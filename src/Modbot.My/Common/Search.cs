namespace Modbot.My.Common;

public static class Search
{
    /// <summary>
    /// An <c>ILIKE</c> pattern matching text that contains <paramref name="term"/> as typed, so a
    /// <c>%</c> or <c>_</c> in the search box matches itself.
    /// </summary>
    public static string Contains(string term)
    {
        ArgumentNullException.ThrowIfNull(term);

        var escaped = term.Trim()
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

        return $"%{escaped}%";
    }
}
