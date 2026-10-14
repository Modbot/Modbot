using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Npgsql;
using NpgsqlTypes;

namespace Modbot.Analytics.Calendar;

/// <summary>
/// Keeps what the member milestones are made from: who came to each date of an event, and when each
/// person was last seen arriving in a group instance.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Always on.</strong> The marks on a profile need no setting, so this runs whether or not
/// Discord posts or a role are switched on, and from the first date it can find: earlier history
/// counts toward the totals. Only the posts and the role care when a moment was reached
/// (<see cref="Settings.MilestonesFrom"/>).
/// </para>
/// <para>
/// <strong>Dates.</strong> A date is counted once its instance has closed, with the same rule for
/// "closed" and the same answer for "who came" as the event recap (<see cref="EventAttendance"/>), so
/// only people in an instance where a moderator's Modbot app stood are counted. The rows are written
/// with <c>ON CONFLICT DO NOTHING</c> before the date is marked counted, so a restart, or a second
/// copy of Modbot, counts the same date again to the same result.
/// </para>
/// <para>
/// <strong>Returns.</strong> A visit is one of the two arrival facts, in any instance, as the
/// Regulars page counts one. The scan reads the fact log in slices of <see cref="Slice"/> from where it
/// stopped, a day further back each time so a late report is still read, and keeps the last visit
/// of each person on <c>member_milestone</c>: the fact log is pruned, a gap of two months is not.
/// A visit after a gap of the settings' number of days or more is a return. The row is changed only
/// where its last visit is still what was read, so two copies cannot both take the same gap.
/// A person's first visit is never a return.
/// </para>
/// </remarks>
public sealed class MemberMilestoneCounter
{
    /// <summary>How many dates one pass counts.</summary>
    public const int DatesPerPass = 50;

    /// <summary>How much of the fact log one read covers. A first pass on a long history takes several.</summary>
    public static readonly TimeSpan Slice = TimeSpan.FromDays(30);

    /// <summary>How many slices one pass reads.</summary>
    public const int SlicesPerPass = 12;

    /// <summary>How far back each read starts, behind where the last one stopped.</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromDays(1);

    /// <summary>How often the log is read again once it is caught up.</summary>
    public static readonly TimeSpan ReadEvery = TimeSpan.FromMinutes(10);

    private static readonly string[] Arrivals = [FactType.InstanceJoined, FactType.InstancePresenceObserved];

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly EventAttendance _attendance;

    public MemberMilestoneCounter(ModbotContext db, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
        _attendance = new EventAttendance(db);
    }

    /// <summary>Counts the closed dates, then reads the fact log for returning visitors.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        await CountDatesAsync(ct).ConfigureAwait(false);
        await ReadVisitsAsync(ct).ConfigureAwait(false);
    }

    /// <returns>How many dates were counted.</returns>
    public async Task<int> CountDatesAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;

        var openings = await _db.CalendarOpenings.AsNoTracking()
            .Where(o => o.InstanceId != null && o.MilestonesCountedAt == null && o.OccurrenceStartsAt < now)
            .OrderBy(o => o.OccurrenceStartsAt)
            .Take(DatesPerPass)
            .ToListAsync(ct).ConfigureAwait(false);

        var counted = 0;

        foreach (var opening in openings)
        {
            var calendarEvent = await _db.CalendarEvents.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == opening.EventId, ct).ConfigureAwait(false);

            var instance = await _db.VRChatInstances.AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == opening.InstanceId, ct).ConfigureAwait(false);

            // The event cannot be gone (its dates go with it), but an instance row can: a date with no
            // instance has nobody to count, and left open it would take a place in every pass.
            if (calendarEvent is null || instance is null)
            {
                await MarkCountedAsync(opening, now, ct).ConfigureAwait(false);
                continue;
            }

            var endsAt = CalendarRepeat.StartingAt(calendarEvent, opening.OccurrenceStartsAt)?.EndsAt
                ?? opening.OccurrenceStartsAt + CalendarRepeat.LengthOf(calendarEvent);

            if ((instance.ClosedAt ?? endsAt + EventAttendance.StillOpenAfter) > now)
                continue;

            var turnout = (await _attendance.ForOpeningsAsync([opening], ct).ConfigureAwait(false))[0];

            if (turnout.People.Count > 0)
            {
                await _db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO event_attendee (user_id, event_id, occurrence_starts_at, counted_at)
                    SELECT u, @event, @occurrence, @endedAt FROM unnest(@users) AS u
                    ON CONFLICT DO NOTHING
                    """,
                    [
                        new NpgsqlParameter("event", opening.EventId),
                        new NpgsqlParameter("occurrence", opening.OccurrenceStartsAt.ToUniversalTime()),
                        new NpgsqlParameter("endedAt", (endsAt < now ? endsAt : now).ToUniversalTime()),
                        new NpgsqlParameter("users", turnout.People.ToArray()),
                    ],
                    ct).ConfigureAwait(false);
            }

            await MarkCountedAsync(opening, now, ct).ConfigureAwait(false);

            counted++;
        }

        return counted;
    }

    private Task<int> MarkCountedAsync(CalendarOpening opening, DateTimeOffset now, CancellationToken ct)
        => _db.CalendarOpenings
            .Where(o => o.EventId == opening.EventId
                && o.OccurrenceStartsAt == opening.OccurrenceStartsAt
                && o.MilestonesCountedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(o => o.MilestonesCountedAt, now), ct);

    /// <summary>Reads the arrivals since the last read and keeps each person's last visit and latest return.</summary>
    public async Task ReadVisitsAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (settings.MilestoneVisitsReadTo is { } readTo && now - readTo < ReadEvery)
            return;

        DateTimeOffset start;

        if (settings.MilestoneVisitsReadTo is { } stopped)
        {
            start = stopped - Overlap;
        }
        else
        {
            var first = await _db.Events.AsNoTracking()
                .Where(e => Arrivals.Contains(e.Type))
                .MinAsync(e => (DateTimeOffset?)e.OccurredAt, ct).ConfigureAwait(false);

            if (first is not { } earliest)
                return;

            start = earliest;
        }

        var backAfter = TimeSpan.FromDays(Math.Max(1, settings.MilestoneBackDays));

        for (var slice = 0; slice < SlicesPerPass && start < now; slice++)
        {
            var end = start + Slice < now ? start + Slice : now;

            await ReadSliceAsync(start, end, backAfter, ct).ConfigureAwait(false);

            // Only forward: another copy that read further must not be moved back.
            await _db.Settings
                .Where(s => s.Id == 1 && (s.MilestoneVisitsReadTo == null || s.MilestoneVisitsReadTo < end))
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.MilestoneVisitsReadTo, end), ct)
                .ConfigureAwait(false);

            // The update went round the tracked row; read it again so the next pass on this context
            // sees where the read stopped.
            await _db.Entry(settings).ReloadAsync(ct).ConfigureAwait(false);

            start = end;
        }
    }

    private async Task ReadSliceAsync(DateTimeOffset from, DateTimeOffset until, TimeSpan backAfter, CancellationToken ct)
    {
        // One row for each person and day: the first and the last arrival that day.
        var days = await _db.Database.SqlQueryRaw<VisitDay>(
            """
            SELECT e.subject_id AS user_id, MIN(e.occurred_at) AS first_at, MAX(e.occurred_at) AS last_at
            FROM modbot_event e
            WHERE e.type = ANY(@types) AND e.occurred_at >= @from AND e.occurred_at < @until
            GROUP BY e.subject_id, date_trunc('day', e.occurred_at AT TIME ZONE 'UTC')
            ORDER BY e.subject_id, MIN(e.occurred_at)
            """,
            new NpgsqlParameter("types", Arrivals),
            new NpgsqlParameter("from", from.ToUniversalTime()),
            new NpgsqlParameter("until", until.ToUniversalTime()))
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var chunk in days.GroupBy(d => d.UserId).Chunk(500))
        {
            var ids = chunk.Select(g => g.Key).ToList();

            var known = await _db.MemberMilestones.AsNoTracking()
                .Where(m => ids.Contains(m.UserId))
                .ToDictionaryAsync(m => m.UserId, ct).ConfigureAwait(false);

            foreach (var person in chunk)
            {
                known.TryGetValue(person.Key, out var row);

                var last = row?.LastVisitAt;
                DateTimeOffset? backAt = null;
                var backDays = 0;

                foreach (var day in person)
                {
                    if (last is { } before && day.FirstAt - before >= backAfter)
                    {
                        backAt = day.FirstAt;
                        backDays = (int)Math.Floor((day.FirstAt - before).TotalDays);
                    }

                    if (last is null || day.LastAt > last)
                        last = day.LastAt;
                }

                if (last == row?.LastVisitAt)
                    continue;

                await KeepAsync(person.Key, row?.LastVisitAt, last, backAt, backDays, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Writes the new last visit where the stored one is still <paramref name="was"/>. Another copy that got
    /// there first leaves this write out, and the overlap of the next read finds the visit again.
    /// </summary>
    private async Task KeepAsync(
        string userId, DateTimeOffset? was, DateTimeOffset? last, DateTimeOffset? backAt, int backDays, CancellationToken ct)
    {
        await _db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO member_milestone (user_id, last_visit_at, back_at, back_after_days)
            VALUES (@user, @last, @backAt, @backDays)
            ON CONFLICT (user_id) DO UPDATE SET
                last_visit_at = EXCLUDED.last_visit_at,
                back_at = COALESCE(EXCLUDED.back_at, member_milestone.back_at),
                back_after_days = CASE WHEN EXCLUDED.back_at IS NULL
                    THEN member_milestone.back_after_days ELSE EXCLUDED.back_after_days END
            WHERE member_milestone.last_visit_at IS NOT DISTINCT FROM @was
            """,
            [
                new NpgsqlParameter("user", userId),
                Time("last", last),
                Time("backAt", backAt),
                new NpgsqlParameter("backDays", NpgsqlDbType.Integer) { Value = backAt is null ? DBNull.Value : backDays },
                Time("was", was),
            ],
            ct).ConfigureAwait(false);
    }

    private static NpgsqlParameter Time(string name, DateTimeOffset? value)
        => new(name, NpgsqlDbType.TimestampTz) { Value = value is { } at ? at.ToUniversalTime() : DBNull.Value };

    /// <summary>One person's arrivals on one day.</summary>
    public sealed class VisitDay
    {
        public string UserId { get; set; } = string.Empty;

        public DateTimeOffset FirstAt { get; set; }

        public DateTimeOffset LastAt { get; set; }
    }
}
