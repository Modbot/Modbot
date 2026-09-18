using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Analytics.DailyTotals;
using Modbot.Core.Data.Entities;

namespace Modbot.Analytics.Reviews;

/// <summary>
/// The numbers repeat-offender status and the moderator pattern checks are decided on.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.8.5: thresholds are configurable, with conservative defaults, and conservative in one
/// direction on purpose -- wrongly asking a volunteer to explain a busy night is corrosive in a
/// way a missed pattern is not. Every default here errs towards not opening a review.
/// </para>
/// <para>
/// Stored on the settings row as sparse JSON (<c>Settings.ReviewThresholds</c>). A field that is
/// absent takes the default below, so a deployment that never changed a number picks up a revised
/// default on upgrade. <see cref="Clamped"/> runs on every read so a hand-edited row cannot set a
/// threshold to zero and turn detection into an accusation generator.
/// </para>
/// </remarks>
public sealed record ReviewThresholds
{
    /// <summary>
    /// Actions against a person in the last 30 days that make them a repeat offender. The window
    /// is fixed at 30 days because that is the number moderators say ("fourth kick in thirty
    /// days") and a second knob would only make the status harder to read.
    /// </summary>
    public int RepeatOffenderActionsIn30Days { get; init; } = 3;

    /// <summary>
    /// Which kinds of action count towards that number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null means every kind, which is what <see cref="ActionsOnPeople.Types"/> lists and what
    /// every deployment counted before this was configurable -- so a settings row written before
    /// it existed, and an operator who never opens the card, both see exactly the numbers they
    /// saw yesterday.
    /// </para>
    /// <para>
    /// Groups do not agree on what a strike is. A group that throws people out of an instance to
    /// break up an argument does not think of that as a mark against anybody, and counting it
    /// alongside bans makes their busiest regulars look like their worst. Which kinds count is
    /// theirs to say; the threshold on its own could not express it.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string>? RepeatOffenderTypes { get; init; }

    /// <summary>
    /// How many times one moderator has to act on one person, across at least two instances or
    /// days, before a review opens -- when nobody else has ever acted on that person.
    /// </summary>
    public int SamePersonActions { get; init; } = 3;

    /// <summary>
    /// The same, when other moderators <em>have</em> acted on the person. Much higher, because a
    /// user everybody is removing is the case the spec says must not look like a grudge.
    /// </summary>
    public int SamePersonActionsWhenOthersActed { get; init; } = 8;

    /// <summary>How far back the same-person check counts.</summary>
    public int SamePersonDays { get; init; } = 30;

    /// <summary>The fewest actions in one UTC day that can open a volume review at all.</summary>
    public int FarAboveTeamMinActions { get; init; } = 10;

    /// <summary>
    /// The day's count must be at least this many times the larger of: the next busiest
    /// moderator that day, and the team's usual per moderator per active day. Comparing against
    /// the next busiest is what keeps a raid night -- when everybody is busy -- from opening a
    /// review on whoever happened to be busiest.
    /// </summary>
    public decimal FarAboveTeamMultiplier { get; init; } = 4;

    /// <summary>
    /// The volume check does not run until the team has this many days with recorded actions in
    /// the baseline window. There is no "usual" to compare against on day one.
    /// </summary>
    public int FarAboveTeamMinTeamDays { get; init; } = 7;

    /// <summary>How many days back "usual" is measured over, ending yesterday.</summary>
    public int BaselineDays { get; init; } = 90;

    public static ReviewThresholds Default { get; } = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Reads the stored document, defaults for anything missing, and clamps.</summary>
    public static ReviewThresholds Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Default;

        try
        {
            return (JsonSerializer.Deserialize<ReviewThresholds>(json, Json) ?? Default).Clamped();
        }
        catch (JsonException)
        {
            // A malformed row must not stop detection; the defaults are the safe answer.
            return Default;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Keeps every number inside a range where the check still means something. A threshold of
    /// zero would open a review on every action; a multiplier below one would fire on an ordinary day.
    /// </summary>
    /// <summary>
    /// The kinds of action that count, resolved: the chosen ones, or every kind when nothing was
    /// chosen. Never empty -- a list nobody can be counted under would freeze every status at
    /// "once", which is worse than the default and harder to notice.
    /// </summary>
    public IReadOnlyList<string> CountedTypes
    {
        get
        {
            var chosen = RepeatOffenderTypes?
                .Where(t => ActionsOnPeople.Types.Contains(t, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return chosen is { Length: > 0 } ? chosen : ActionsOnPeople.Types;
        }
    }

    public ReviewThresholds Clamped() => this with
    {
        RepeatOffenderActionsIn30Days = Math.Clamp(RepeatOffenderActionsIn30Days, 2, 100),
        SamePersonActions = Math.Clamp(SamePersonActions, 2, 100),
        SamePersonActionsWhenOthersActed = Math.Max(Math.Clamp(SamePersonActionsWhenOthersActed, 2, 1000), SamePersonActions),
        SamePersonDays = Math.Clamp(SamePersonDays, 1, 365),
        FarAboveTeamMinActions = Math.Clamp(FarAboveTeamMinActions, 2, 10_000),
        FarAboveTeamMultiplier = Math.Clamp(FarAboveTeamMultiplier, 1.5m, 100m),
        FarAboveTeamMinTeamDays = Math.Clamp(FarAboveTeamMinTeamDays, 1, 365),
        BaselineDays = Math.Clamp(BaselineDays, 7, 365),
    };
}

/// <summary>
/// The facts that count as a moderator acting on a person, for both halves of spec 5.8.
/// </summary>
/// <remarks>
/// Instance kicks, warns, bans, removals from the group, and join requests turned away. Not
/// unbans (relief), not invites or approvals (welcomes), not role changes (administration): none
/// of those is a strike against anybody, and counting them would make a moderator who runs the
/// door look like one who runs people out of it.
/// </remarks>
public static class ActionsOnPeople
{
    public static readonly string[] Types =
    [
        FactType.GroupInstanceKick,
        FactType.GroupInstanceWarn,
        FactType.MemberBanned,
        FactType.MemberKicked,
        FactType.JoinRequestRejected,
        FactType.JoinRequestBlocked,
    ];

    /// <summary>The same actions as the daily totals count them, for the baselines.</summary>
    public static readonly string[] BaselineMetrics =
    [
        DailyTotalMetrics.ModeratorInstanceKicks,
        DailyTotalMetrics.ModeratorWarns,
        DailyTotalMetrics.ModeratorBans,
        DailyTotalMetrics.ModeratorRemovals,
        DailyTotalMetrics.ModeratorRejections,
    ];

    /// <summary>Plain words for a count of each kind, in the order a sentence lists them.</summary>
    public static string Describe(int instanceKicks, int warns, int bans, int removals, int rejections)
    {
        var parts = new List<string>();

        void Add(int n, string one, string many)
        {
            if (n > 0) parts.Add($"{n} {(n == 1 ? one : many)}");
        }

        Add(instanceKicks, "instance kick", "instance kicks");
        Add(warns, "warn", "warns");
        Add(bans, "ban", "bans");
        Add(removals, "removal from the group", "removals from the group");
        Add(rejections, "join request turned away", "join requests turned away");

        return string.Join(", ", parts);
    }
}
