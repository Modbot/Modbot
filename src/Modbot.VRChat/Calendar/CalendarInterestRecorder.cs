using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;
using CalendarEvent = Modbot.Core.Data.Entities.CalendarEvent;
using VRChatEvent = VRChat.API.Model.CalendarEvent;

namespace Modbot.VRChat.Calendar;

/// <summary>
/// Writes down how many people are interested on VRChat, once, when a date of an event has
/// started: the count VRChat's calendar shows on that date.
/// </summary>
/// <remarks>
/// <para>
/// The count is a field of the calendar rows Modbot already reads, so there is no new endpoint. The
/// group's calendar is read for the month of each date running now that has no count yet (at most
/// three pages a month, at background priority, so a moderator's request goes first), and not at
/// all the rest of the time.
/// </para>
/// <para>
/// Each date is written once (<see cref="CalendarInterestStore"/>). A date VRChat's calendar does not
/// list is written with no count once every page was read, so it is not looked for again. A read that
/// stopped short, with the date not among the rows, is made again on the next pass, but only
/// <see cref="MostMisses"/> times: then the date is written with no count. A read VRChat failed (not
/// a rate limit or a sign-in that is waiting) is made again after 5, then 10, then 20 minutes for
/// those dates. The counters live in memory, so a restart starts them again.
/// </para>
/// </remarks>
public sealed class CalendarInterestRecorder
{
    /// <summary>How far apart VRChat's start and Modbot's may be and still be the same date.</summary>
    private static readonly TimeSpan SameDate = TimeSpan.FromMinutes(1);

    /// <summary>How many reads that stopped short may miss a date before it is written with no count.</summary>
    internal const int MostMisses = 3;

    /// <summary>The wait after a failed read: 5 minutes, doubling to at most 20.</summary>
    internal static TimeSpan BackOff(int failures) =>
        TimeSpan.FromMinutes(Math.Min(20, 5 * Math.Pow(2, Math.Max(0, failures - 1))));

    private sealed class Tries
    {
        public int Misses;
        public int Failures;
        public DateTimeOffset NotBefore;
    }

    /// <summary>What has been tried for each date still waiting for a count. Shared by every pass.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid EventId, DateTimeOffset StartsAt), Tries> Waiting = new();

    private readonly IVRChatGate _gate;
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly CalendarInterestStore _store;
    private readonly ILogger _log;

    public CalendarInterestRecorder(IVRChatGate gate, ModbotContext db, IModbotClock clock, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _gate = gate;
        _db = db;
        _clock = clock;
        _store = new CalendarInterestStore(db);
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <returns>How many counts were written.</returns>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (settings.ManagedGroupId is not { Length: > 0 } groupId)
            return 0;

        var now = _clock.UtcNow;

        var places = await _db.CalendarEventPlaces.AsNoTracking()
            .Where(p => p.Place == CalendarPlaces.VRChat && p.ExternalId != null)
            .ToListAsync(ct).ConfigureAwait(false);

        if (places.Count == 0)
            return 0;

        var ids = places.Select(p => p.EventId).ToList();

        var events = await _db.CalendarEvents.AsNoTracking()
            .Where(e => ids.Contains(e.Id) && e.DeletedAt == null && e.PublishToVRChat)
            .ToDictionaryAsync(e => e.Id, ct).ConfigureAwait(false);

        var due = new List<(CalendarEvent Event, string ExternalId, DateTimeOffset StartsAt)>();

        foreach (var place in places)
        {
            if (!events.TryGetValue(place.EventId, out var calendarEvent) || !CalendarEventStates.IsLive(calendarEvent.State))
                continue;

            var occurrence = CalendarRepeat.Current(calendarEvent);

            if (now < occurrence.StartsAt || now >= occurrence.EndsAt)
                continue;

            var taken = await _db.CalendarInterests.AsNoTracking()
                .AnyAsync(i => i.EventId == place.EventId
                    && i.OccurrenceStartsAt == occurrence.StartsAt
                    && i.VRChatTakenAt != null, ct).ConfigureAwait(false);

            if (taken)
            {
                Waiting.TryRemove((place.EventId, occurrence.StartsAt), out _);
                continue;
            }

            if (Waiting.TryGetValue((place.EventId, occurrence.StartsAt), out var tries) && now < tries.NotBefore)
                continue;

            due.Add((calendarEvent, place.ExternalId!, occurrence.StartsAt));
        }

        // Dates that ended long ago are not waited for any more.
        foreach (var key in Waiting.Keys.Where(k => k.StartsAt < now - TimeSpan.FromDays(1)).ToList())
            Waiting.TryRemove(key, out _);

        if (due.Count == 0)
            return 0;

        var rows = new List<VRChatEvent>();
        var whole = true;

        foreach (var month in due.SelectMany(d => CalendarVRChatReader.MonthsListing(d.StartsAt)).Distinct())
        {
            var read = await CalendarVRChatReader.ReadMonthPagesAsync(_gate, groupId, month, VRChatCallPriority.Background, ct)
                .ConfigureAwait(false);

            if (read.Failed is { } failed)
            {
                // The gate is cold or a sign-in is waiting: the next pass asks again. Anything else
                // VRChat refused is asked again later and later, per date.
                if (failed.Kind is not (VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting))
                {
                    foreach (var d in due)
                    {
                        var tries = Waiting.GetOrAdd((d.Event.Id, d.StartsAt), _ => new Tries());
                        tries.Failures++;
                        tries.NotBefore = now + BackOff(tries.Failures);
                    }
                }

                return 0;
            }

            rows.AddRange(read.Rows);
            whole &= read.Whole;
        }

        var written = 0;

        foreach (var (calendarEvent, externalId, startsAt) in due)
        {
            var row = rows
                .Where(r => !r.IsDraft
                    && r.DeletedAt is null
                    && string.Equals(CalendarVRChatReader.KeyOf(r), externalId, StringComparison.Ordinal)
                    && (CalendarVRChatMatch.AsUtc(r.StartsAt) - startsAt).Duration() < SameDate)
                .OrderBy(r => (CalendarVRChatMatch.AsUtc(r.StartsAt) - startsAt).Duration())
                .FirstOrDefault();

            // Not in what was read, and not all of it was: it may be on a page that was not. After a
            // few such reads it is written with no count, so VRChat is not asked for it all event long.
            if (row is null && !whole)
            {
                var tries = Waiting.GetOrAdd((calendarEvent.Id, startsAt), _ => new Tries());

                if (++tries.Misses < MostMisses)
                    continue;
            }

            if (await _store.RecordVRChatAsync(calendarEvent.Id, startsAt, row?.InterestedUserCount, now, ct).ConfigureAwait(false))
                written++;

            Waiting.TryRemove((calendarEvent.Id, startsAt), out _);
        }

        if (written > 0)
            _log.Information("Wrote down VRChat's interested count for {Dates} dates", written);

        return written;
    }
}
