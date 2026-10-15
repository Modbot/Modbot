using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Calendar;
using Modbot.Discord.Cards;

namespace Modbot.Discord.Tests.Calendar;

/// <summary>
/// The colour of an event's channel post: the one picked for the event while it is scheduled, and
/// green, dark and red for open, finished and cancelled whatever was picked, so the state still
/// reads from the colour.
/// </summary>
public class CalendarPostColourTests
{
    private const int Picked = 0xE67E22;

    private static readonly DateTimeOffset Start = new(2026, 9, 20, 19, 0, 0, TimeSpan.Zero);

    private static CalendarEvent Event(int? postColour)
    {
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = "Friday night",
            Description = "Plain words",
            StartsAt = Start,
            EndsAt = Start.AddHours(2),
            TimeZone = "UTC",
            State = CalendarEventStates.Scheduled,
            PostColour = postColour,
        };

        CalendarTimeline.Advance(e, Start.AddDays(-1));
        return e;
    }

    private static uint ColourOf(CalendarEvent e, CalendarCardState state) =>
        CalendarCard.For(e, CalendarRepeat.Current(e), null, state, null).Color;

    [Fact]
    public void AScheduledPostIsThePickedColour()
    {
        Assert.Equal((uint)Picked, ColourOf(Event(Picked), CalendarCardState.Scheduled));
    }

    [Fact]
    public void WithNoColourPickedAScheduledPostIsViolet()
    {
        Assert.Equal(CardColour.Violet, ColourOf(Event(null), CalendarCardState.Scheduled));
    }

    [Theory]
    [InlineData(CalendarCardState.Open, CardColour.Green)]
    [InlineData(CalendarCardState.Finished, CardColour.Dark)]
    [InlineData(CalendarCardState.Cancelled, CardColour.Red)]
    public void OpenFinishedAndCancelledPostsKeepTheirOwnColours(CalendarCardState state, uint expected)
    {
        Assert.Equal(expected, ColourOf(Event(Picked), state));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0x1000000)]
    public void AColourOutsideTheRangeFallsBackToViolet(int stored)
    {
        Assert.Equal(CardColour.Violet, ColourOf(Event(stored), CalendarCardState.Scheduled));
    }

    [Fact]
    public void BlackIsAColourToo()
    {
        Assert.Equal(0u, ColourOf(Event(0), CalendarCardState.Scheduled));
    }
}
