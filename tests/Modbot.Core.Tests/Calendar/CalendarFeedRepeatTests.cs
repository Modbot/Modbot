using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Calendar;

/// <summary>
/// Calendar design §6 (2026-10-02): every N days, weeks or months is the RRULE's INTERVAL, and a
/// number of times its COUNT, so a calendar program shows the same dates Modbot does.
/// </summary>
public class CalendarFeedRepeatTests
{
    private static readonly DateTimeOffset Thursday = new(2026, 10, 1, 19, 0, 0, TimeSpan.Zero);

    private static CalendarEvent Event(string repeat, int every = 1, int? times = null, DateOnly? until = null, params string[] days)
    {
        var e = CalendarRepeatTests.Event(Thursday, TimeSpan.FromHours(2), repeat, until: until, days: days);
        e.RepeatEvery = every;
        e.RepeatTimes = times;
        return e;
    }

    private static string? Rule(CalendarEvent e) => CalendarFeedWriter.Rule(e, CalendarRepeat.ZoneOf(e));

    [Fact]
    public void EveryOtherWeekIsAnIntervalWithWeeksFromMonday()
    {
        Assert.Equal("FREQ=WEEKLY;BYDAY=TH;INTERVAL=2;WKST=MO", Rule(Event(CalendarRepeats.Weekly, every: 2, days: ["TH"])));
    }

    [Theory]
    [InlineData(CalendarRepeats.Daily, "FREQ=DAILY;INTERVAL=3")]
    [InlineData(CalendarRepeats.Monthly, "FREQ=MONTHLY;INTERVAL=3")]
    public void EveryFewDaysOrMonthsIsAnInterval(string repeat, string rule)
    {
        Assert.Equal(rule, Rule(Event(repeat, every: 3)));
    }

    [Fact]
    public void ANumberOfTimesIsACount()
    {
        Assert.Equal("FREQ=WEEKLY;BYDAY=TH;COUNT=4", Rule(Event(CalendarRepeats.Weekly, times: 4, days: ["TH"])));
        Assert.Equal("FREQ=WEEKLY;BYDAY=TH;INTERVAL=2;WKST=MO;COUNT=6", Rule(Event(CalendarRepeats.Weekly, every: 2, times: 6, days: ["TH"])));
    }

    [Fact]
    public void EveryWeekWritesTheRuleAsBefore()
    {
        // Nothing new in the rule of an event that uses neither, so calendar programs see no change.
        Assert.Equal("FREQ=WEEKLY;BYDAY=TH", Rule(Event(CalendarRepeats.Weekly, days: ["TH"])));
    }

    [Fact]
    public void ACountedRepeatPastItsLastDateLeavesTheFeedOnlyAfterTheDaysItIsKept()
    {
        // Four Thursdays, the last on 22 October. It finished then, and is still in the feed a week
        // later; it is gone well after the time the feed keeps ended events.
        var e = Event(CalendarRepeats.Weekly, times: 4, days: ["TH"]);
        e.State = CalendarEventStates.Finished;

        var lastEnd = Thursday.AddDays(21).AddHours(2);

        Assert.True(CalendarFeedWriter.Belongs(e, lastEnd.AddDays(7)));
        Assert.False(CalendarFeedWriter.Belongs(e, lastEnd + CalendarFeedWriter.KeepEndedFor + TimeSpan.FromDays(1)));
    }
}
