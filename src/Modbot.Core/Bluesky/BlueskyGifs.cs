using Modbot.Core.Data.Entities;

namespace Modbot.Core.Bluesky;

/// <summary>
/// A GIF on a Bluesky post, picked from Klipy in the calendar form. It goes out the way Bluesky's own app
/// posts one: a link card (<c>app.bsky.embed.external</c>) whose address is the GIF's Klipy address, with
/// no picture uploaded. Bluesky's apps read the size and the file names off that address.
/// </summary>
/// <remarks>
/// Klipy's rules for an app that shows its GIFs (docs.klipy.com, integration requirements): the browser
/// searches and loads the GIFs itself, and nothing of a GIF is kept but its address. So Modbot keeps and
/// sends the address, and refuses any other.
/// </remarks>
public static class BlueskyGifs
{
    /// <summary>The host Klipy serves its GIFs from.</summary>
    public const string MediaHost = "static.klipy.com";

    /// <summary>The words for a GIF address that is not on Klipy's media host.</summary>
    public const string NotKlipy = "A Bluesky GIF must be a Klipy address (https://static.klipy.com/...).";

    /// <summary>The longest a GIF address may be: the column's.</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>
    /// What is wrong with a GIF address, or null: it must be an https address on Klipy's media host, with no user
    /// name, password or port, a path under <c>/ii/</c>, and the height and width (<c>hh</c> and <c>ww</c>) in its
    /// query, which Bluesky's apps need to read it as a GIF.
    /// </summary>
    public static string? Problem(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength)
            return NotKlipy;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var address))
            return NotKlipy;

        var ok = address.Scheme == Uri.UriSchemeHttps
            && string.Equals(address.Host, MediaHost, StringComparison.OrdinalIgnoreCase)
            && address.IsDefaultPort
            && string.IsNullOrEmpty(address.UserInfo)
            && address.AbsolutePath.StartsWith("/ii/", StringComparison.Ordinal)
            && address.AbsolutePath.Length > "/ii/".Length
            && HasQueryName(address, "hh")
            && HasQueryName(address, "ww");

        return ok ? null : NotKlipy;
    }

    /// <summary>
    /// The link card a GIF is posted as, worded the way Bluesky's app words one: the address, and the line "Alt: "
    /// and the alt text. With no alt text the line is "ALT: " and the post's title (or "GIF" when that is empty
    /// too), never a bare "Alt: ".
    /// </summary>
    public static BlueskyCard Card(BlueskyPostGif gif, string? postTitle = null)
    {
        ArgumentNullException.ThrowIfNull(gif);

        var alt = gif.Alt?.Trim() ?? string.Empty;

        if (alt.Length > 0)
            return new BlueskyCard(gif.Url, alt, "Alt: " + alt);

        var fallback = string.IsNullOrWhiteSpace(postTitle) ? "GIF" : postTitle.Trim();

        return new BlueskyCard(gif.Url, fallback, "ALT: " + fallback);
    }

    private static bool HasQueryName(Uri address, string name) =>
        address.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Split('=')[0] == name);
}
