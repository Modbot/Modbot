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

    /// <summary>
    /// What the provider said the request cost, in US dollars, when it said. Only OpenRouter does;
    /// every other row is priced from the price list when it is read.
    /// </summary>
    public decimal? ReportedCost { get; set; }
}

/// <summary>
/// A feature's monthly limit in tokens, from before Modbot had prices. No row, or a null limit,
/// means no limit.
/// </summary>
/// <remarks>
/// Limits are money now (<see cref="AiSpendLimit"/>). A token limit is changed into a money limit
/// once the feature's model has a price; until then it is kept, still counted, and shown as a
/// token limit (AI chat design §10.5). Nothing makes new ones.
/// </remarks>
public class AiFeatureLimit
{
    public string Feature { get; set; } = string.Empty;

    /// <summary>Input plus output tokens allowed per UTC calendar month.</summary>
    public long? MonthlyTokenLimit { get; set; }
}
