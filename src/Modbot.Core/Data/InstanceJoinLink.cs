namespace Modbot.Core.Data;

/// <summary>
/// VRChat's launch page for a room:
/// <c>https://vrchat.com/home/launch?worldId=wrld_…&amp;instanceId=26093~group(grp_…)~groupAccessType(plus)~region(us)</c>.
/// </summary>
/// <remarks>
/// In Core rather than beside the instance card because the calendar puts the same link on a
/// Discord event and a channel post, and the API shows it on the calendar page.
/// </remarks>
public static class InstanceJoinLink
{
    /// <summary>The longest address Discord accepts for a link button.</summary>
    public const int MaxLength = 512;

    /// <summary>
    /// The link for a location. <c>instanceId</c> is everything after the first <c>:</c>,
    /// qualifiers and all, because the qualifiers are part of which room it is. The characters
    /// VRChat writes in a location (<c>~</c>, <c>(</c>, <c>)</c>) are kept, and anything that could
    /// end or split the query string is escaped.
    /// </summary>
    /// <returns>Null when the location has no instance part, or the link would be longer than <see cref="MaxLength"/>.</returns>
    public static string? For(string? location, string? worldId = null)
    {
        if (string.IsNullOrEmpty(location))
            return null;

        var colon = location.IndexOf(':');
        if (colon < 0 || colon == location.Length - 1)
            return null;

        var world = worldId is { Length: > 0 } w ? w : location[..colon];
        var instanceId = location[(colon + 1)..];

        var link = $"https://vrchat.com/home/launch?worldId={QueryValue(world)}&instanceId={QueryValue(instanceId)}";

        // Discord refuses the whole message when a link button's address is too long, so a room with
        // a very long custom instance id goes without the link rather than without the message.
        return link.Length <= MaxLength ? link : null;
    }

    private static string QueryValue(string value) =>
        Uri.EscapeDataString(value).Replace("%28", "(", StringComparison.Ordinal).Replace("%29", ")", StringComparison.Ordinal);
}
