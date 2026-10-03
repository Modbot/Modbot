using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Calendar;

/// <summary>
/// Calendar design §2.2: one date of a repeating event cancelled, moved or reworded on its own, and
/// every list of dates and the feed following it.
/// </summary>
public class CalendarDatesTests
{
    private static readonly TimeSpan Two = TimeSpan.FromHours(2);

    // Sundays at 20:00 in London: 19:00 UTC through October 18, 20:00 UTC from October 25.
    private static readonly DateTimeOffset Sep20 = new(2026, 9, 20, 19, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Sep27 = new(2026, 9, 27, 19, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Oct4 = new(2026, 10, 4, 19, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Oct11 = new(2026, 10, 11, 19, 0, 0, TimeSpan.Zero);

    private static CalendarEvent Weekly() =>
        CalendarRepeatTests.Event(Sep20, Two, CalendarRepeats.Weekly, "Europe/London", null, "SU");

    private static CalendarDateChange Change(CalendarEvent e, DateTimeOffset planned) => new()
    {
        Id = Guid.CreateVersion7(),
        EventId = e.Id,
        PlannedStartsAt = planned,
        CreatedAt = Sep20,
        UpdatedAt = Sep20,
    };

    [Fact]
    public void ACancelledDateIsLeftOut()
    {
        var e = Weekly();
        var change = Change(e, Sep27);
        change.Cancelled = true;
        e.DateChanges.Add(change);

        var starts = CalendarRepeat.Between(e, Sep20, Oct11 + TimeSpan.FromHours(1)).Select(o => o.StartsAt).ToList();

        Assert.Equal([Sep20, Oct4, Oct11], starts);
    }

    [Fact]
    public void AMovedDateIsAtItsNewTimeAndInOrder()
    {
        var e = Weekly();
        var monday = new DateTimeOffset(2026, 10, 5, 19, 0, 0, TimeSpan.Zero);
        var change = Change(e, Sep27);
        change.StartsAt = monday;
        change.EndsAt = monday + TimeSpan.FromHours(3);
        e.DateChanges.Add(change);

        var dates = CalendarRepeat.Between(e, Sep20, Oct11 + TimeSpan.FromHours(1)).ToList();

        Assert.Equal([Sep20, Oct4, monday, Oct11], dates.Select(o => o.StartsAt));

        var moved = dates[2];
        Assert.Equal(Sep27, moved.PlannedStartsAt);
        Assert.Equal(monday + TimeSpan.FromHours(3), moved.EndsAt);
        Assert.Same(change, moved.Change);

        // Every other date is known by its own start.
        Assert.All(dates.Where(o => o.Change is null), o => Assert.Equal(o.StartsAt, o.PlannedStartsAt));
    }

    [Fact]
    public void ADateWithOnlyItsOwnTitleKeepsItsTime()
    {
        var e = Weekly();
        var change = Change(e, Sep27);
        change.Title = "Halloween special";
        e.DateChanges.Add(change);

        var date = CalendarRepeat.ForDate(e, Sep27);

        Assert.NotNull(date);
        Assert.Equal(Sep27, date.Value.StartsAt);
        Assert.Equal(Sep27 + Two, date.Value.EndsAt);
        Assert.Equal("Halloween special", CalendarRepeat.TitleOf(e, date.Value));
        Assert.Equal("Event", CalendarRepeat.TitleOf(e, CalendarRepeat.ForDate(e, Oct4)!.Value));
    }

    [Fact]
    public void TheNextDateSkipsACancelledOne()
    {
        var e = Weekly();
        var change = Change(e, Sep27);
        change.Cancelled = true;
        e.DateChanges.Add(change);

        // Just after the first date ended: the next one is a week after the cancelled one.
        var now = Sep20 + TimeSpan.FromHours(3);
        Assert.Equal(Oct4, CalendarRepeat.Next(e, now)!.Value.StartsAt);

        CalendarTimeline.Advance(e, now);
        Assert.Equal(Oct4, e.OccurrenceStartsAt);
        Assert.Equal(CalendarEventStates.Scheduled, e.State);
    }

    [Fact]
    public void AMovedDateOpensAtItsNewTime()
    {
        var e = Weekly();
        var later = Sep27 + TimeSpan.FromHours(2);
        var change = Change(e, Sep27);
        change.StartsAt = later;
        change.EndsAt = later + Two;
        e.DateChanges.Add(change);

        // At the planned time the moved date has not started.
        CalendarTimeline.Advance(e, Sep27 + TimeSpan.FromMinutes(1));
        Assert.Equal(later, e.OccurrenceStartsAt);
        Assert.Equal(CalendarEventStates.Scheduled, e.State);

        Assert.Equal(CalendarStep.Opened, CalendarTimeline.Advance(e, later));
        Assert.Equal(CalendarEventStates.Open, e.State);

        // The event's current date is known again by its planned start.
        Assert.Equal(Sep27, CalendarRepeat.Current(e).PlannedStartsAt);
    }

    [Fact]
    public void OnlyTheRepeatsOwnDatesArePlannedDates()
    {
        var e = Weekly();

        Assert.True(CalendarRepeat.IsPlannedDate(e, Sep27));
        Assert.False(CalendarRepeat.IsPlannedDate(e, Sep27 + TimeSpan.FromHours(1)));
        Assert.False(CalendarRepeat.IsPlannedDate(e, new DateTimeOffset(2026, 9, 28, 19, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void CancelledDatesAreListedAtTheirPlannedTimes()
    {
        var e = Weekly();
        var change = Change(e, Oct4);
        change.Cancelled = true;
        e.DateChanges.Add(change);

        var cancelled = CalendarRepeat.CancelledBetween(e, Sep20, Oct11).ToList();

        Assert.Single(cancelled);
        Assert.Equal(Oct4, cancelled[0].StartsAt);
        Assert.Equal(Oct4 + Two, cancelled[0].EndsAt);
    }

    [Fact]
    public void MovingTheSeriesAnHourKeepsACancelledDateCancelled()
    {
        var e = Weekly();
        var zone = CalendarRepeat.ZoneOf(e);
        var change = Change(e, Oct4);
        change.Cancelled = true;
        change.VRChatSentFingerprint = "sent";
        change.VRChatId = "occ_4";
        e.DateChanges.Add(change);

        e.StartsAt += TimeSpan.FromHours(1);
        e.EndsAt += TimeSpan.FromHours(1);
        CalendarDates.Rematch(e, zone, Sep20);

        var kept = Assert.Single(e.DateChanges);
        Assert.Equal(Oct4 + TimeSpan.FromHours(1), kept.PlannedStartsAt);
        Assert.True(kept.Cancelled);

        // Moved to another date: VRChat hears about it again.
        Assert.Null(kept.VRChatSentFingerprint);
        Assert.Null(kept.VRChatId);
        Assert.DoesNotContain(CalendarRepeat.Between(e, Sep20, Oct11), o => o.StartsAt == Oct4 + TimeSpan.FromHours(1));
    }

    [Fact]
    public void ADayTheRepeatNoLongerHasDropsItsChange()
    {
        var e = Weekly();
        var zone = CalendarRepeat.ZoneOf(e);
        var change = Change(e, Oct4);
        change.Cancelled = true;
        e.DateChanges.Add(change);

        // Sundays become Mondays.
        e.StartsAt += TimeSpan.FromDays(1);
        e.EndsAt += TimeSpan.FromDays(1);
        e.RepeatDays = ["MO"];
        CalendarDates.Rematch(e, zone, Sep20);

        Assert.Empty(e.DateChanges);
    }

    [Fact]
    public void ADateThatIsOverIsLeftAsItRan()
    {
        var e = Weekly();
        var zone = CalendarRepeat.ZoneOf(e);
        var change = Change(e, Sep27);
        change.Title = "What it was called";
        e.DateChanges.Add(change);

        e.RepeatDays = ["MO"];
        e.StartsAt = new DateTimeOffset(2026, 10, 5, 19, 0, 0, TimeSpan.Zero);
        e.EndsAt = e.StartsAt + Two;
        CalendarDates.Rematch(e, zone, Oct4);

        var kept = Assert.Single(e.DateChanges);
        Assert.Equal(Sep27, kept.PlannedStartsAt);
    }

    [Fact]
    public void APlainDateIsForgottenOnlyWhenNeitherVRChatNorGoogleHoldsIt()
    {
        var e = Weekly();
        var change = Change(e, Sep27);

        Assert.True(CalendarDates.CanForget(change, Two));

        change.GoogleSentFingerprint = "sent";
        Assert.True(CalendarDates.MayBeOnGoogle(change));
        Assert.False(CalendarDates.CanForget(change, Two));

        CalendarDates.ForgetOnGoogle(change);
        change.VRChatSentFingerprint = "sent";
        change.VRChatId = "occ_2";
        Assert.False(CalendarDates.CanForget(change, Two));

        // VRChat sent the planned date back: it holds it as planned, which does not keep the row.
        CalendarDates.PlannedOnVRChat(change);
        Assert.Equal(CalendarDates.VRChatHasPlannedDate, change.VRChatSentFingerprint);
        Assert.Null(change.VRChatId);
        Assert.False(CalendarDates.MayBeOnVRChat(change));
        Assert.True(CalendarDates.CanForget(change, Two));
    }

    [Fact]
    public void MovingTheSeriesKeepsAPlainDateGoogleHolds_AndSendsItToGoogleAgain()
    {
        var e = Weekly();
        var zone = CalendarRepeat.ZoneOf(e);
        var change = Change(e, Oct4);
        change.GoogleSentFingerprint = "sent";
        change.GoogleError = "Refused";
        change.GoogleErrorAt = Sep20;
        e.DateChanges.Add(change);

        e.StartsAt += TimeSpan.FromHours(1);
        e.EndsAt += TimeSpan.FromHours(1);
        CalendarDates.Rematch(e, zone, Sep20);

        // Kept rather than dropped, so Google's copy is not left behind; matched to another date,
        // Google's state for it is cleared like VRChat's.
        var kept = Assert.Single(e.DateChanges);
        Assert.Equal(Oct4 + TimeSpan.FromHours(1), kept.PlannedStartsAt);
        Assert.Null(kept.GoogleSentFingerprint);
        Assert.Null(kept.GoogleError);
        Assert.Null(kept.GoogleErrorAt);
    }

    [Fact]
    public void AChangeThatChangesNothingIsPlain()
    {
        var e = Weekly();
        var change = Change(e, Sep27);

        Assert.True(CalendarDates.IsPlain(change, Two));

        change.EndsAt = Sep27 + Two;
        change.StartsAt = Sep27;
        Assert.True(CalendarDates.IsPlain(change, Two));

        change.EndsAt = Sep27 + Two + TimeSpan.FromMinutes(30);
        Assert.False(CalendarDates.IsPlain(change, Two));
    }

    [Fact]
    public void TheFeedLeavesOutACancelledDateAndWritesAMovedOne()
    {
        var e = Weekly();
        e.Version = 4;
        e.UpdatedAt = Sep20;

        var cancelled = Change(e, Oct4);
        cancelled.Cancelled = true;
        e.DateChanges.Add(cancelled);

        var later = Sep27 + TimeSpan.FromHours(1);
        var moved = Change(e, Sep27);
        moved.StartsAt = later;
        moved.EndsAt = later + Two;
        moved.Title = "Late start";
        e.DateChanges.Add(moved);

        var feed = CalendarFeedWriter.Write("Night Owls", [e], new Dictionary<string, string>(), Sep20);

        Assert.Contains("RRULE:FREQ=WEEKLY;BYDAY=SU\r\n", feed, StringComparison.Ordinal);
        Assert.Contains("EXDATE;TZID=Europe/London:20261004T200000\r\n", feed, StringComparison.Ordinal);

        // The moved date is its own VEVENT under the same UID, naming the date it replaces.
        var events = feed.Split("BEGIN:VEVENT\r\n").Skip(1).ToList();
        Assert.Equal(2, events.Count);

        var own = events[1];
        Assert.Contains($"UID:{e.Id:D}@modbot\r\n", own, StringComparison.Ordinal);
        Assert.Contains("RECURRENCE-ID;TZID=Europe/London:20260927T200000\r\n", own, StringComparison.Ordinal);
        Assert.Contains("DTSTART;TZID=Europe/London:20260927T210000\r\n", own, StringComparison.Ordinal);
        Assert.Contains("DTEND;TZID=Europe/London:20260927T230000\r\n", own, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Late start\r\n", own, StringComparison.Ordinal);
        Assert.DoesNotContain("RRULE", own, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEventWithNoChangesWritesNoExceptions()
    {
        var e = Weekly();
        e.UpdatedAt = Sep20;

        var feed = CalendarFeedWriter.Write("Night Owls", [e], new Dictionary<string, string>(), Sep20);

        Assert.DoesNotContain("EXDATE", feed, StringComparison.Ordinal);
        Assert.DoesNotContain("RECURRENCE-ID", feed, StringComparison.Ordinal);
    }

    [Fact]
    public void TheZoneOfADateMovedPastTheLastOneIsWritten()
    {
        var e = CalendarRepeatTests.Event(Sep20, Two, CalendarRepeats.Weekly, "Europe/London", new DateOnly(2026, 10, 4), "SU");
        e.UpdatedAt = Sep20;

        // The last date, moved past the clocks going back.
        var moved = Change(e, Oct4);
        moved.StartsAt = new DateTimeOffset(2026, 11, 1, 20, 0, 0, TimeSpan.Zero);
        moved.EndsAt = moved.StartsAt + Two;
        e.DateChanges.Add(moved);

        var feed = CalendarFeedWriter.Write("Night Owls", [e], new Dictionary<string, string>(), Sep20);

        Assert.Contains("BEGIN:STANDARD", feed, StringComparison.Ordinal);
        Assert.Contains("DTSTART;TZID=Europe/London:20261101T200000\r\n", feed, StringComparison.Ordinal);
    }
}
