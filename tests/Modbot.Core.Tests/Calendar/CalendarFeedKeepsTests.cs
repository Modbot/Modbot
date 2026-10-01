using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Calendar;

/// <summary>
/// Calendar design §6 (changed 2026-10-01): finished and cancelled events stay in the feed for a
/// while, a cancelled one says so, and each event links to itself in Modbot.
/// </summary>
public class CalendarFeedKeepsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static CalendarEvent Ended(TimeSpan endedAgo, string state = CalendarEventStates.Finished)
    {
        var e = CalendarRepeatTests.Event(Now - endedAgo - TimeSpan.FromHours(2), TimeSpan.FromHours(2));
        e.State = state;
        e.UpdatedAt = Now;
        return e;
    }

    [Fact]
    public void LiveEventsBelong_DraftsAndDeletedOnesDoNot()
    {
        var scheduled = Ended(-TimeSpan.FromDays(3), CalendarEventStates.Scheduled);
        var draft = Ended(-TimeSpan.FromDays(3), CalendarEventStates.Draft);
        var deleted = Ended(-TimeSpan.FromDays(3), CalendarEventStates.Cancelled);
        deleted.CancelledAt = Now;
        deleted.DeletedAt = Now;

        Assert.True(CalendarFeedWriter.Belongs(scheduled, Now));
        Assert.False(CalendarFeedWriter.Belongs(draft, Now));
        Assert.False(CalendarFeedWriter.Belongs(deleted, Now));
    }

    [Fact]
    public void AFinishedEventStaysForThirtyDaysAfterItsLastDate()
    {
        Assert.True(CalendarFeedWriter.Belongs(Ended(TimeSpan.FromDays(29)), Now));
        Assert.False(CalendarFeedWriter.Belongs(Ended(TimeSpan.FromDays(31)), Now));
    }

    [Fact]
    public void AFinishedRepeatingEventCountsFromItsLastDate_NotItsFirst()
    {
        // Every day for ten weeks, the last ending ten days ago.
        var last = Now - TimeSpan.FromDays(10);
        var e = CalendarRepeatTests.Event(
            last - TimeSpan.FromDays(70) - TimeSpan.FromHours(2),
            TimeSpan.FromHours(2),
            CalendarRepeats.Daily,
            until: DateOnly.FromDateTime((last - TimeSpan.FromHours(2)).UtcDateTime));
        e.State = CalendarEventStates.Finished;

        Assert.True(CalendarFeedWriter.Belongs(e, Now));
    }

    [Fact]
    public void ACancelledEventStaysForThirtyDaysAfterTheCancel_AsCancelled()
    {
        var recent = Ended(-TimeSpan.FromDays(5), CalendarEventStates.Cancelled);
        recent.CancelledAt = Now - TimeSpan.FromDays(2);

        var old = Ended(-TimeSpan.FromDays(5), CalendarEventStates.Cancelled);
        old.CancelledAt = Now - TimeSpan.FromDays(40);

        Assert.True(CalendarFeedWriter.Belongs(recent, Now));
        Assert.False(CalendarFeedWriter.Belongs(old, Now));

        var feed = CalendarFeedWriter.Write("Night Owls", [recent], new Dictionary<string, string>(), Now);
        Assert.Contains("STATUS:CANCELLED\r\n", feed, StringComparison.Ordinal);
        Assert.DoesNotContain("STATUS:CONFIRMED", feed, StringComparison.Ordinal);
    }

    [Fact]
    public void EachEventLinksToItselfInModbot_WhenThereIsAPublicAddress()
    {
        var e = Ended(-TimeSpan.FromDays(3), CalendarEventStates.Scheduled);

        var linked = CalendarFeedWriter.Write("Night Owls", [e], new Dictionary<string, string>(), Now, "https://modbot.example/");
        Assert.Contains($"URL:https://modbot.example/calendar?event={e.Id:D}\r\n", linked, StringComparison.Ordinal);

        var unlinked = CalendarFeedWriter.Write("Night Owls", [e], new Dictionary<string, string>(), Now, publicAddress: null);
        Assert.DoesNotContain("URL:", unlinked, StringComparison.Ordinal);
    }

    [Fact]
    public void ADateCancelledOnItsOwnIsAnExdate_NotACancelledCopy()
    {
        var e = CalendarRepeatTests.Event(Now + TimeSpan.FromDays(1), TimeSpan.FromHours(2), CalendarRepeats.Daily);
        e.UpdatedAt = Now;
        e.DateChanges.Add(new CalendarDateChange
        {
            Id = Guid.CreateVersion7(),
            EventId = e.Id,
            PlannedStartsAt = e.StartsAt + TimeSpan.FromDays(1),
            Cancelled = true,
            CreatedAt = Now,
            UpdatedAt = Now,
        });

        var feed = CalendarFeedWriter.Write("Night Owls", [e], new Dictionary<string, string>(), Now);

        Assert.Contains("EXDATE:20261003T120000Z\r\n", feed, StringComparison.Ordinal);
        Assert.DoesNotContain("RECURRENCE-ID", feed, StringComparison.Ordinal);
        Assert.DoesNotContain("STATUS:CANCELLED", feed, StringComparison.Ordinal);
    }
}
