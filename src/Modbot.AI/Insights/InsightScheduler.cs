using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.AI.Insights;

/// <summary>
/// Writes each scheduled insight whose moment has come, once (AI insights design §3).
/// </summary>
public sealed class InsightScheduler(ModbotContext db, InsightWriter writer, IModbotClock clock)
{
    /// <summary>
    /// How long a due insight waits for the daily totals to catch up with the last day it covers.
    /// The totals run every quarter-hour, so this is only reached when they have stalled -- and a
    /// late insight on slightly short figures beats none.
    /// </summary>
    public static readonly TimeSpan LongestWaitForTotals = TimeSpan.FromHours(1);

    /// <summary>Runs every kind that is due. Returns how many insights were stored.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;

        var settings = await db.InsightSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var zone = InsightTimes.ZoneOrUtc(settings?.TimeZone);

        var schedules = await db.InsightSchedules.AsNoTracking().Where(s => s.Enabled).ToListAsync(ct);
        var totalsThrough = await db.DailyTotalsState.AsNoTracking()
            .Where(s => s.Id == 1).Select(s => s.ObservedThrough).FirstOrDefaultAsync(ct);

        var written = 0;

        foreach (var schedule in schedules.Where(s => InsightKinds.IsKnown(s.Kind)))
        {
            var moment = InsightTimes.LatestMoment(now, schedule.Every, schedule.Hour, schedule.Weekday, zone);

            if (schedule.HandledThrough is { } handled && handled >= moment)
                continue;

            var today = InsightPeriod.DayOf(moment);
            var totalsReady = totalsThrough is { } through && through >= InsightPeriod.DayStart(today);

            if (!totalsReady && now - moment < LongestWaitForTotals)
                continue;

            // Claimed before writing, so two runs that overlap -- or two copies of Modbot pointed at
            // one database -- cannot both write it. A failed call is not tried again until the next
            // moment: a provider that is down would otherwise be asked every minute.
            var claimed = await db.InsightSchedules
                .Where(s => s.Kind == schedule.Kind && (s.HandledThrough == null || s.HandledThrough < moment))
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.HandledThrough, moment), ct);

            if (claimed == 0)
                continue;

            var insight = await writer.WriteAsync(
                schedule.Kind, schedule.Every, today, InsightStart.Schedule(schedule.DiscordChannelId), ct);

            if (insight is not null)
                written++;
        }

        return written;
    }

    /// <summary>
    /// Marks every moment already past as dealt with, so saving a schedule never writes one straight
    /// away for a time that went by before it was turned on.
    /// </summary>
    public static void MarkPastMomentsHandled(IEnumerable<InsightSchedule> schedules, string? timeZone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedules);

        var zone = InsightTimes.ZoneOrUtc(timeZone);

        foreach (var schedule in schedules)
            schedule.HandledThrough = InsightTimes.LatestMoment(now, schedule.Every, schedule.Hour, schedule.Weekday, zone);
    }
}
