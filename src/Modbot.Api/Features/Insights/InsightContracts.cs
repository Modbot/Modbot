using Modbot.AI.Insights;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Insights;

/// <summary>One stored insight, with the figures it was written from.</summary>
/// <param name="StartedBy"><c>schedule</c> or <c>button</c>.</param>
/// <param name="RequestedBy">Who pressed Generate now, or null for a scheduled one.</param>
/// <param name="Text">What the model wrote. Null when the attempt failed.</param>
/// <param name="Error">Why the attempt failed.</param>
/// <param name="Figures">Exactly what the model was given. Null only for a row whose figures cannot be read.</param>
/// <param name="DiscordError">Why posting to Discord failed for good, if it did.</param>
public sealed record InsightView(
    Guid Id,
    string Kind,
    string Label,
    DateOnly FirstDay,
    DateOnly LastDay,
    DateTimeOffset CreatedAt,
    string StartedBy,
    string? RequestedBy,
    string? Model,
    string? Text,
    string? Error,
    InsightFigures? Figures,
    DateTimeOffset? DiscordPostedAt,
    string? DiscordError)
{
    public static InsightView From(Insight insight)
    {
        ArgumentNullException.ThrowIfNull(insight);

        return new InsightView(
            insight.Id,
            insight.Kind,
            InsightKinds.Label(insight.Kind),
            insight.FirstDay,
            insight.LastDay,
            insight.CreatedAt,
            insight.StartedBy,
            insight.RequestedByUsername,
            insight.Model,
            insight.Text,
            insight.Error,
            InsightFigures.FromJson(insight.Figures),
            insight.DiscordPostedAt,
            insight.DiscordError);
    }
}

public sealed record InsightPage(IReadOnlyList<InsightView> Insights);

/// <summary>One kind's schedule, as stored.</summary>
/// <param name="Every"><c>day</c> or <c>week</c>.</param>
/// <param name="Hour">0 to 23, in the time zone.</param>
/// <param name="Weekday">0 is Sunday.</param>
/// <param name="Last">The most recent attempt of this kind, written or failed, or null.</param>
public sealed record InsightKindSettings(
    string Kind,
    string Label,
    bool Enabled,
    string Every,
    int Hour,
    int Weekday,
    string? DiscordChannelId,
    InsightView? Last);

/// <param name="TimeZone">An IANA name, or null for UTC.</param>
/// <param name="Model">The model insights use, or null for <paramref name="BaseModel"/>.</param>
/// <param name="AiOn">Whether AI is switched on and set up on Base. Nothing is written while it is not.</param>
public sealed record AiInsightsSettingsResponse(
    string? TimeZone,
    string? Model,
    string? BaseModel,
    bool AiOn,
    IReadOnlyList<InsightKindSettings> Kinds);

public sealed record InsightKindUpdate(
    string Kind,
    bool Enabled,
    string Every,
    int Hour,
    int Weekday,
    string? DiscordChannelId);

/// <param name="Kinds">Kinds left out keep what they had.</param>
public sealed record AiInsightsSettingsUpdate(
    string? TimeZone,
    string? Model,
    IReadOnlyList<InsightKindUpdate>? Kinds);
