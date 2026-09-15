using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Calendar;

/// <summary>
/// Calendar design §2: the rule is stored, not the occurrences, and repeats are counted in the
/// event's own time zone.
/// </summary>
public class CalendarRepeatTests
{
    internal static CalendarEvent Event(
        DateTimeOffset startsAt,
        TimeSpan length,
        string repeat = CalendarRepeats.None,
        string timeZone = "UTC",
        DateOnly? until = null,
        params string[] days) => new()
    {
        Id = Guid.Parse("01923456-7890-7abc-8def-0123456789ab"),
        Title = "Event",
        StartsAt = startsAt,
        EndsAt = startsAt + length,
        TimeZone = timeZone,
        Repeat = repeat,
        RepeatDays = [.. days],
        RepeatUntil = until,
        State = CalendarEventStates.Scheduled,
    };

    [Fact]
    public void AnEventThatDoesNotRepeatHappensOnce()
    {
        var e = Event(new DateTimeOffset(2026, 9, 20, 19, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(2));

        var all = CalendarRepeat.Between(e, e.StartsAt.AddYears(-1), e.StartsAt.AddYears(1)).ToList();

        Assert.Equal(new[] { new CalendarOccurrence(e.StartsAt, e.EndsAt) }, all);
    }

    [Fact]
    public void AWeeklyEventKeepsItsWallClockTimeAcrossTheClocksGoingBack()
    {
        // 20:00 in London on Sunday 18 October 2026 is 19:00 UTC (summer time). The clocks go back
        // on 25 October, so the next Sunday's 20:00 is 20:00 UTC.
        var e = Event(
            new DateTimeOffset(2026, 10, 18, 19, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(2),
            CalendarRepeats.Weekly,
            "Europe/London",
            days: ["SU"]);

        var two = CalendarRepeat.Between(e, e.StartsAt.AddMinutes(-1)).Take(2).ToList();

        Assert.Equal(new DateTimeOffset(2026, 10, 18, 19, 0, 0, TimeSpan.Zero), two[0].StartsAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 20, 0, 0, TimeSpan.Zero), two[1].StartsAt);
        Assert.Equal(TimeSpan.FromHours(2), two[1].EndsAt - two[1].StartsAt);
    }

    [Fact]
    public void AWeeklyEventOnSeveralDaysHappensOnEachOfThem()
    {
        // Monday 21 September 2026.
        var e = Event(
            new DateTimeOffset(2026, 9, 21, 18, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(1),
            CalendarRepeats.Weekly,
            days: ["MO", "WE", "FR"]);

        var starts = CalendarRepeat.Between(e, e.StartsAt.AddMinutes(-1)).Take(4).Select(o => o.StartsAt.Day).ToList();

        Assert.Equal(new[] { 21, 23, 25, 28 }, starts);
    }

    [Fact]
    public void AWeeklyEventAlwaysIncludesTheDayItStartsOn()
    {
        // Sunday 20 September, with only Wednesday ticked: the first start is still an occurrence.
        var e = Event(
            new DateTimeOffset(2026, 9, 20, 18, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(1),
            CalendarRepeats.Weekly,
            days: ["WE"]);

        var starts = CalendarRepeat.Between(e, e.StartsAt.AddMinutes(-1)).Take(3).Select(o => o.StartsAt.Day).ToList();

        Assert.Equal(new[] { 20, 23, 27 }, starts);
    }

    [Fact]
    public void AMonthlyEventOnThe31stSkipsMonthsWithoutOne()
    {
        var e = Event(new DateTimeOffset(2026, 1, 31, 18, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(1), CalendarRepeats.Monthly);

        var months = CalendarRepeat.Between(e, e.StartsAt.AddMinutes(-1)).Take(4).Select(o => o.StartsAt.Month).ToList();

        Assert.Equal(new[] { 1, 3, 5, 7 }, months);
    }

    [Fact]
    public void ARepeatStopsAfterItsLastDate()
    {
        var e = Event(
            new DateTimeOffset(2026, 9, 20, 18, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(1),
            CalendarRepeats.Daily,
            until: new DateOnly(2026, 9, 22));

        var all = CalendarRepeat.Between(e, e.StartsAt.AddYears(-1)).ToList();

        Assert.Equal(3, all.Count);
        Assert.Equal(22, all[^1].StartsAt.Day);
    }

    [Fact]
    public void NextIsTheOccurrenceStillRunning()
    {
        var e = Event(new DateTimeOffset(2026, 9, 20, 18, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(2), CalendarRepeats.Daily);

        var running = CalendarRepeat.Next(e, e.StartsAt.AddMinutes(90));

        Assert.Equal(e.StartsAt, running?.StartsAt);
    }

    [Fact]
    public void NextIsNullWhenNothingIsLeft()
    {
        var e = Event(new DateTimeOffset(2026, 9, 20, 18, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(2));

        Assert.Null(CalendarRepeat.Next(e, e.EndsAt));
    }

    [Fact]
    public void AnInstanceOpensTheChosenMinutesEarly()
    {
        var e = Event(new DateTimeOffset(2026, 9, 20, 18, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(2));
        e.AutoOpen = true;
        e.OpenMinutesBefore = 10;

        var opensAt = CalendarRepeat.OpensAt(e, new CalendarOccurrence(e.StartsAt, e.EndsAt));

        Assert.Equal(e.StartsAt.AddMinutes(-10), opensAt);
    }
}
