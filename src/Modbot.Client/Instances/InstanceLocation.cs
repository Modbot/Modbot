namespace Modbot.Client.Instances;

/// <summary>
/// A VRChat location string, taken apart into the pieces Modbot is allowed to keep.
/// </summary>
/// <remarks>
/// <para>The grammar, from <c>.agent/research/vrchat-log-format.md</c>:</para>
/// <code>
/// wrld_4b34…:39911~group(grp_2d8c…)~groupAccessType(members)~ageGate~region(use)
/// └ world ┘ └inst ┘ └──────────────────── qualifiers ────────────────────────┘
/// </code>
/// <para><strong>Nothing here reaches a server as written.</strong> The raw location string is
/// deliberately not a property of this type, because for non-group instances it carries
/// <c>~nonce(…)</c> — the instance secret. Modbot must never persist or transmit that, so it is
/// discarded at the point of parsing rather than filtered out later by code somebody might
/// forget to write.</para>
/// <para><strong>Identity is world plus instance, and neither alone.</strong> Instance ids are
/// unique within a world, not globally.</para>
/// </remarks>
public sealed record InstanceLocation
{
    private readonly Dictionary<string, string?> _qualifiers;

    private InstanceLocation(string worldId, string instanceId, Dictionary<string, string?> qualifiers)
    {
        WorldId = worldId;
        InstanceId = instanceId;
        _qualifiers = qualifiers;
    }

    /// <summary>The world — the Unity content. Opaque; never validated for shape.</summary>
    public string WorldId { get; }

    /// <summary>
    /// One running session of that world. Usually a VRChat-assigned number, but it is
    /// <strong>arbitrary user-controlled text</strong> and groups routinely set it to something
    /// readable. Treat it as hostile input wherever it is displayed: it may contain Discord
    /// mentions, markdown, or bidi overrides.
    /// </summary>
    public string InstanceId { get; }

    /// <summary>
    /// The owning group, or <c>null</c> when this is not a group instance. This one field is the
    /// entire routing decision, and it is made here, locally, with no network call — see
    /// <see cref="IsGroupInstance"/>.
    /// </summary>
    public string? GroupId => Qualifier("group");

    /// <summary><c>public</c>, <c>plus</c>, <c>members</c> — display and policy context only.</summary>
    public string? GroupAccessType => Qualifier("groupAccessType");

    /// <summary>VRChat's network region: <c>use</c>, <c>usw</c>, <c>eu</c>, <c>jp</c>, …</summary>
    public string? Region => Qualifier("region");

    /// <summary>
    /// Whether this is a group instance at all.
    /// </summary>
    /// <remarks>
    /// An instance with no <c>~group(…)</c> qualifier is not a group instance and is dropped before
    /// transmission. That is what excludes a moderator's own private, friends-only and public
    /// VRChat use by structure rather than by heuristic — M3 5.5.1.
    /// </remarks>
    public bool IsGroupInstance => GroupId is not null;

    /// <summary>
    /// The qualifier names present, with <c>nonce</c> already removed. Useful for noticing a
    /// qualifier VRChat has started emitting that Modbot does not yet understand.
    /// </summary>
    public IReadOnlyCollection<string> QualifierNames => _qualifiers.Keys;

    public string? Qualifier(string name)
        => _qualifiers.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// Splits a location string. Returns <c>false</c> for anything that is not a world and an
    /// instance; it never throws, because a format change must degrade to "not understood" rather
    /// than take the client down.
    /// </summary>
    public static bool TryParse(string? raw, out InstanceLocation location)
    {
        location = null!;

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var colon = raw.IndexOf(':');
        if (colon <= 0)
            return false;

        var worldId = raw[..colon];
        var rest = raw[(colon + 1)..];

        // The instance id runs to the first qualifier. VRChat's own encoding gives it no escaping,
        // so an instance id containing '~' would be genuinely ambiguous -- there is no reading
        // that recovers it, and guessing would be worse than being consistent with VRChat.
        var firstQualifier = rest.IndexOf('~');
        var instanceId = firstQualifier < 0 ? rest : rest[..firstQualifier];
        if (instanceId.Length == 0)
            return false;

        var qualifiers = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (firstQualifier >= 0)
        {
            foreach (var segment in rest[(firstQualifier + 1)..].Split('~', StringSplitOptions.RemoveEmptyEntries))
            {
                var (name, value) = SplitQualifier(segment);

                // The instance secret. Dropped here, at the point it is read, so that no later
                // code can store it, log it or send it -- not even by mistake. Format research
                // section 1.3.4.
                if (string.Equals(name, "nonce", StringComparison.Ordinal))
                    continue;

                qualifiers[name] = value;
            }
        }

        location = new InstanceLocation(worldId, instanceId, qualifiers);
        return true;
    }

    /// <summary>
    /// <c>group(grp_…)</c> into a name and a value; <c>ageGate</c> into a name and no value.
    /// Values are taken between the delimiters and never matched against an expected id shape —
    /// legacy VRChat ids have no shape. Foundation 3.1.1.
    /// </summary>
    private static (string Name, string? Value) SplitQualifier(string segment)
    {
        var open = segment.IndexOf('(');
        if (open < 0 || segment[^1] != ')')
            return (segment, null);

        return (segment[..open], segment[(open + 1)..^1]);
    }

    /// <summary>
    /// Deliberately prints identity only. A location is the one string in the client that may
    /// contain a secret, and <c>ToString</c> is exactly how such a thing ends up in a log file.
    /// </summary>
    public override string ToString() => $"{WorldId}:{InstanceId}";

    public bool Equals(InstanceLocation? other)
        => other is not null
           && string.Equals(WorldId, other.WorldId, StringComparison.Ordinal)
           && string.Equals(InstanceId, other.InstanceId, StringComparison.Ordinal)
           && GroupId == other.GroupId;

    public override int GetHashCode() => HashCode.Combine(WorldId, InstanceId, GroupId);
}
