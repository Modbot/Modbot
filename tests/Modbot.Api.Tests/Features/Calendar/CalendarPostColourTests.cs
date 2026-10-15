using Modbot.Api.Features.Calendar;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// The colour of an event's channel post while it is scheduled: kept as sent, refused outside
/// 0 to 0xFFFFFF, and told apart in the change record.
/// </summary>
public class CalendarPostColourTests
{
    private static CalendarEventRequest Request(int? postColour) => new(
        "Game night", "Bring a controller", "2026-10-09T20:00", "2026-10-09T22:00", "UTC", "none", [], null,
        "wrld_calendar", "members", "us", null, null, "gaming", [], [], [], "group", false,
        PublishToVRChat: false, PublishToDiscord: false, PostToChannel: false, ChannelId: null,
        AutoOpen: false, OpenMinutesBefore: 10, Draft: false, PostColour: postColour);

    [Fact]
    public void ThePickedColourIsKept()
    {
        var e = new CalendarEvent();

        Assert.Null(CalendarEndpoints.Apply(Request(0x3498DB), e, preview: true));
        Assert.Equal(0x3498DB, e.PostColour);
    }

    [Fact]
    public void NoColourSentTakesThePickedOneOff()
    {
        var e = new CalendarEvent { PostColour = 0x3498DB };

        Assert.Null(CalendarEndpoints.Apply(Request(null), e, preview: true));
        Assert.Null(e.PostColour);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(CalendarEvent.MaxPostColour)]
    public void TheEndsOfTheRangeAreColours(int colour)
    {
        var e = new CalendarEvent();

        Assert.Null(CalendarEndpoints.Apply(Request(colour), e, preview: true));
        Assert.Equal(colour, e.PostColour);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(CalendarEvent.MaxPostColour + 1)]
    public void AColourOutsideTheRangeIsRefused(int colour)
    {
        var e = new CalendarEvent();

        var problem = CalendarEndpoints.Apply(Request(colour), e, preview: true);

        Assert.NotNull(problem);
        Assert.Contains("colour", problem, StringComparison.OrdinalIgnoreCase);
        Assert.Null(e.PostColour);
    }

    [Fact]
    public void AChangeOfOnlyTheColourShowsInTheChangeRecord()
    {
        var before = CalendarEventFields.Of(new CalendarEvent { PostColour = null });
        var after = CalendarEventFields.Of(new CalendarEvent { PostColour = 0x3498DB });

        Assert.NotEqual(before["postColour"]?.ToJsonString(), after["postColour"]?.ToJsonString());
    }
}
