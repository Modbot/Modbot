using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Calendar;

/// <summary>
/// Calendar design §2 (2026-10-02): a repeat every N days, weeks or months, and one that stops after
/// a number of times. Asked for by a tester: "every two weeks or every three weeks", and "repeats
/// weekly for four weeks, then it stops".
/// </summary>
public class CalendarRepeatEveryTests
{
    // Thursday 1 October 2026, 19:00 UTC.
    private static readonly DateTimeOffset Thursday = new(2026, 10, 1, 19, 0, 0, TimeSpan.Zero);

    private static CalendarEvent Event(string repeat, int every = 1, int? times = null, DateOnly? until = null, params string[] days)
    {
        var e = CalendarRepeatTests.Event(Thursday, TimeSpan.FromHours(2), repeat, until: until, days: days);
        e.RepeatEvery = every;
        e.RepeatTimes = times;
        return e;
    }

    private static List<DateTimeOffset> Starts(CalendarEvent e, int take) =>
        [.. CalendarRepeat.Between(e, e.StartsAt.AddMinutes(-1)).Take(take).Select(o => o.StartsAt)];

    [Fact]
    public void EveryOtherWeekSkipsAWeek()
    {
        var e = Event(CalendarRepeats.Weekly, every: 2, days: ["TH"]);

        Assert.Equal(
            new[] { Thursday, Thursday.AddDays(14), Thursday.AddDays(28), Thursday.AddDays(42) },
            Starts(e, 4));
    }

    [Fact]
    public void EveryOtherWeekCountsWeeksFromMondayToSunday()
    {
        // Starting on a Thursday with Monday and Thursday ticked: the Monday of the first week is
        // before the start, so the first week has only the Thursday; the next week is skipped; the
        // week after has both. iCalendar's WKST=MO counts the same weeks.
        var e = Event(CalendarRepeats.Weekly, every: 2, days: ["MO", "TH"]);

        Assert.Equal(
            new[] { Thursday, Thursday.AddDays(11), Thursday.AddDays(14), Thursday.AddDays(25), Thursday.AddDays(28) },
            Starts(e, 5));
    }

    [Fact]
    public void EveryThreeDays()
    {
        var e = Event(CalendarRepeats.Daily, every: 3);

        Assert.Equal(new[] { Thursday, Thursday.AddDays(3), Thursday.AddDays(6) }, Starts(e, 3));
    }

    [Fact]
    public void EveryOtherMonthStillSkipsAMonthWithoutTheDay()
    {
        // 31 January, every 2 months: March has a 31st, May does, July does; a month without one
        // would be skipped and still counted.
        var e = CalendarRepeatTests.Event(new DateTimeOffset(2027, 1, 31, 18, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(1), CalendarRepeats.Monthly);
        e.RepeatEvery = 2;

        var months = CalendarRepeat.Between(e, e.StartsAt.AddMinutes(-1)).Take(4).Select(o => o.StartsAt.Month).ToList();

        Assert.Equal(new[] { 1, 3, 5, 7 }, months);

        // From 30 November, every 3 months: February has no 30th, so the next is May.
        var november = CalendarRepeatTests.Event(new DateTimeOffset(2026, 11, 30, 18, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(1), CalendarRepeats.Monthly);
        november.RepeatEvery = 3;

        Assert.Equal(
            new[] { new DateTimeOffset(2026, 11, 30, 18, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 5, 30, 18, 0, 0, TimeSpan.Zero) },
            CalendarRepeat.Between(november, november.StartsAt.AddMinutes(-1)).Take(2).Select(o => o.StartsAt).ToList());
    }

    [Fact]
    public void ANumberOfTimesStopsTheRepeat()
    {
        var e = Event(CalendarRepeats.Weekly, times: 4, days: ["TH"]);

        var all = CalendarRepeat.Between(e, e.StartsAt.AddYears(-1), e.StartsAt.AddYears(1)).ToList();

        Assert.Equal(4, all.Count);
        Assert.Equal(Thursday.AddDays(21), all[^1].StartsAt);
        Assert.Equal(Thursday.AddDays(21), CalendarRepeat.LastPlanned(e)?.StartsAt);
    }

    [Fact]
    public void ANumberOfTimesCountsEveryDayOfAWeek()
    {
        var e = Event(CalendarRepeats.Weekly, every: 2, times: 3, days: ["MO", "TH"]);

        Assert.Equal(new[] { Thursday, Thursday.AddDays(11), Thursday.AddDays(14) }, Starts(e, 10));
    }

    [Fact]
    public void ADateCancelledOnItsOwnStillCounts()
    {
        // As an EXDATE does under iCalendar's COUNT: cancelling the second of four leaves three.
        var e = Event(CalendarRepeats.Weekly, times: 4, days: ["TH"]);
        e.DateChanges.Add(new CalendarDateChange { PlannedStartsAt = Thursday.AddDays(7), Cancelled = true });

        Assert.Equal(new[] { Thursday, Thursday.AddDays(14), Thursday.AddDays(21) }, Starts(e, 10));
    }

    [Fact]
    public void AnEventWithNoEndHasNoLastDate()
    {
        Assert.Null(CalendarRepeat.LastPlanned(Event(CalendarRepeats.Weekly, every: 2, days: ["TH"])));
    }

    [Fact]
    public void TheDatesBeforeAStartAreCounted()
    {
        var e = Event(CalendarRepeats.Weekly, every: 2, times: 6, days: ["TH"]);

        Assert.Equal(0, CalendarRepeat.PlannedBefore(e, Thursday));
        Assert.Equal(2, CalendarRepeat.PlannedBefore(e, Thursday.AddDays(28)));
    }

    [Fact]
    public void ATimeTheRepeatSkipsIsNotOneOfItsDates()
    {
        var e = Event(CalendarRepeats.Weekly, every: 2, days: ["TH"]);

        Assert.True(CalendarRepeat.IsPlannedDate(e, Thursday.AddDays(14)));
        Assert.False(CalendarRepeat.IsPlannedDate(e, Thursday.AddDays(7)));
    }

    [Fact]
    public void ADateChangedOnItsOwnFollowsToTheSameDayOnlyWhenTheRepeatStillHasIt()
    {
        // Every week, with the date two weeks on given its own title. Made every other week, that
        // date is still one of them; the one a week on is not, and its change goes.
        var e = Event(CalendarRepeats.Weekly, days: ["TH"]);
        e.DateChanges.Add(new CalendarDateChange { PlannedStartsAt = Thursday.AddDays(7), Title = "Week two" });
        e.DateChanges.Add(new CalendarDateChange { PlannedStartsAt = Thursday.AddDays(14), Title = "Week three" });

        var zone = CalendarRepeat.ZoneOf(e);
        e.RepeatEvery = 2;
        CalendarDates.Rematch(e, zone, Thursday.AddDays(-1));

        var change = Assert.Single(e.DateChanges);
        Assert.Equal(Thursday.AddDays(14), change.PlannedStartsAt);
    }
}
