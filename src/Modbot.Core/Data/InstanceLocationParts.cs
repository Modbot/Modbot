namespace Modbot.Core.Data;

/// <summary>
/// The world and the instance out of a VRChat location string, by its delimiters and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The grammar (<c>.agent/research/vrchat-log-format.md</c> section 1.2) is
/// <c>&lt;worldId&gt; ":" &lt;instanceId&gt; ( "~" qualifier )*</c>. The fact row has a column for
/// each of the first two, and an audit entry for an instance event carries the whole string --
/// so this takes the string apart far enough to fill those columns and no further.
/// </para>
/// <para>
/// <strong>Delimiters only.</strong> Nothing here checks that the world id starts with
/// <c>wrld_</c>, that the instance id is a number, or that either has a length. Legacy VRChat ids
/// follow no structure (foundation 3.1.1), and a shape check would silently drop the oldest
/// instances while looking correct in every test. When the string has no <c>:</c> at all, the
/// whole of it is kept as the world id rather than guessed at.
/// </para>
/// <para>
/// The client has the same grammar in <c>Modbot.Client.Instances.InstanceLocation</c>, with the
/// qualifiers too. It is not referenced from here on purpose: the sync side needs two columns,
/// not a routing decision, and a project reference for one split is the wrong price.
/// </para>
/// </remarks>
/// <param name="WorldId">Everything before the first <c>:</c>; the whole string when there is none.</param>
/// <param name="InstanceId">
/// Between the first <c>:</c> and the first <c>~</c> after it, or the end. Null when the string has
/// no <c>:</c>, and null when that stretch is empty -- an empty id is nothing to key on.
/// </param>
/// <param name="GroupId">
/// The owning group from <c>~group(grp_...)</c>, or null when the room is not a group instance.
/// </param>
/// <param name="GroupAccessType">
/// How open a group instance is -- <c>members</c>, <c>plus</c>, <c>public</c> -- from
/// <c>~groupAccessType(...)</c>.
/// </param>
/// <param name="Region">VRChat's network region from <c>~region(...)</c>.</param>
public readonly record struct InstanceLocationParts(
    string? WorldId,
    string? InstanceId,
    string? GroupId = null,
    string? GroupAccessType = null,
    string? Region = null)
{
    /// <summary>
    /// What kind of room the location describes, in one word, or null when it does not say.
    /// </summary>
    /// <remarks>
    /// Only <c>group</c> is decided here, because <c>~group(...)</c> is the one qualifier whose
    /// presence is conclusive. A location with no qualifiers at all may be a public instance or
    /// may simply be a location somebody wrote down without them, and guessing between those
    /// would put a wrong word on a screen. Null means "not said", which is true.
    /// </remarks>
    public string? Type => GroupId is null ? null : "group";

    /// <summary>
    /// Splits a location string. Never throws: a shape this does not understand degrades to
    /// "world only" or to nothing, and the verbatim string is still in the payload either way.
    /// </summary>
    public static InstanceLocationParts Split(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return default;

        var colon = location.IndexOf(':');
        if (colon < 0)
            return new InstanceLocationParts(location, null);

        var worldId = location[..colon];
        var rest = location[(colon + 1)..];

        // VRChat gives the instance id no escaping, so one containing '~' cannot be recovered;
        // stopping at the first '~' is consistent with VRChat and with the client.
        var tilde = rest.IndexOf('~');
        var instanceId = tilde < 0 ? rest : rest[..tilde];

        string? groupId = null;
        string? groupAccessType = null;
        string? region = null;

        if (tilde >= 0)
        {
            foreach (var segment in rest[(tilde + 1)..].Split('~', StringSplitOptions.RemoveEmptyEntries))
            {
                var (name, value) = SplitQualifier(segment);

                switch (name)
                {
                    case "group": groupId = value; break;
                    case "groupAccessType": groupAccessType = value; break;
                    case "region": region = value; break;
                }
            }
        }

        return new InstanceLocationParts(
            worldId.Length == 0 ? null : worldId,
            instanceId.Length == 0 ? null : instanceId,
            groupId,
            groupAccessType,
            region);
    }

    /// <summary>
    /// Splits <c>group(grp_...)</c> into a name and a value, and a bare <c>ageGate</c> into a name
    /// with none.
    /// </summary>
    /// <remarks>
    /// Anchored on the <em>last</em> <c>)</c> rather than the first, for the same reason the log
    /// parser anchors on the last bracket: the text inside is not Modbot's, and a value that
    /// itself contains brackets must not be able to truncate what is read.
    /// </remarks>
    private static (string Name, string? Value) SplitQualifier(string segment)
    {
        var open = segment.IndexOf('(');
        if (open < 0 || segment.Length == 0 || segment[^1] != ')')
            return (segment, null);

        var value = segment[(open + 1)..^1];
        return (segment[..open], value.Length == 0 ? null : value);
    }
}
