using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Calendar;
using Modbot.VRChat.Tests.Sync;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>
/// The calendar's VRChat side over a real database, a real gate and limiter, and a scripted VRChat.
/// </summary>
/// <remarks>
/// Each pass gets a fresh context, the way a scoped service does, so nothing passes on entities the
/// change tracker still remembered from the pass before -- which is exactly what a restart forgets.
/// </remarks>
public abstract class CalendarTestBase(PostgresFixture fixture) : SyncTestBase(fixture)
{
    protected const string WorldId = "wrld_calendar";

    protected async Task<CalendarPublishResult> PublishAsync()
    {
        await using var context = Database.NewContext();
        return await new CalendarVRChatPublisher(Gate, context, Clock, Facts(context)).RunOnceAsync(Ct);
    }

    protected async Task<CalendarOpenerResult> OpenAsync()
    {
        await using var context = Database.NewContext();
        return await new CalendarOpener(Gate, context, new PlaceStore(context, Clock), Clock, Facts(context)).RunOnceAsync(Ct);
    }

    protected async Task<int> ScheduleAsync()
    {
        await using var context = Database.NewContext();
        return await new CalendarScheduler(context, Clock, Facts(context)).RunOnceAsync(Ct);
    }

    protected CalendarFacts Facts(ModbotContext context) =>
        new(new FactWriter(context, Clock), new EventPartitionMaintainer(context, Clock), Clock);

    /// <summary>A scheduled event starting <paramref name="startsIn"/> from now, saved.</summary>
    protected async Task<CalendarEvent> AddEventAsync(TimeSpan startsIn, Action<CalendarEvent>? shape = null)
    {
        var now = Clock.UtcNow;
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = "Movie night",
            Description = "Bring snacks",
            StartsAt = now + startsIn,
            EndsAt = now + startsIn + TimeSpan.FromHours(2),
            TimeZone = "UTC",
            WorldId = WorldId,
            State = CalendarEventStates.Scheduled,
            CreatedAt = now,
            UpdatedAt = now,
        };

        shape?.Invoke(e);

        await using var context = Database.NewContext();
        context.CalendarEvents.Add(e);
        await context.SaveChangesAsync(Ct);
        return e;
    }

    /// <summary>Changes a saved event the way the API does: the change, a new version, a new change time.</summary>
    protected async Task EditAsync(Guid id, Action<CalendarEvent> change)
    {
        await using var context = Database.NewContext();
        var e = await context.CalendarEvents.SingleAsync(x => x.Id == id, Ct);
        change(e);
        e.Version++;
        e.UpdatedAt = Clock.UtcNow;
        await context.SaveChangesAsync(Ct);
    }

    protected async Task<CalendarEventPlace?> PlaceAsync(Guid id, string place)
    {
        await using var context = Database.NewContext();
        return await context.CalendarEventPlaces.AsNoTracking().SingleOrDefaultAsync(p => p.EventId == id && p.Place == place, Ct);
    }
}
