namespace Modbot.My.Common;

/// <summary>One page of a list, with the total so a caller knows when to stop.</summary>
public sealed record Page<T>(int Total, int Offset, int Limit, IReadOnlyList<T> Items);

public static class Paging
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 1000;

    public static (int Offset, int Limit) Clamp(int? offset, int? limit) =>
        (Math.Max(0, offset ?? 0), Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit));
}
