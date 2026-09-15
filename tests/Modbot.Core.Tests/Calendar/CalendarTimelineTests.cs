using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Calendar;

/// <summary>Calendar design §2.1: scheduled → open → scheduled for each occurrence, finished after the last.</summary>
public class CalendarTimelineTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnOccurrenceOpensWhenItsInstanceIsDueToOpen()
    {
        var e = CalendarRepeatTests.Event(Start, TimeSpan.FromHours(2), CalendarRepeats.Weekly, days: ["SU"]);
        e.AutoOpen = true;
        e.OpenMinutesBefore = 10;

        Assert.Equal(CalendarStep.NextOccurrence, CalendarTimeline.Advance(e, Start.AddMinutes(-11)));
        Assert.Equal(CalendarEventStates.Scheduled, e.State);

        Assert.Equal(CalendarStep.Opened, CalendarTimeline.Advance(e, Start.AddMinutes(-10)));
        Assert.Equal(CalendarEventStates.Open, e.State);
        Assert.Equal(Start, e.OccurrenceStartsAt);

        // Staying open is not opening again.
        Assert.Equal(CalendarStep.None, CalendarTimeline.Advance(e, Start.AddMinutes(30)));
    }

    [Fact]
    public void ARepeatingEventGoesBackToScheduledForItsNextOccurrence()
    {
        var e = CalendarRepeatTests.Event(Start, TimeSpan.FromHours(2), CalendarRepeats.Weekly, days: ["SU"]);
        CalendarTimeline.Advance(e, Start.AddMinutes(1));

        var step = CalendarTimeline.Advance(e, Start.AddHours(2));

        Assert.Equal(CalendarStep.NextOccurrence, step);
        Assert.Equal(CalendarEventStates.Scheduled, e.State);
        Assert.Equal(Start.AddDays(7), e.OccurrenceStartsAt);
    }

    [Fact]
    public void AnEventFinishesAfterItsLastOccurrence()
    {
        var e = CalendarRepeatTests.Event(Start, TimeSpan.FromHours(2));
        CalendarTimeline.Advance(e, Start.AddMinutes(1));

        Assert.Equal(CalendarStep.Finished, CalendarTimeline.Advance(e, Start.AddHours(2)));
        Assert.Equal(CalendarEventStates.Finished, e.State);
    }

    [Theory]
    [InlineData(CalendarEventStates.Draft)]
    [InlineData(CalendarEventStates.Cancelled)]
    [InlineData(CalendarEventStates.Finished)]
    public void OnlyLiveEventsMove(string state)
    {
        var e = CalendarRepeatTests.Event(Start, TimeSpan.FromHours(2), CalendarRepeats.Daily);
        e.State = state;

        Assert.Equal(CalendarStep.None, CalendarTimeline.Advance(e, Start.AddMinutes(1)));
        Assert.Equal(state, e.State);
    }
}
