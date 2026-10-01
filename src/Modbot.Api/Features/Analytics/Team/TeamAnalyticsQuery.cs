using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Analytics.Reviews;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.Analytics.Team;

/// <summary>
/// "Who is doing the moderation work, and when is nobody covering?" (spec 10.1, 5.8).
/// </summary>
/// <remarks>
/// <para>
/// The per-moderator numbers come from the daily totals, one metric per kind of action, so a
/// year of history costs a few hundred rows. Coverage gaps come from facts, because they are a
/// question about minutes rather than days and no daily total can hold that.
/// </para>
/// <para>
/// <strong>How coverage is decided.</strong> Every presence report comes from a moderator's
/// companion, so a client reporting from an instance is proof a moderator is in it — that
/// signal needs no roster at all. A roster is still built (moderation roles from the group
/// snapshot, people who have taken moderation actions, the owner) so that a moderator who is
/// seen by <em>somebody else's</em> client also counts as cover. A gap begins at the moment the
/// last of either signal ends while people are still known to be present, and ends when a
/// moderator is seen again or the instance closes. If neither is ever seen, the gap's end is
/// unknown, and the response says so rather than guessing.
/// </para>
/// <para>
/// <strong>What it cannot see.</strong> A moderator without the client, in an instance no client
/// is in, is invisible; an instance no client ever entered has no population at all. Only the
/// second shows on the page, as its own number ("Instances nobody watched"). The first is not
/// written there, because screens carry no explanatory text; the Team section of the analytics
/// docs page states it.
/// </para>
/// <para>
/// <strong>Not a leaderboard.</strong> Each moderator's own numbers go only to the people who can
/// read the audit log, which already says who did what; everybody else gets the team's numbers,
/// their own row, and the team's middle beside it. Burnout among volunteer moderators is the cost
/// of a page that ranks them, so the middle is a median (one busy moderator does not set the
/// usual), is left out for a team too small to keep it from naming somebody, and the rows sort by
/// when each moderator was last active, never by how much they did.
/// </para>
/// <para>
/// The busy-hours grid, the waits and the outcome numbers each have a class of their own beside
/// this one (<see cref="TeamCoverWeek"/>, <see cref="TeamQueues"/>, <see cref="TeamOutcomes"/>).
/// </para>
/// </remarks>
public sealed class TeamAnalyticsQuery(ModbotContext db)
{
    /// <summary>
    /// The kinds, in page order, with the words the columns use: the actions on people first, then
    /// door work and admin.
    /// </summary>
    /// <remarks>
    /// Which group a kind is in is the reviews' own list (<see cref="ActionsOnPeople"/>), so the page
    /// and the review checks agree about what an action on a person is. Unbans sit with the door
    /// work: lifting a ban lets somebody back in and is no strike against anybody (accountability
    /// signals design 3.4). Before this the page summed every kind into one "Actions" number, which
    /// made a moderator who runs the door look like one who runs people out of it.
    /// </remarks>
    public static readonly IReadOnlyList<ActionKind> Kinds =
    [
        Kind(DailyTotalMetrics.ModeratorInstanceKicks, "Instance kicks"),
        Kind(DailyTotalMetrics.ModeratorWarns, "Warns"),
        Kind(DailyTotalMetrics.ModeratorBans, "Bans"),
        Kind(DailyTotalMetrics.ModeratorRemovals, "Removed from group"),
        Kind(DailyTotalMetrics.ModeratorRejections, "Requests rejected"),
        Kind(DailyTotalMetrics.ModeratorInvites, "Invites"),
        Kind(DailyTotalMetrics.ModeratorApprovals, "Requests approved"),
        Kind(DailyTotalMetrics.ModeratorUnbans, "Unbans"),
        Kind(DailyTotalMetrics.ModeratorRoleChanges, "Role changes"),
    ];

    /// <summary>
    /// The fewest moderators active in the window for the team's middle to be shown. Four, so the
    /// middle of a small team is never simply one moderator's own figure.
    /// </summary>
    public const int LeastForMiddle = 4;

    private static ActionKind Kind(string metric, string label) => new(
        metric,
        label,
        ActionsOnPeople.BaselineMetrics.Contains(metric, StringComparer.Ordinal) ? ActionGroups.People : ActionGroups.Door);

    private static readonly IReadOnlySet<string> PeopleMetrics =
        Kinds.Where(k => k.Group == ActionGroups.People).Select(k => k.Metric).ToHashSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> DoorMetrics =
        Kinds.Where(k => k.Group == ActionGroups.Door).Select(k => k.Metric).ToHashSet(StringComparer.Ordinal);

    /// <summary>The actions that mark somebody as a moderator, whatever roles they hold.</summary>
    private static readonly string[] ModerationActionTypes =
    [
        FactType.MemberBanned,
        FactType.MemberUnbanned,
        FactType.MemberKicked,
        FactType.GroupInstanceKick,
        FactType.GroupInstanceWarn,
        FactType.JoinRequestRejected,
        FactType.JoinRequestBlocked,
        FactType.RoleGranted,
        FactType.RoleRevoked,
    ];

    private static readonly IReadOnlySet<string> KindMetrics =
        Kinds.Select(k => k.Metric).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// What this page draws from, and so where its "all time" starts: the per-moderator daily
    /// totals and the facts behind them, the presence reports the coverage gaps are worked out
    /// from, and the instance openings and closes the gaps are measured against.
    /// </summary>
    public static readonly PageSources Sources = PageSources.Of(
        Kinds.Select(k => k.Metric).ToList(),
        [.. AnalyticsSql.PresenceTypes, FactType.GroupInstanceCreated, FactType.GroupInstanceClosed, FactType.JoinRequestCreated],
        groupInstances: true,
        headCounts: true);

    private readonly AnalyticsSql _sql = new(db);

    /// <param name="viewer">Who is asking: decides whether each moderator is named, and which row is theirs.</param>
    /// <param name="people">
    /// The head count that makes an instance busy for the hour grid, in place of the saved setting;
    /// null for the saved one.
    /// </param>
    public async Task<TeamAnalytics> RunAsync(
        DateOnly from,
        DateOnly to,
        DateTimeOffset now,
        TeamViewer viewer,
        int? people = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        var thresholds = ReviewThresholds.Read(await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.ReviewThresholds)
            .FirstOrDefaultAsync(ct));

        var totals = await _sql.DailyTotalsAsync(from, to, KindMetrics, ct);
        var everyone = await ModeratorsAsync(totals, ct);

        var roster = await RosterAsync(ct);
        var gaps = await CoverageGapsAsync(from, to, roster, ct);
        var watched = await InstancesWatchedAsync(from, to, ct);
        var unwatched = await InstancesOpenedWithoutAnyWatchAsync(from, to, now, ct);

        var cover = await new TeamCoverWeek(_sql).RunAsync(
            from,
            to,
            now,
            Math.Clamp(people ?? thresholds.CoverPeople, ReviewThresholds.MinCoverPeople, ReviewThresholds.MaxCoverPeople),
            thresholds.CoverPeople,
            gaps,
            ct);

        var outcomes = new TeamOutcomes(_sql);

        var byKind = Kinds
            .Select(k =>
            {
                var points = totals.SummedPerDay(new HashSet<string>(StringComparer.Ordinal) { k.Metric });
                return new KindSeries(k.Metric, k.Label, points.Sum(p => p.Value), points);
            })
            .ToList();

        return new TeamAnalytics(
            from,
            to,
            MissingDays.Today(to, now),
            Kinds,
            viewer.CanSeeEachModerator,
            await YouAsync(everyone, viewer.VRChatUserId, ct),
            viewer.CanSeeEachModerator ? everyone : [],
            everyone.Count,
            MiddleOf(everyone),
            totals.SummedPerDay(PeopleMetrics),
            totals.SummedPerDay(DoorMetrics),
            byKind,
            viewer.CanSeeEachModerator ? gaps : [.. gaps.Select(g => g with { LastModerator = null })],
            cover,
            await new TeamQueues(_sql).RunAsync(from, to, ct),
            await outcomes.ActedOnAgainAsync(from, to, thresholds.CountedTypes, ct),
            await outcomes.BansLiftedAsync(from, to, now, ct),
            roster.Count,
            watched,
            unwatched,
            await new MissingDaysQuery(db).AuditLogAsync(from, to, ct),
            await AnalyticsCoverageQuery.RunAsync(db, ct),
            now);
    }

    private async Task<IReadOnlyList<ModeratorSummary>> ModeratorsAsync(
        IReadOnlyList<DailyTotalRow> totals,
        CancellationToken ct)
    {
        var perActor = totals
            .Where(r => r.Dimension.Length > 0 && r.Value != 0)
            .GroupBy(r => r.Dimension, StringComparer.Ordinal)
            .ToList();

        if (perActor.Count == 0)
            return [];

        var split = perActor.ToDictionary(g => g.Key, g => AnalyticsSql.SplitDimension(g.Key), StringComparer.Ordinal);
        var names = await _sql.NamesAsync(split.Values.Select(s => s.Id).ToList(), ct);
        var usual = await UsualAsync(ct);

        return perActor
            .Select(g =>
            {
                var (platform, id) = split[g.Key];
                return Summary(new Person(platform, id, names.GetValueOrDefault(id)), g.ToList(), usual.GetValueOrDefault(g.Key));
            })
            // Most recently active first, then by name. Never by volume: a table sorted by count
            // is a leaderboard (accountability spec 3.4, analytics design review F8). Sorted this
            // way, whoever has gone quiet sinks to the bottom, which is the question the table is
            // here to answer.
            .OrderByDescending(m => m.LastActiveDay)
            .ThenBy(m => m.Who.Name ?? m.Who.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Who.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>One moderator's numbers from their own daily total rows.</summary>
    private static ModeratorSummary Summary(Person who, IReadOnlyList<DailyTotalRow> rows, decimal? usual)
    {
        var byKind = rows
            .GroupBy(r => r.Metric, StringComparer.Ordinal)
            .ToDictionary(k => k.Key, k => k.Sum(r => r.Value), StringComparer.Ordinal);

        var onPeople = rows.Where(r => PeopleMetrics.Contains(r.Metric)).ToList();
        var daysOnPeople = onPeople.Select(r => r.Day).Distinct().Count();
        var onPeopleTotal = onPeople.Sum(r => r.Value);

        return new ModeratorSummary(
            who,
            onPeopleTotal,
            rows.Where(r => DoorMetrics.Contains(r.Metric)).Sum(r => r.Value),
            byKind,
            rows.Select(r => r.Day).Distinct().Count(),
            daysOnPeople == 0 ? null : Math.Round(onPeopleTotal / daysOnPeople, 1),
            usual,
            rows.Count == 0 ? null : rows.Max(r => r.Day));
    }

    /// <summary>
    /// Each moderator's usual from the reviews' baselines, keyed by daily total dimension. The
    /// baselines are rebuilt with every detection run, so this reads them rather than working them
    /// out again.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, decimal>> UsualAsync(CancellationToken ct)
    {
        var rows = await db.ModeratorBaselines.AsNoTracking()
            .Select(b => new { b.Platform, b.ModeratorId, b.ActionsPerActiveDay })
            .ToListAsync(ct);

        var usual = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var row in rows)
            usual[DailyTotalDimensions.ForUser(row.Platform, row.ModeratorId)] = Math.Round(row.ActionsPerActiveDay, 1);

        return usual;
    }

    /// <summary>
    /// The caller's own row: theirs from the list when they acted in the window, otherwise an
    /// empty one under their VRChat name, so a quiet month reads as nought rather than as nothing.
    /// </summary>
    private async Task<ModeratorSummary?> YouAsync(
        IReadOnlyList<ModeratorSummary> everyone,
        string? vrchatUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(vrchatUserId))
            return null;

        var vrchat = DailyTotalDimensions.Label(FactPlatform.VRChat);

        var mine = everyone.FirstOrDefault(m =>
            m.Who.Platform == vrchat && string.Equals(m.Who.Id, vrchatUserId, StringComparison.Ordinal));

        if (mine is not null)
            return mine;

        var names = await _sql.NamesAsync([vrchatUserId], ct);
        var usual = await UsualAsync(ct);

        return Summary(
            new Person(vrchat, vrchatUserId, names.GetValueOrDefault(vrchatUserId)),
            [],
            usual.GetValueOrDefault(DailyTotalDimensions.ForUser(FactPlatform.VRChat, vrchatUserId)));
    }

    /// <summary>The middle of each number over the moderators active in the window.</summary>
    private static TeamMiddle? MiddleOf(IReadOnlyList<ModeratorSummary> everyone)
    {
        if (everyone.Count < LeastForMiddle)
            return null;

        var perDay = everyone.Where(m => m.OnPeoplePerDay is not null).Select(m => m.OnPeoplePerDay!.Value).ToList();

        return new TeamMiddle(
            everyone.Count,
            Middles.Of(everyone.Select(m => m.OnPeople))!.Value,
            Middles.Of(everyone.Select(m => m.DoorAndAdmin))!.Value,
            Middles.Of(everyone.Select(m => (decimal)m.DaysActive))!.Value,
            Middles.Of(perDay) is { } p ? Math.Round(p, 1) : null);
    }

    /// <summary>
    /// Everyone the moderator rule currently matches. Over the whole log, because "is this
    /// person a moderator" is a now question and not a window one.
    /// </summary>
    private async Task<IReadOnlySet<string>> RosterAsync(CancellationToken ct)
    {
        var roster = new HashSet<string>(StringComparer.Ordinal);

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var snapshot = GroupInfoSnapshot.Parse(settings?.GroupInfoSnapshot);

        if (snapshot?.OwnerId is { Length: > 0 } owner)
            roster.Add(owner);

        var moderationRoles = (snapshot?.Roles ?? [])
            .Where(ModerationRoles.IsModerationRole)
            .Select(r => r.Id)
            .ToArray();

        if (moderationRoles.Length > 0)
        {
            // Holds the role now: the latest grant of it is later than any revoke of it.
            const string RoleHolders = """
                SELECT h.subject_id
                FROM (
                    SELECT e.subject_id,
                           MAX(e.occurred_at) FILTER (WHERE e.type = @granted) AS granted,
                           MAX(e.occurred_at) FILTER (WHERE e.type = @revoked) AS revoked
                    FROM modbot_event e
                    WHERE e.type IN (@granted, @revoked)
                      AND e.subject_platform = @vrchat
                      AND e.data->>'roleId' = ANY(@roles)
                    GROUP BY e.subject_id, e.data->>'roleId'
                ) h
                WHERE h.granted IS NOT NULL AND (h.revoked IS NULL OR h.revoked < h.granted)
                """;

            foreach (var id in await _sql.ReadAsync(RoleHolders, r => r.GetString(0), ct,
                         ("granted", FactType.RoleGranted),
                         ("revoked", FactType.RoleRevoked),
                         ("vrchat", (short)FactPlatform.VRChat),
                         ("roles", moderationRoles)))
                roster.Add(id);
        }

        const string Actors = """
            SELECT DISTINCT e.actor_id
            FROM modbot_event e
            WHERE e.actor_id IS NOT NULL AND e.actor_platform = @vrchat
              AND (e.type = ANY(@actions) OR (e.type = @join AND e.actor_id <> e.subject_id))
            """;

        foreach (var id in await _sql.ReadAsync(Actors, r => r.GetString(0), ct,
                     ("vrchat", (short)FactPlatform.VRChat),
                     ("actions", ModerationActionTypes),
                     ("join", FactType.MemberJoined)))
            roster.Add(id);

        return roster;
    }

    /// <summary>
    /// The gaps, from the presence stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Done in SQL because the stream is every presence fact in the window and a busy group
    /// writes tens of thousands a day; only the handful of rows where cover ran out come back.
    /// </para>
    /// <para>
    /// Per person, presence is the last thing said about them in that instance: an arrival makes
    /// them present, a leave makes them absent, and a repeat of either changes nothing. That is
    /// what keeps two clients' reports of one arrival from counting as two people, and a leave
    /// nobody saw the arrival for from counting as minus one. The instance's population is the
    /// running sum of those changes.
    /// </para>
    /// <para>
    /// Cover is the running sum of two things: recognised moderators present (same rule as
    /// above) and clients reporting. A client's stretch in an instance ends at its last fact
    /// there, or at a leave after which the next thing that client reports is that same person
    /// arriving again -- because when the client's own user walks out it records exactly their
    /// leave and then nothing until they enter somewhere again, and on re-entry their own
    /// arrival comes first. A stretch begins at the client's first fact and after every end.
    /// At one instant a stretch's end sorts after person rows and its start before them, so the
    /// moment cover reaches zero is seen with the population already updated for whoever left.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<CoverageGap>> CoverageGapsAsync(
        DateOnly from,
        DateOnly to,
        IReadOnlySet<string> roster,
        CancellationToken ct)
    {
        const string Sql = """
            WITH p AS (
                SELECT e.world_id, e.instance_id, e.subject_id, e.occurred_at, e.id,
                       CASE WHEN e.type = @leave THEN 0 ELSE 1 END AS here,
                       e.subject_id = ANY(@mods) AS is_mod,
                       e.data->>'deviceId' AS device
                FROM modbot_event e
                WHERE e.type = ANY(@presence)
                  AND e.occurred_at >= @from AND e.occurred_at < @to
                  AND e.world_id IS NOT NULL AND e.instance_id IS NOT NULL
            ),
            changes AS (
                SELECT p.*,
                       p.here - COALESCE(LAG(p.here) OVER (
                           PARTITION BY p.world_id, p.instance_id, p.subject_id
                           ORDER BY p.occurred_at, p.id), 0) AS change
                FROM p
            ),
            people AS (
                SELECT world_id, instance_id, subject_id, occurred_at, id, change,
                       CASE WHEN is_mod THEN change ELSE 0 END AS cover_change, 0 AS kind
                FROM changes
                WHERE change <> 0
            ),
            device_rows AS (
                SELECT world_id, instance_id, device, subject_id, occurred_at, id, here,
                       LEAD(here) OVER w AS next_here,
                       LEAD(subject_id) OVER w AS next_subject
                FROM p
                WHERE device IS NOT NULL
                WINDOW w AS (PARTITION BY world_id, instance_id, device ORDER BY occurred_at, id)
            ),
            device_marks AS (
                SELECT *,
                       (next_here IS NULL
                        OR (here = 0 AND next_here = 1 AND next_subject = subject_id)) AS is_end
                FROM device_rows
            ),
            device_edges AS (
                SELECT world_id, instance_id, occurred_at, id, is_end,
                       COALESCE(LAG(is_end) OVER (
                           PARTITION BY world_id, instance_id, device ORDER BY occurred_at, id), true) AS is_start
                FROM device_marks
            ),
            stream AS (
                SELECT world_id, instance_id, subject_id, occurred_at, id, change AS people_change, cover_change, kind
                FROM people
                UNION ALL
                SELECT world_id, instance_id, NULL, occurred_at, id, 0, 1, -1 FROM device_edges WHERE is_start
                UNION ALL
                SELECT world_id, instance_id, NULL, occurred_at, id, 0, -1, 1 FROM device_edges WHERE is_end
            ),
            run AS (
                SELECT s.*,
                       SUM(people_change) OVER w AS people,
                       SUM(cover_change) OVER w AS covered,
                       LAG(subject_id) OVER w AS prev_subject,
                       LAG(occurred_at) OVER w AS prev_at,
                       LAG(people_change) OVER w AS prev_people_change
                FROM stream s
                WINDOW w AS (PARTITION BY world_id, instance_id ORDER BY occurred_at, kind, id ROWS UNBOUNDED PRECEDING)
            ),
            marked AS (
                SELECT r.*,
                       LAG(covered) OVER w AS prev_covered,
                       MIN(CASE WHEN covered > 0 THEN occurred_at END) OVER (
                           PARTITION BY world_id, instance_id
                           ORDER BY occurred_at, kind, id
                           ROWS BETWEEN 1 FOLLOWING AND UNBOUNDED FOLLOWING) AS resumed_at
                FROM run r
                WINDOW w AS (PARTITION BY world_id, instance_id ORDER BY occurred_at, kind, id)
            )
            SELECT world_id, instance_id, occurred_at, people::int, resumed_at,
                   CASE
                       WHEN kind = 0 THEN subject_id
                       WHEN prev_at = occurred_at AND prev_people_change = -1 THEN prev_subject
                   END AS leaver
            FROM marked
            WHERE covered = 0 AND COALESCE(prev_covered, 0) > 0 AND people > 0
            ORDER BY occurred_at
            """;

        var rows = await _sql.ReadAsync(
            Sql,
            r => (
                WorldId: r.GetString(0),
                InstanceId: r.GetString(1),
                StartedAt: AnalyticsSql.InstantOf(r, 2),
                People: r.GetInt32(3),
                ResumedAt: AnalyticsSql.InstantOrNull(r, 4),
                Leaver: r.IsDBNull(5) ? null : r.GetString(5)),
            ct,
            ("leave", FactType.InstanceLeft),
            ("presence", AnalyticsSql.PresenceTypes),
            ("mods", roster.ToArray()),
            ("from", AnalyticsSql.DayStart(from)),
            ("to", AnalyticsSql.DayEnd(to)));

        if (rows.Count == 0)
            return [];

        var closes = await InstanceClosesAsync(from, to, ct);
        var names = await _sql.NamesAsync(rows.Where(r => r.Leaver is not null).Select(r => r.Leaver!).ToList(), ct);

        var worldIds = rows.Select(r => r.WorldId).Distinct(StringComparer.Ordinal).ToList();
        var numbers = rows.Select(r => r.InstanceId).Distinct(StringComparer.Ordinal).ToList();

        var worldNames = await db.VRChatWorlds.AsNoTracking()
            .Where(w => worldIds.Contains(w.WorldId) && w.Name != null)
            .Select(w => new { w.WorldId, w.Name })
            .ToDictionaryAsync(w => w.WorldId, w => w.Name!, StringComparer.Ordinal, ct);

        // Every instance that ever carried one of these numbers; which one a gap was in is decided
        // by time below, because VRChat hands a number out again once its instance has closed.
        var instances = await db.VRChatInstances.AsNoTracking()
            .Where(i => worldIds.Contains(i.WorldId) && i.VRChatInstanceId != null && numbers.Contains(i.VRChatInstanceId))
            .Select(i => new { i.Id, i.WorldId, Number = i.VRChatInstanceId!, i.Name, i.OpenedAt, i.ClosedAt, i.LastSeenAt })
            .ToListAsync(ct);

        return rows
            .Select(r =>
            {
                var instance = instances.FirstOrDefault(i =>
                    string.Equals(i.WorldId, r.WorldId, StringComparison.Ordinal)
                    && string.Equals(i.Number, r.InstanceId, StringComparison.Ordinal)
                    && r.StartedAt >= i.OpenedAt
                    && r.StartedAt <= (i.ClosedAt ?? i.LastSeenAt));

                var closedAt = closes.GetValueOrDefault((r.WorldId, r.InstanceId));
                var closed = closedAt is { } c && c >= r.StartedAt ? c : (DateTimeOffset?)null;

                var (endedAt, endedBy) = (r.ResumedAt, closed) switch
                {
                    (null, null) => ((DateTimeOffset?)null, "unknown"),
                    ({ } resumed, null) => (resumed, "moderator-arrived"),
                    (null, { } closedAtInstant) => (closedAtInstant, "instance-closed"),
                    ({ } resumed, { } closedAtInstant) => resumed <= closedAtInstant
                        ? (resumed, "moderator-arrived")
                        : (closedAtInstant, "instance-closed"),
                };

                return new CoverageGap(
                    r.WorldId,
                    r.InstanceId,
                    worldNames.GetValueOrDefault(r.WorldId),
                    instance?.Id,
                    instance?.Name,
                    r.StartedAt,
                    endedAt,
                    endedBy,
                    r.People,
                    r.Leaver is null ? null : new Person("vrchat", r.Leaver, names.GetValueOrDefault(r.Leaver)));
            })
            .ToList();
    }

    private async Task<Dictionary<(string World, string Instance), DateTimeOffset>> InstanceClosesAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        const string Sql = """
            SELECT e.world_id, e.instance_id, MAX(e.occurred_at)
            FROM modbot_event e
            WHERE e.type = @close AND e.occurred_at >= @from AND e.occurred_at < @to
              AND e.world_id IS NOT NULL AND e.instance_id IS NOT NULL
            GROUP BY 1, 2
            """;

        var rows = await _sql.ReadAsync(
            Sql,
            r => (Key: (r.GetString(0), r.GetString(1)), At: AnalyticsSql.InstantOf(r, 2)),
            ct,
            ("close", FactType.GroupInstanceClosed),
            ("from", AnalyticsSql.DayStart(from)),
            ("to", AnalyticsSql.DayEnd(to)));

        return rows.ToDictionary(r => r.Key, r => r.At);
    }

    private async Task<int> InstancesWatchedAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        const string Sql = """
            SELECT COUNT(*)::int FROM (
                SELECT DISTINCT e.world_id, e.instance_id
                FROM modbot_event e
                WHERE e.type = ANY(@presence) AND e.occurred_at >= @from AND e.occurred_at < @to
                  AND e.world_id IS NOT NULL AND e.instance_id IS NOT NULL
            ) w
            """;

        var rows = await _sql.ReadAsync(Sql, r => r.GetInt32(0), ct,
            ("presence", AnalyticsSql.PresenceTypes),
            ("from", AnalyticsSql.DayStart(from)),
            ("to", AnalyticsSql.DayEnd(to)));

        return rows.Count > 0 ? rows[0] : 0;
    }

    /// <summary>
    /// How far either side of an instance's life a presence report still counts as from inside it. A
    /// client's clock and Modbot's disagree a little, and Modbot's own times are a poll wide.
    /// </summary>
    private static readonly TimeSpan PresenceLeeway = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The group's instances Modbot first saw inside the window, from its instance list, that no
    /// presence report came from while they ran.
    /// </summary>
    /// <remarks>
    /// Counted from <c>vrchat_instance</c>, one per instance, so a number VRChat handed out twice is two
    /// instances and each is judged by the reports made during its own life. The audit log's create
    /// entries used to be the count, and they missed instances Modbot saw open that VRChat wrote no
    /// entry for.
    /// </remarks>
    private async Task<int> InstancesOpenedWithoutAnyWatchAsync(
        DateOnly from, DateOnly to, DateTimeOffset now, CancellationToken ct)
    {
        var group = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.ManagedGroupId)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(group))
            return 0;

        var start = AnalyticsSql.DayStart(from);
        var end = AnalyticsSql.DayEnd(to);

        var opened = await db.VRChatInstances.AsNoTracking()
            .Where(i => i.GroupId == group && i.OpenedAt >= start && i.OpenedAt < end)
            .Select(i => new { i.WorldId, i.VRChatInstanceId, i.OpenedAt, i.ClosedAt })
            .ToListAsync(ct);

        // With no number there is nothing a presence report could name it by.
        var numbered = opened.Where(i => i.VRChatInstanceId is not null).ToList();
        var withoutNumber = opened.Count - numbered.Count;

        if (numbered.Count == 0)
            return withoutNumber;

        const string Sql = """
            SELECT COUNT(*)::int
            FROM unnest(@worlds::text[], @numbers::text[], @froms::timestamptz[], @tos::timestamptz[])
                 AS o(world_id, instance_id, from_at, to_at)
            WHERE NOT EXISTS (
                SELECT 1 FROM modbot_event e
                WHERE e.type = ANY(@presence)
                  AND e.world_id = o.world_id AND e.instance_id = o.instance_id
                  AND e.occurred_at >= o.from_at AND e.occurred_at <= o.to_at)
            """;

        var rows = await _sql.ReadAsync(Sql, r => r.GetInt32(0), ct,
            ("worlds", numbered.Select(i => i.WorldId).ToArray()),
            ("numbers", numbered.Select(i => i.VRChatInstanceId!).ToArray()),
            ("froms", numbered.Select(i => (i.OpenedAt - PresenceLeeway).UtcDateTime).ToArray()),
            ("tos", numbered.Select(i => ((i.ClosedAt ?? now) + PresenceLeeway).UtcDateTime).ToArray()),
            ("presence", AnalyticsSql.PresenceTypes));

        return withoutNumber + (rows.Count > 0 ? rows[0] : 0);
    }
}
