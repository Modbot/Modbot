namespace Modbot.Core.Calendar;

/// <summary>
/// Finds the world id in what a person pasted: a world's page on vrchat.com, a launch link, or the
/// id itself (world lists design §2).
/// </summary>
/// <remarks>
/// The id is never checked for shape (foundation §3.1.1): a link is only taken apart to find where
/// the id sits in it, and anything that is not a link is taken as the id exactly as typed.
/// </remarks>
public static class WorldLinks
{
    /// <summary>
    /// The world id in <paramref name="text"/>: the <c>worldId</c> of a link's query, or the part after
    /// <c>/world/</c> in its path; otherwise the text itself, trimmed. Null for nothing, or for a link
    /// with no world in it.
    /// </summary>
    public static string? WorldIdFrom(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var link)
            || (link.Scheme != Uri.UriSchemeHttps && link.Scheme != Uri.UriSchemeHttp))
        {
            return trimmed;
        }

        foreach (var pair in link.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
                continue;

            if (string.Equals(Uri.UnescapeDataString(pair[..equals]), "worldId", StringComparison.OrdinalIgnoreCase))
            {
                var value = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' ')).Trim();
                if (value.Length > 0)
                    return value;
            }
        }

        var parts = link.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (string.Equals(parts[i], "world", StringComparison.OrdinalIgnoreCase))
            {
                var value = Uri.UnescapeDataString(parts[i + 1]).Trim();
                if (value.Length > 0)
                    return value;
            }
        }

        return null;
    }
}
