using Modbot.Api.Features.Analytics;
using Modbot.Api.Features.Analytics.Team;

namespace Modbot.Api.Tests.Features.Analytics;

/// <summary>
/// The middle value the Moderation tab gives as "typical", and how a queue's waits become one. A
/// middle and never an average: one request left over a weekend must not outweigh the rest.
/// </summary>
public class TeamMiddlesTests
{
    [Fact]
    public void Middle_OfNothing_IsNull() => Assert.Null(Middles.Of([]));

    [Fact]
    public void Middle_OfAnOddCount_IsTheMiddleValue_WhateverTheOrder()
        => Assert.Equal(2m, Middles.Of([3m, 1m, 2m]));

    [Fact]
    public void Middle_OfAnEvenCount_IsHalfwayBetweenTheTwoMiddleValues()
        => Assert.Equal(2.5m, Middles.Of([10m, 1m, 3m, 2m]));

    [Fact]
    public void Middle_IsNotPulledByOneLongWait()
        => Assert.Equal(5m, Middles.Of([4m, 5m, 6m, 2880m, 3m]));

    [Fact]
    public void Wait_GivesTheMiddleOverall_AndTheMiddleOfEachDaysDecisions()
    {
        var day1 = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddDays(1);

        var wait = TeamQueues.Wait(Queues.JoinRequests,
        [
            (day1, day1.AddMinutes(10)),
            (day1, day1.AddMinutes(30)),
            (day2, day2.AddMinutes(60)),
        ]);

        Assert.Equal(Queues.JoinRequests, wait.Queue);
        Assert.Equal(3, wait.Decided);
        Assert.Equal(30m, wait.MiddleMinutes);
        Assert.Equal(
            [new DayValue(new DateOnly(2026, 9, 1), 20m), new DayValue(new DateOnly(2026, 9, 2), 60m)],
            wait.MiddleMinutesPerDay);
    }

    [Fact]
    public void Wait_WithNothingDecided_HasNoMiddle()
    {
        var wait = TeamQueues.Wait(Queues.Flags, []);

        Assert.Equal(0, wait.Decided);
        Assert.Null(wait.MiddleMinutes);
        Assert.Empty(wait.MiddleMinutesPerDay);
    }
}
