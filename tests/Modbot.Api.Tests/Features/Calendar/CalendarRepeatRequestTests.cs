using Modbot.Api.Features.Calendar;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// Calendar design §2 (2026-10-02): what the form may ask for -- every 1 to 52 days, weeks or
/// months, 1 to 500 times, one end or the other -- and Featured, which an older client that does
/// not send it leaves as it was.
/// </summary>
public class CalendarRepeatRequestTests
{
    private static CalendarEventRequest Request(
        string repeat = "weekly",
        string? until = null,
        int? every = null,
        int? times = null,
        bool? featured = null) =>
        new(
            "Movie night", "Bring snacks", "2026-10-01T20:00", "2026-10-01T22:00", "Europe/London",
            repeat, ["TH"], until, null, "members", "us", null, null, "film_media",
            [], [], [], "group", false, true, false, false, null, false, 10, false,
            RepeatEvery: every, RepeatTimes: times, Featured: featured);

    [Fact]
    public void EveryAndTimesAreKept()
    {
        var e = new CalendarEvent();

        Assert.Null(CalendarEndpoints.Apply(Request(every: 2, times: 6), e));

        Assert.Equal(2, e.RepeatEvery);
        Assert.Equal(6, e.RepeatTimes);
        Assert.Null(e.RepeatUntil);
    }

    [Fact]
    public void NoEveryIsEveryOne()
    {
        var e = new CalendarEvent { RepeatEvery = 3 };

        Assert.Null(CalendarEndpoints.Apply(Request(), e));

        Assert.Equal(1, e.RepeatEvery);
        Assert.Null(e.RepeatTimes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(CalendarRepeats.MaxEvery + 1)]
    public void EveryOutOfRangeIsRefused(int every)
    {
        Assert.NotNull(CalendarEndpoints.Apply(Request(every: every), new CalendarEvent()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(CalendarRepeats.MaxTimes + 1)]
    public void TimesOutOfRangeAreRefused(int times)
    {
        Assert.NotNull(CalendarEndpoints.Apply(Request(times: times), new CalendarEvent()));
    }

    [Fact]
    public void ALastDateAndANumberOfTimesTogetherAreRefused()
    {
        Assert.Equal(
            "Pick a last date or a number of times, not both.",
            CalendarEndpoints.Apply(Request(until: "2026-12-31", times: 4), new CalendarEvent()));
    }

    [Fact]
    public void AnEventThatDoesNotRepeatDropsThem()
    {
        var e = new CalendarEvent { RepeatEvery = 2, RepeatTimes = 4 };

        Assert.Null(CalendarEndpoints.Apply(Request(repeat: "none", every: 2, times: 4), e));

        Assert.Equal(1, e.RepeatEvery);
        Assert.Null(e.RepeatTimes);
    }

    [Fact]
    public void FeaturedIsSetAndLeftOutKeepsIt()
    {
        var e = new CalendarEvent();

        Assert.Null(CalendarEndpoints.Apply(Request(featured: true), e));
        Assert.True(e.Featured);

        Assert.Null(CalendarEndpoints.Apply(Request(featured: null), e));
        Assert.True(e.Featured);

        Assert.Null(CalendarEndpoints.Apply(Request(featured: false), e));
        Assert.False(e.Featured);
    }
}
