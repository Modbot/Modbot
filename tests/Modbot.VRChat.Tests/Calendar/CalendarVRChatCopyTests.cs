using Modbot.Core.Data.Entities;
using Modbot.VRChat.Calendar;
using Modbot.VRChat.Tests.Fakes;
using VRChat.API.Model;
using CalendarEvent = Modbot.Core.Data.Entities.CalendarEvent;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>Calendar design §12: what VRChat says about an event, copied onto a Modbot event.</summary>
public class CalendarVRChatCopyTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 26, 1, 0, 0, TimeSpan.Zero);

    private static global::VRChat.API.Model.CalendarEvent Series(CalendarEventRecurrence rule) =>
        FakeCalendar.Made("cal_series", "Hangout", Start, TimeSpan.FromHours(4), Start, CalendarEventOccurrenceKind.Series, recurrence: rule);

    [Fact]
    public void AWeeklySeriesKeepsItsZoneAndStartDay()
    {
        var e = new CalendarEvent();
        CalendarVRChatCopy.Onto(e, Series(new CalendarEventRecurrence(frequency: CalendarEventFrequency.Weekly, interval: 1, timezone: "America/New_York")));

        Assert.Equal(CalendarRepeats.Weekly, e.Repeat);
        Assert.Equal("America/New_York", e.TimeZone);

        // 01:00 UTC on a Sunday is Saturday evening in New York.
        Assert.Equal(new[] { "SA" }, e.RepeatDays);
        Assert.Equal(TimeSpan.FromHours(4), e.EndsAt - e.StartsAt);
    }

    [Fact]
    public void AnEndAfterSoManyTimesIsKeptAsThatManyTimes()
    {
        // Until 2026-10-02 it was counted out to a last date, as Modbot had no number of times.
        var e = new CalendarEvent { RepeatUntil = new DateOnly(2027, 1, 1) };
        var rule = new CalendarEventRecurrence(
            frequency: CalendarEventFrequency.Weekly,
            interval: 1,
            timezone: "America/New_York",
            end: new CalendarEventRecurrenceEnd(type: CalendarEventRecurrenceEndType.AfterOccurrences, count: 3));

        CalendarVRChatCopy.Onto(e, Series(rule));

        Assert.Equal(3, e.RepeatTimes);
        Assert.Null(e.RepeatUntil);
    }

    [Fact]
    public void EveryOtherWeekIsKept()
    {
        var e = new CalendarEvent();
        var source = Series(new CalendarEventRecurrence(frequency: CalendarEventFrequency.Weekly, interval: 2, timezone: "UTC"));

        Assert.Null(CalendarVRChatCopy.CannotKeep(source));

        CalendarVRChatCopy.Onto(e, source);

        Assert.Equal(CalendarRepeats.Weekly, e.Repeat);
        Assert.Equal(2, e.RepeatEvery);
        Assert.Null(e.RepeatTimes);
    }

    [Fact]
    public void FeaturedIsCopiedAsVRChatHasIt()
    {
        var e = new CalendarEvent();
        var source = FakeCalendar.Made("cal_one", "Movie", Start, TimeSpan.FromHours(2), Start, featured: true);

        CalendarVRChatCopy.Onto(e, source);

        Assert.True(e.Featured);
    }

    [Fact]
    public void AnEndOnADateIsThatDate()
    {
        var e = new CalendarEvent();
        var rule = new CalendarEventRecurrence(
            frequency: CalendarEventFrequency.Weekly,
            interval: 1,
            timezone: "America/New_York",
            end: new CalendarEventRecurrenceEnd(type: CalendarEventRecurrenceEndType.AfterDate, date: "2026-12-31T23:59:59"));

        CalendarVRChatCopy.Onto(e, Series(rule));

        Assert.Equal(new DateOnly(2026, 12, 31), e.RepeatUntil);
    }

    [Theory]
    [InlineData(CalendarEventFrequency.Weekly, CalendarRepeats.MaxEvery + 1)]
    [InlineData(CalendarEventFrequency.Yearly, 1)]
    public void ARuleModbotCannotHoldIsSaidSo(CalendarEventFrequency frequency, int interval)
    {
        var source = Series(new CalendarEventRecurrence(frequency: frequency, interval: interval, timezone: "UTC"));
        Assert.NotNull(CalendarVRChatCopy.CannotKeep(source));
    }

    [Fact]
    public void ModbotsOwnFieldsAreLeftAlone()
    {
        var e = new CalendarEvent
        {
            WorldId = "wrld_kept",
            AccessType = "plus",
            Region = "eu",
            PublishToDiscord = true,
            AutoOpen = true,
            ImageUrl = "https://example.com/moderators-choice.png",
            TimeZone = "Europe/London",
        };

        CalendarVRChatCopy.Onto(e, FakeCalendar.Made("cal_one", "Movie", Start, TimeSpan.FromHours(2), Start));

        Assert.Equal("wrld_kept", e.WorldId);
        Assert.Equal("plus", e.AccessType);
        Assert.Equal("eu", e.Region);
        Assert.True(e.PublishToDiscord);
        Assert.True(e.AutoOpen);

        // The picture is the moderator's on an event made in Modbot, and a one-off keeps its zone.
        Assert.Equal("https://example.com/moderators-choice.png", e.ImageUrl);
        Assert.Equal("Europe/London", e.TimeZone);
        Assert.Equal("file_picture", e.VRChatImageId);
    }
}
