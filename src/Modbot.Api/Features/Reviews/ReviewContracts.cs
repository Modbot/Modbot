using System.Text.Json;
using Modbot.Analytics.Reviews;
using Modbot.Api.Features.Analytics;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Reviews;

/// <param name="AboutPerson">For a same-person review, the person; null for a day review.</param>
/// <param name="Evidence">Every number the review was opened on and the fact ids behind them.</param>
/// <param name="State"><c>Open</c> or <c>Closed</c>.</param>
public sealed record ReviewView(
    Guid Id,
    Person Moderator,
    string Signal,
    string SignalLabel,
    string About,
    Person? AboutPerson,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    string Summary,
    JsonElement Evidence,
    string State,
    DateTimeOffset OpenedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt,
    string? ClosedByUsername,
    string? Note,
    string? Outcome = null);

/// <param name="OpenCount">Every open review, whatever this page was filtered to.</param>
/// <param name="LastRunAt">When detection last ran. Null means never -- there is nothing to read into an empty list yet.</param>
public sealed record ReviewListResponse(
    IReadOnlyList<ReviewView> Reviews,
    int OpenCount,
    DateTimeOffset? LastRunAt,
    DateTimeOffset Now);

public sealed record OpenReviewCount(int Open);

/// <param name="Note">Required. What the person who looked at it concluded, in their words.</param>
/// <param name="Outcome">
/// <c>right</c> or <c>wrong</c>. Required for a review opened by an AI moderation flag, where wrong
/// dismisses the flag and right confirms it; not accepted for any other kind of review.
/// </param>
public sealed record CloseReviewRequest(string Note, string? Outcome = null);

/// <summary>The words for each signal.</summary>
public static class ReviewSignals
{
    public static string Label(string signal) => signal switch
    {
        ReviewSignal.SamePerson => "Keeps acting on one person",
        ReviewSignal.FarAboveTeam => "Far more actions than the rest of the team",
        ReviewSignal.AiFlag => "Flagged by a moderation rule",
        _ => signal,
    };
}
