namespace Modbot.Cloud.Common;

/// <summary>One page of a list, the same shape wherever Cloud pages something.</summary>
public sealed record Page<T>(int Total, int Offset, int Limit, IReadOnlyList<T> Items);

/// <summary>Turns whatever a caller asked for into a page size the server is willing to serve.</summary>
public static class Paging
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static (int Skip, int Take) Clamp(int? offset, int? limit) => (
        Math.Max(0, offset ?? 0),
        Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit));
}

/// <summary>Search text as a <c>LIKE</c> pattern, with the wildcards a person typed made literal.</summary>
public static class Search
{
    public static string Contains(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var escaped = text.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

        return $"%{escaped}%";
    }
}
