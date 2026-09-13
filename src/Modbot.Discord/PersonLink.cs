namespace Modbot.Discord;

/// <summary>
/// The link to a person in the Modbot web app, built from the public address setting and
/// nothing else.
/// </summary>
/// <remarks>
/// The same rule the reset-link sender follows (accounts and access design §4.2): a link Modbot
/// sends out is built from the address a human typed and confirmed, never from a request's
/// host or a forwarded header. No public address means no link, and the message says so
/// rather than guessing.
/// </remarks>
public static class PersonLink
{
    public static string? For(string? publicAddress, string vrchatUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vrchatUserId);

        if (string.IsNullOrWhiteSpace(publicAddress))
            return null;

        return $"{publicAddress.TrimEnd('/')}/audit?subject={Uri.EscapeDataString(vrchatUserId)}";
    }
}
