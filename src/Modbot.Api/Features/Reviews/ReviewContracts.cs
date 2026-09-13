using System.Text.Json;
using Modbot.Analytics.Reviews;
using Modbot.Api.Features.Analytics;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Reviews;

/// <summary>One check that can open a review, with its rule in words.</summary>
/// <param name="Signal">The stored key, e.g. <c>same-person</c>.</param>
/// <param name="Label">The heading the page shows.</param>
/// <param name="Rule">When it opens, with the current thresholds in the sentence.</param>
public sealed record SignalInfo(string Signal, string Label, string Rule);

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
    string? Note);

/// <param name="OpenCount">Every open review, whatever this page was filtered to.</param>
/// <param name="HowUsualIsMeasured">The baseline rule, for the page to show beside "their usual".</param>
/// <param name="LastRunAt">When detection last ran. Null means never -- there is nothing to read into an empty list yet.</param>
public sealed record ReviewListResponse(
    IReadOnlyList<ReviewView> Reviews,
    int OpenCount,
    IReadOnlyList<SignalInfo> Signals,
    string HowUsualIsMeasured,
    DateTimeOffset? LastRunAt,
    DateTimeOffset Now);

public sealed record OpenReviewCount(int Open);

/// <param name="Note">Required. What the person who looked at it concluded, in their words.</param>
public sealed record CloseReviewRequest(string Note);

/// <summary>The words for each signal, and the rule each one follows at the current thresholds.</summary>
public static class ReviewSignals
{
    public static string Label(string signal) => signal switch
    {
        ReviewSignal.SamePerson => "Keeps acting on one person",
        ReviewSignal.FarAboveTeam => "Far more actions than the rest of the team",
        _ => signal,
    };

    public static IReadOnlyList<SignalInfo> Describe(ReviewThresholds t) =>
    [
        new(
            ReviewSignal.SamePerson,
            Label(ReviewSignal.SamePerson),
            $"Opens when one moderator acts on the same person {t.SamePersonActions} or more times in "
            + $"{t.SamePersonDays} days, across at least two different instances or days, and no other "
            + $"moderator has ever acted on that person -- or {t.SamePersonActionsWhenOthersActed} or more "
            + "times whether or not anybody else has."),
        new(
            ReviewSignal.FarAboveTeam,
            Label(ReviewSignal.FarAboveTeam),
            $"Opens when a moderator does {t.FarAboveTeamMinActions} or more actions in one day (UTC) and "
            + $"that is at least {t.FarAboveTeamMultiplier:0.#} times both the next busiest moderator's count "
            + $"that day and the team's usual per moderator per day. Waits until the team has "
            + $"{t.FarAboveTeamMinTeamDays} days of history."),
    ];
}
