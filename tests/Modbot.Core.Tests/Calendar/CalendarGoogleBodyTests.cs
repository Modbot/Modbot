using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Calendar;

/// <summary>
/// Google Calendar design §3.3 and §3.5: what an event is sent to Google as, written by the same code
/// as the calendar feed, and which events go at all.
/// </summary>
public class CalendarGoogleBodyTests
{
    private static readonly TimeSpan Two = TimeSpan.FromHours(2);

    // Sunday 20:00 in London: 19:00 UTC in September (summer time), 20:00 UTC from October 25.
    private static readonly DateTimeOffset Sep20 = new(2026, 9, 20, 19, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, string> NoNames = [];

    private static CalendarEvent Event(string repeat = CalendarRepeats.None, string zone = "Europe/London", params string[] days)
    {
        var e = CalendarRepeatTests.Event(Sep20, Two, repeat, zone, null, days);
        e.Visibility = "public";
        e.PublishToGoogle = true;
        e.Description = "Bring snacks";
        return e;
    }

    private static CalendarDateChange Change(CalendarEvent e, DateTimeOffset planned) => new()
    {
        Id = Guid.CreateVersion7(),
        EventId = e.Id,
        PlannedStartsAt = planned,
        CreatedAt = Sep20,
        UpdatedAt = Sep20,
    };

    [Fact]
    public void ARepeatIsTheFeedsOwnRule_WeeklyOnDays()
    {
        var e = Event(CalendarRepeats.Weekly, "Europe/London", "SU", "WE");

        var body = CalendarGoogleBody.For(e, NoNames, "Group");

        Assert.Equal(["RRULE:FREQ=WEEKLY;BYDAY=WE,SU"], body.Recurrence);
        Assert.Equal("RRULE:" + CalendarFeedWriter.Rule(e, CalendarRepeat.ZoneOf(e)), body.Recurrence[0]);
    }

    [Fact]
    public void EveryOtherWeekCountsWeeksFromMonday()
    {
        var e = Event(CalendarRepeats.Weekly, "Europe/London", "SU");
        e.RepeatEvery = 2;

        Assert.Equal(["RRULE:FREQ=WEEKLY;BYDAY=SU;INTERVAL=2;WKST=MO"], CalendarGoogleBody.For(e, NoNames, null).Recurrence);
    }

    [Fact]
    public void ALastDayAndANumberOfTimesAreWrittenAsTheFeedWritesThem()
    {
        var until = Event(CalendarRepeats.Weekly, "Europe/London", "SU");
        until.RepeatUntil = new DateOnly(2026, 12, 31);

        // The last moment of December 31 in London, in UTC.
        Assert.Equal(["RRULE:FREQ=WEEKLY;BYDAY=SU;UNTIL=20261231T235959Z"], CalendarGoogleBody.For(until, NoNames, null).Recurrence);

        var times = Event(CalendarRepeats.Weekly, "Europe/London", "SU");
        times.RepeatTimes = 5;

        Assert.Equal(["RRULE:FREQ=WEEKLY;BYDAY=SU;COUNT=5"], CalendarGoogleBody.For(times, NoNames, null).Recurrence);
    }

    [Fact]
    public void MonthlyOnThe31st()
    {
        var e = CalendarRepeatTests.Event(new DateTimeOffset(2026, 10, 31, 19, 0, 0, TimeSpan.Zero), Two, CalendarRepeats.Monthly, "UTC");
        e.Visibility = "public";

        var body = CalendarGoogleBody.For(e, NoNames, null);

        Assert.Equal(["RRULE:FREQ=MONTHLY"], body.Recurrence);
        Assert.Equal("2026-10-31T19:00:00+00:00", body.ToJson("mbtest0")["start"]!["dateTime"]!.GetValue<string>());
    }

    [Fact]
    public void TimesCarryTheZonesOffsetOnTheDay_AndTheZone()
    {
        var e = Event();
        var json = CalendarGoogleBody.For(e, NoNames, null).ToJson("mbtest0");

        Assert.Equal("2026-09-20T20:00:00+01:00", json["start"]!["dateTime"]!.GetValue<string>());
        Assert.Equal("Europe/London", json["start"]!["timeZone"]!.GetValue<string>());
        Assert.Equal("2026-09-20T22:00:00+01:00", json["end"]!["dateTime"]!.GetValue<string>());

        // After the clocks go back, the same wall-clock time is a different offset.
        var winter = CalendarGoogleBody.TimeOf(new DateTimeOffset(2026, 11, 1, 20, 0, 0, TimeSpan.Zero), "Europe/London");
        Assert.Equal("2026-11-01T20:00:00+00:00", winter["dateTime"]!.GetValue<string>());
    }

    [Fact]
    public void ACancelledDateIsAnExdateInTheEventsZone_OrInUtcWithNoZone()
    {
        var london = Event(CalendarRepeats.Weekly, "Europe/London", "SU");
        var cancelled = Change(london, Sep20.AddDays(7));
        cancelled.Cancelled = true;
        london.DateChanges.Add(cancelled);

        Assert.Equal(
            ["RRULE:FREQ=WEEKLY;BYDAY=SU", "EXDATE;TZID=Europe/London:20260927T200000"],
            CalendarGoogleBody.For(london, NoNames, null).Recurrence);

        var utc = Event(CalendarRepeats.Weekly, "UTC", "SU");
        var dropped = Change(utc, Sep20.AddDays(7));
        dropped.Cancelled = true;
        utc.DateChanges.Add(dropped);

        Assert.Equal("EXDATE:20260927T190000Z", CalendarGoogleBody.For(utc, NoNames, null).Recurrence[1]);
    }

    [Fact]
    public void TheDescriptionIsEscaped_KeepsItsLines_AndEndsWithTheWorldPage()
    {
        var e = Event();
        e.Description = "<b>Tom & Jerry</b>\nSecond line";
        e.WorldId = "wrld_abc";

        var body = CalendarGoogleBody.For(e, new Dictionary<string, string> { ["wrld_abc"] = "The Black Cat" }, "Group");

        Assert.Equal(
            "&lt;b&gt;Tom &amp; Jerry&lt;/b&gt;\nSecond line\n\nWorld: The Black Cat\nhttps://vrchat.com/home/world/wrld_abc",
            body.Description);
        Assert.Equal("The Black Cat", body.Location);
        Assert.Equal("https://vrchat.com/home/world/wrld_abc", body.SourceUrl);
        Assert.Equal("Group", body.SourceTitle);
    }

    [Fact]
    public void WithNoWorldThePlaceIsVRChat_AndThereIsNoLink()
    {
        var e = Event();
        e.WorldId = null;

        var body = CalendarGoogleBody.For(e, NoNames, "Group");

        Assert.Equal("VRChat", body.Location);
        Assert.Null(body.SourceUrl);
        Assert.Equal("Bring snacks", body.Description);
        Assert.False(body.ToJson("mbtest0").ContainsKey("source"));
    }

    [Fact]
    public void TheFingerprintLeavesOutWhetherTheDateIsOpen()
    {
        var e = Event();
        var scheduled = CalendarGoogleBody.For(e, NoNames, null).Fingerprint("cal");

        e.State = CalendarEventStates.Open;
        e.OccurrenceStartsAt = e.StartsAt;

        Assert.Equal(scheduled, CalendarGoogleBody.For(e, NoNames, null).Fingerprint("cal"));
        Assert.NotEqual(scheduled, CalendarGoogleBody.For(e, NoNames, null).Fingerprint("another calendar"));
    }

    [Fact]
    public void ACancelledEventSaysSo_AndARepeatingOneStopsAtTheDateItWasCancelledOn()
    {
        var e = Event(CalendarRepeats.Weekly, "Europe/London", "SU");
        e.State = CalendarEventStates.Cancelled;
        e.OccurrenceStartsAt = Sep20.AddDays(14);

        var body = CalendarGoogleBody.For(e, NoNames, null);

        Assert.Equal("Cancelled: Event", body.Summary);
        Assert.Equal("RRULE:FREQ=WEEKLY;BYDAY=SU;UNTIL=20261004T225959Z", body.Recurrence[0]);
    }

    [Fact]
    public void OneDateIsCancelled_PutBack_OrChanged()
    {
        var e = Event(CalendarRepeats.Weekly, "Europe/London", "SU");
        var planned = Sep20.AddDays(7);

        var cancelled = Change(e, planned);
        cancelled.Cancelled = true;
        Assert.Equal(CalendarGoogleDateKind.Cancel, CalendarGoogleBody.DateFor(e, cancelled, NoNames).Kind);

        var plain = Change(e, planned);
        var back = CalendarGoogleBody.DateFor(e, plain, NoNames);
        Assert.Equal(CalendarGoogleDateKind.Planned, back.Kind);
        Assert.Equal(planned, back.StartsAt);
        Assert.Equal("Event", back.Summary);

        var moved = Change(e, planned);
        moved.StartsAt = planned.AddHours(1);
        moved.Title = "Double feature";
        var changed = CalendarGoogleBody.DateFor(e, moved, NoNames);
        Assert.Equal(CalendarGoogleDateKind.Changed, changed.Kind);
        Assert.Equal(planned.AddHours(1), changed.StartsAt);
        Assert.Equal("Double feature", changed.Summary);
        Assert.NotEqual(back.Fingerprint, changed.Fingerprint);

        // Sent back as Google gave the date, with only its times, words and status changed.
        var instance = new System.Text.Json.Nodes.JsonObject { ["id"] = "x_1", ["recurringEventId"] = "x", ["status"] = "confirmed" };
        var put = changed.ApplyTo(instance);
        Assert.Equal("x", put["recurringEventId"]!.GetValue<string>());
        Assert.Equal("Double feature", put["summary"]!.GetValue<string>());
        Assert.Equal("cancelled", CalendarGoogleBody.DateFor(e, cancelled, NoNames).ApplyTo(instance)["status"]!.GetValue<string>());
    }

    [Fact]
    public void AnEventIsReadBackAsModbotsOwnOnlyByItsOwnId()
    {
        var e = Event();
        var json = CalendarGoogleBody.For(e, NoNames, null).ToJson("mbtest0");

        Assert.True(CalendarGoogleBody.IsOwnedBy(json, e.Id));
        Assert.False(CalendarGoogleBody.IsOwnedBy(json, Guid.NewGuid()));
        Assert.False(CalendarGoogleBody.IsOwnedBy(new System.Text.Json.Nodes.JsonObject(), e.Id));
    }
}
