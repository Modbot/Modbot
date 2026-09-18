using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Analytics.Reviews;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;

namespace Modbot.Analytics.Giveaways;

/// <summary>What a rule or a weight needs counted, and over what stretch of time.</summary>
/// <param name="Kind">One of <see cref="GiveawayRuleKinds"/> or <see cref="GiveawayWeights"/>.</param>
/// <param name="WithinDays">The last so many days, or null for all of recorded history.</param>
public readonly record struct GiveawayMeasure(string Kind, int? WithinDays)
{
    /// <summary>True when this number comes from polled presence reports rather than exact facts.</summary>
    public bool FromPolledData => Kind is GiveawayRuleKinds.InstanceHours
        or GiveawayRuleKinds.OneInstanceHours
        or GiveawayRuleKinds.SeenWithinDays
        or GiveawayWeights.DaysSeen;
}

/// <summary>
/// Every number the rules and the weighting need, counted once for everybody.
/// </summary>
/// <remarks>
/// <para>
/// One query per distinct measurement rather than one per person: a giveaway with four rules over
/// ten thousand candidates is four statements, not forty thousand. M7 §5 calls segment evaluation
/// the heaviest read in Modbot, and the shape of the reads is most of what decides that.
/// </para>
/// <para>
/// <strong>Daily totals wherever a daily total answers the question</strong> (§5). Voice hours and
/// message counts are sums of <c>discord.member.voice-minutes</c> and
/// <c>discord.member.messages</c>, which already exist per member per day; only the presence
/// numbers, which no per-person daily total covers yet, are counted from facts.
/// </para>
/// </remarks>
public sealed class GiveawayMeasures
{
    /// <summary>
    /// How near a threshold a measured number has to be to count as a close call.
    /// </summary>
    /// <remarks>
    /// A tenth of the threshold, never less than half an hour. Presence is sampled by whichever
    /// moderator happened to be in the instance, so a figure within a tenth of the line could as
    /// easily be on the other side of it, and a page that printed it as though it settled the
    /// matter would be inventing precision (M7 §2.3).
    /// </remarks>
    public static decimal CloseMargin(decimal threshold) => Math.Max(0.5m, Math.Abs(threshold) / 10m);

    /// <summary>Whether a measured number sits within a whisker of the threshold.</summary>
    public static bool IsCloseCall(decimal measured, decimal threshold)
        => Math.Abs(measured - threshold) <= CloseMargin(threshold);

    private readonly Dictionary<GiveawayMeasure, Dictionary<string, decimal>> _counted = [];

    /// <summary>What one person's number is, or zero when nothing was recorded.</summary>
    public decimal Of(GiveawayMeasure measure, GiveawayCandidate person)
    {
        ArgumentNullException.ThrowIfNull(person);

        if (!_counted.TryGetValue(measure, out var byPerson))
            return 0m;

        var vrchat = person.VRChatUserId;
        var discord = person.DiscordUserId;

        // Presence is recorded about a VRChat account; voice and messages about a Discord one.
        // A person who has only the wrong half of the pair reads as nought, which is the honest
        // answer and is why `linkedAccounts` exists as a rule.
        return measure.Kind switch
        {
            GiveawayRuleKinds.InstanceHours or GiveawayRuleKinds.OneInstanceHours
                or GiveawayRuleKinds.SeenWithinDays or GiveawayWeights.DaysSeen =>
                vrchat is not null && byPerson.TryGetValue(vrchat, out var seen) ? seen : 0m,
            _ => discord is not null && byPerson.TryGetValue(discord, out var said) ? said : 0m,
        };
    }

    /// <summary>Every measurement a rule tree and a weighting between them need.</summary>
    public static IReadOnlyList<GiveawayMeasure> Needed(GiveawayRule rule, string weighting)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var needs = new List<GiveawayMeasure>();
        Collect(rule, needs);

        if (weighting != GiveawayWeights.Uniform)
        {
            var kind = weighting switch
            {
                GiveawayWeights.InstanceHours => GiveawayRuleKinds.InstanceHours,
                GiveawayWeights.VoiceHours => GiveawayRuleKinds.VoiceHours,
                GiveawayWeights.Messages => GiveawayRuleKinds.Messages,
                _ => GiveawayWeights.DaysSeen,
            };

            Add(needs, new GiveawayMeasure(kind, null));
        }

        return needs;
    }

    /// <summary>Counts every measurement for the people given, and keeps the answers.</summary>
    public async Task CountAsync(
        ModbotContext db,
        IReadOnlyList<GiveawayCandidate> people,
        IReadOnlyList<GiveawayMeasure> needed,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(needed);

        var vrchatIds = people.Where(p => p.VRChatUserId is not null).Select(p => p.VRChatUserId!).Distinct().ToArray();
        var discordIds = people.Where(p => p.DiscordUserId is not null).Select(p => p.DiscordUserId!).Distinct().ToArray();

        foreach (var measure in needed)
        {
            ct.ThrowIfCancellationRequested();

            _counted[measure] = measure.Kind switch
            {
                GiveawayRuleKinds.InstanceHours => await PresenceAsync(db, vrchatIds, measure, now, Presence.TotalHours, ct),
                GiveawayRuleKinds.OneInstanceHours => await PresenceAsync(db, vrchatIds, measure, now, Presence.OneInstanceHours, ct),
                GiveawayRuleKinds.SeenWithinDays => await PresenceAsync(db, vrchatIds, measure, now, Presence.DaysSinceSeen, ct),
                GiveawayWeights.DaysSeen => await PresenceAsync(db, vrchatIds, measure, now, Presence.DaysSeen, ct),
                GiveawayRuleKinds.VoiceHours => await DailyTotalAsync(
                    db, discordIds, measure, now, DailyTotalMetrics.DiscordMemberVoiceMinutes, perHour: true, ct),
                GiveawayRuleKinds.Messages => await DailyTotalAsync(
                    db, discordIds, measure, now, DailyTotalMetrics.DiscordMemberMessages, perHour: false, ct),
                GiveawayRuleKinds.NoTrouble => await TroubleAsync(db, vrchatIds, discordIds, measure, now, ct),
                _ => [],
            };
        }
    }

    private static void Collect(GiveawayRule rule, List<GiveawayMeasure> needs)
    {
        if (GiveawayRuleKinds.IsCombining(rule.Kind))
        {
            foreach (var inner in rule.Rules)
                Collect(inner, needs);

            return;
        }

        switch (rule.Kind)
        {
            case GiveawayRuleKinds.InstanceHours:
            case GiveawayRuleKinds.OneInstanceHours:
            case GiveawayRuleKinds.VoiceHours:
            case GiveawayRuleKinds.Messages:
            case GiveawayRuleKinds.NoTrouble:
                Add(needs, new GiveawayMeasure(rule.Kind, rule.WithinDays));
                break;

            // Its window is the amount, so every "seen in the last N days" rule is answered from
            // one reading: how many days it has been since they were last seen.
            case GiveawayRuleKinds.SeenWithinDays:
                Add(needs, new GiveawayMeasure(rule.Kind, null));
                break;
        }
    }

    private static void Add(List<GiveawayMeasure> needs, GiveawayMeasure measure)
    {
        if (!needs.Contains(measure))
            needs.Add(measure);
    }

    /// <summary>Which presence figure a statement produces.</summary>
    private enum Presence
    {
        TotalHours,
        OneInstanceHours,
        DaysSeen,
        DaysSinceSeen,
    }

    /// <summary>
    /// Hours, days or last-seen from the presence facts, per person.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sessions are worked out the way <c>PresenceCounts</c> works them out — a person's
    /// presence in an instance is the last thing said about them there, and a session nobody saw
    /// the end of closes at the last report from that instance. The two have to agree: a member's
    /// profile and a giveaway's rules answering "how long has this person been here" differently
    /// would be the worst kind of bug to find out about from a member.
    /// </para>
    /// <para>
    /// The person filter is applied after the sessions are built, not before, for the same reason
    /// it is there: narrowing to one person first would make "the last report from that instance"
    /// mean "the last report about that person", and close every session early.
    /// </para>
    /// </remarks>
    private static async Task<Dictionary<string, decimal>> PresenceAsync(
        ModbotContext db,
        string[] subjects,
        GiveawayMeasure measure,
        DateTimeOffset now,
        Presence want,
        CancellationToken ct)
    {
        if (subjects.Length == 0)
            return new Dictionary<string, decimal>(StringComparer.Ordinal);

        var from = measure.WithinDays is { } days ? now.AddDays(-days) : DateTimeOffset.MinValue;

        var final = want switch
        {
            Presence.TotalHours => """
                SELECT subject_id, (SUM(EXTRACT(EPOCH FROM (ended - started))) / 3600.0)::numeric
                FROM sessions WHERE change = 1 AND subject_id = ANY(@subjects)
                GROUP BY subject_id
                """,
            Presence.OneInstanceHours => """
                SELECT subject_id, MAX(hours)::numeric FROM (
                    SELECT subject_id, world_id, instance_id,
                           SUM(EXTRACT(EPOCH FROM (ended - started))) / 3600.0 AS hours
                    FROM sessions WHERE change = 1 AND subject_id = ANY(@subjects)
                    GROUP BY subject_id, world_id, instance_id
                ) per_instance
                GROUP BY subject_id
                """,
            Presence.DaysSeen => """
                SELECT subject_id, COUNT(DISTINCT (started AT TIME ZONE 'UTC')::date)::numeric
                FROM sessions WHERE change = 1 AND subject_id = ANY(@subjects)
                GROUP BY subject_id
                """,
            _ => """
                SELECT subject_id, (EXTRACT(EPOCH FROM (@now - MAX(ended))) / 86400.0)::numeric
                FROM sessions WHERE change = 1 AND subject_id = ANY(@subjects)
                GROUP BY subject_id
                """,
        };

        var sql = $"{Sessions}\n{final}";

        var rows = await ReviewSql.ReadAsync(
            db,
            sql,
            r => (Id: r.GetString(0), Value: r.IsDBNull(1) ? 0m : r.GetDecimal(1)),
            ct,
            ("leave", FactType.InstanceLeft),
            ("presence", PresenceTypes),
            ("from", from),
            ("to", now),
            ("now", now),
            ("subjects", subjects));

        return rows.ToDictionary(r => r.Id, r => r.Value, StringComparer.Ordinal);
    }

    /// <summary>A per-person daily total summed over the window.</summary>
    private static async Task<Dictionary<string, decimal>> DailyTotalAsync(
        ModbotContext db,
        string[] discordIds,
        GiveawayMeasure measure,
        DateTimeOffset now,
        string metric,
        bool perHour,
        CancellationToken ct)
    {
        if (discordIds.Length == 0)
            return new Dictionary<string, decimal>(StringComparer.Ordinal);

        var dimensions = discordIds.Select(id => DailyTotalDimensions.ForUser(FactPlatform.Discord, id)).ToArray();

        var from = measure.WithinDays is { } days
            ? DateOnly.FromDateTime(now.AddDays(-days).UtcDateTime)
            : DateOnly.MinValue;

        var rows = await db.DailyTotals.AsNoTracking()
            .Where(r => r.Metric == metric && r.Day >= from && dimensions.Contains(r.Dimension))
            .GroupBy(r => r.Dimension)
            .Select(g => new { Dimension = g.Key, Value = g.Sum(r => r.Value) })
            .ToListAsync(ct);

        var counted = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var colon = row.Dimension.IndexOf(':', StringComparison.Ordinal);
            var id = colon < 0 ? row.Dimension : row.Dimension[(colon + 1)..];
            counted[id] = perHour ? row.Value / 60m : row.Value;
        }

        return counted;
    }

    /// <summary>
    /// How many bans, kicks and standing flags a person has, on either side.
    /// </summary>
    /// <remarks>
    /// Counted per person and keyed on both ids, because a ban is recorded about a VRChat account
    /// and a Discord timeout about a Discord one, and a rule that said "no trouble" while missing
    /// half of it would be worse than no rule. A dismissed flag does not count: a moderator has
    /// already said it was nothing.
    /// </remarks>
    private static async Task<Dictionary<string, decimal>> TroubleAsync(
        ModbotContext db,
        string[] vrchatIds,
        string[] discordIds,
        GiveawayMeasure measure,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var counted = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var everyone = vrchatIds.Concat(discordIds).Distinct(StringComparer.Ordinal).ToArray();

        if (everyone.Length == 0)
            return counted;

        var from = measure.WithinDays is { } days ? now.AddDays(-days) : DateTimeOffset.MinValue;

        const string FactsSql = """
            SELECT e.subject_id, COUNT(*)::numeric
            FROM modbot_event e
            WHERE e.type = ANY(@types) AND e.subject_id = ANY(@subjects) AND e.occurred_at >= @from
            GROUP BY e.subject_id
            """;

        foreach (var row in await ReviewSql.ReadAsync(
            db,
            FactsSql,
            r => (Id: r.GetString(0), Count: r.GetDecimal(1)),
            ct,
            ("types", TroubleTypes),
            ("subjects", everyone),
            ("from", from)))
        {
            counted[row.Id] = counted.GetValueOrDefault(row.Id) + row.Count;
        }

        var flags = await db.ModerationFlags.AsNoTracking()
            .Where(f => f.DismissedAt == null && f.FlaggedAt >= from && everyone.Contains(f.SubjectId))
            .GroupBy(f => f.SubjectId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        foreach (var flag in flags)
            counted[flag.Id] = counted.GetValueOrDefault(flag.Id) + flag.Count;

        return counted;
    }

    /// <summary>
    /// A person's trouble count, from whichever of their two ids has any.
    /// </summary>
    /// <remarks>
    /// Its own lookup rather than <see cref="Of"/> because trouble is keyed on both platforms at
    /// once, and the general lookup picks one.
    /// </remarks>
    public decimal TroubleOf(GiveawayMeasure measure, GiveawayCandidate person)
    {
        ArgumentNullException.ThrowIfNull(person);

        if (!_counted.TryGetValue(measure, out var byPerson))
            return 0m;

        var total = 0m;

        if (person.VRChatUserId is { } vrchat && byPerson.TryGetValue(vrchat, out var theirs))
            total += theirs;

        if (person.DiscordUserId is { } discord && byPerson.TryGetValue(discord, out var discordTrouble))
            total += discordTrouble;

        return total;
    }

    /// <summary>The three fact types a companion reports about who is in an instance.</summary>
    private static readonly string[] PresenceTypes =
    [
        FactType.InstanceJoined,
        FactType.InstancePresenceObserved,
        FactType.InstanceLeft,
    ];

    /// <summary>What counts as trouble: a ban, a removal, an instance kick, or their Discord twins.</summary>
    private static readonly string[] TroubleTypes =
    [
        FactType.MemberBanned,
        FactType.MemberKicked,
        FactType.GroupInstanceKick,
        FactType.GroupInstanceWarn,
        FactType.DiscordMemberBanned,
        FactType.DiscordMemberKicked,
        FactType.DiscordMemberTimedOut,
    ];

    /// <summary>
    /// The sessions every presence figure is built on, over one window.
    /// </summary>
    /// <remarks>
    /// The bounds are real instants rather than nulls so that no parameter here is ever typeless,
    /// the same reason <c>PresenceCounts</c> expresses "all of recorded history" as the widest pair.
    /// </remarks>
    private const string Sessions = """
        WITH p AS (
            SELECT e.world_id, e.instance_id, e.subject_id, e.occurred_at, e.id,
                   CASE WHEN e.type = @leave THEN 0 ELSE 1 END AS here
            FROM modbot_event e
            WHERE e.type = ANY(@presence)
              AND e.occurred_at >= @from AND e.occurred_at < @to
              AND e.world_id IS NOT NULL AND e.instance_id IS NOT NULL
        ),
        changes AS (
            SELECT p.*,
                   p.here - COALESCE(LAG(p.here) OVER (
                       PARTITION BY p.world_id, p.instance_id, p.subject_id
                       ORDER BY p.occurred_at, p.id), 0) AS change,
                   MAX(p.occurred_at) OVER (PARTITION BY p.world_id, p.instance_id) AS last_report
            FROM p
        ),
        sessions AS (
            SELECT world_id, instance_id, subject_id, change,
                   occurred_at AS started,
                   COALESCE(LEAD(occurred_at) OVER (
                       PARTITION BY world_id, instance_id, subject_id
                       ORDER BY occurred_at, id), last_report) AS ended
            FROM changes
            WHERE change <> 0
        )
        """;
}
