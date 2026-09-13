namespace Modbot.VRChat.Sync;

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
public readonly record struct InstanceLocationParts(string? WorldId, string? InstanceId)
{
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

        return new InstanceLocationParts(
            worldId.Length == 0 ? null : worldId,
            instanceId.Length == 0 ? null : instanceId);
    }
}
