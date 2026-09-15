namespace Modbot.Core.Data.Entities;

/// <summary>
/// One AI request's token counts, for spend limits and cost estimates by feature.
/// </summary>
/// <remarks>
/// Shared by every AI feature. Written after a request comes back with counts; a request that
/// failed before the provider counted anything writes nothing.
/// </remarks>
public class AiUsage
{
    public long Id { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>Which feature asked, e.g. <c>moderation</c>, <c>chat</c>, <c>insights</c>.</summary>
    public string Feature { get; set; } = string.Empty;

    /// <summary>The Modbot account the request was for, or null when Modbot asked on its own.</summary>
    public Guid? UserId { get; set; }

    public string Model { get; set; } = string.Empty;

    public string? Provider { get; set; }

    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    /// <summary>Input tokens the provider served from its cache. Included in <see cref="InputTokens"/>.</summary>
    public int CachedInputTokens { get; set; }
}

/// <summary>
/// A feature's spend limit. No row, or a null limit, means no limit.
/// </summary>
/// <remarks>
/// Counted in tokens per UTC calendar month for now, because Modbot has no price list yet. The
/// settings screen for limits comes later; every feature already asks through
/// <c>IAiUsage.LimitReachedAsync</c>, so changing how a limit is counted changes one place.
/// </remarks>
public class AiFeatureLimit
{
    public string Feature { get; set; } = string.Empty;

    /// <summary>Input plus output tokens allowed per UTC calendar month.</summary>
    public long? MonthlyTokenLimit { get; set; }
}
