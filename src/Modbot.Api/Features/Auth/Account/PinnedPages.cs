namespace Modbot.Api.Features.Auth.Account;

/// <summary>
/// The list of pages a person pinned in the menu, checked before it is kept.
/// </summary>
/// <remarks>
/// The server does not know which pages exist: the web app names them and may add or drop one
/// without a change here, so a name it no longer knows is kept and the app skips it when it reads
/// the list. What is checked is only that the list is a short list of short names, so the column
/// cannot be used to store anything else.
/// </remarks>
public static class PinnedPages
{
    /// <summary>More than the app has pages, so a person is never stopped from pinning one.</summary>
    public const int MostPages = 60;

    /// <summary>Longer than any page name.</summary>
    public const int LongestName = 48;

    /// <summary>
    /// The names as they will be kept: trimmed, in the order sent, each once. Null when the list
    /// is missing, too long, or holds a name that is empty, too long or not a page-name shape
    /// (lower-case letters, digits and dashes).
    /// </summary>
    public static List<string>? Clean(IReadOnlyList<string>? pages)
    {
        if (pages is null || pages.Count > MostPages)
            return null;

        var kept = new List<string>(pages.Count);
        foreach (var raw in pages)
        {
            var name = raw?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > LongestName || !name.All(IsNameCharacter))
                return null;

            if (!kept.Contains(name))
                kept.Add(name);
        }

        return kept;
    }

    private static bool IsNameCharacter(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-';
}
