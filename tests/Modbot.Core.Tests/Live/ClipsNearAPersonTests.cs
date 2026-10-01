using Modbot.Core.Data.Entities;
using Modbot.Core.Live;

namespace Modbot.Core.Tests.Live;

/// <summary>
/// Which saved clips a case file offers: the ones saved in an instance while the person on it was
/// there, by the companions' reports.
/// </summary>
public class ClipsNearAPersonTests
{
    private static readonly DateTimeOffset Nine = new(2026, 10, 1, 21, 0, 0, TimeSpan.Zero);

    private static ClipMark Clip(long id, DateTimeOffset at, string instance = "98874", string? world = "wrld_cat")
        => new(id, world, instance, at);

    private static PersonMark Mark(string type, DateTimeOffset at, string instance = "98874", string? world = "wrld_cat")
        => new(type, world, instance, at);

    [Fact]
    public void SomebodyWhoArrivedEarlierAndNeverLeftIsInTheClip()
    {
        // Walked in at eight, clipped at nine: no fact at nine at all, and that is the ordinary case.
        var picked = ClipsNearAPerson.Pick(
            [Clip(1, Nine)],
            [Mark(FactType.InstanceJoined, Nine.AddHours(-1))]);

        Assert.Equal([1L], picked);
    }

    [Fact]
    public void SomebodyWhoLeftBeforeTheClipsMinutesIsNotInIt()
    {
        var picked = ClipsNearAPerson.Pick(
            [Clip(1, Nine)],
            [
                Mark(FactType.InstanceJoined, Nine.AddHours(-1)),
                Mark(FactType.InstanceLeft, Nine.AddMinutes(-20)),
            ]);

        Assert.Empty(picked);
    }

    [Fact]
    public void SomebodyWhoLeftDuringTheClipsMinutesIsInIt()
    {
        var picked = ClipsNearAPerson.Pick(
            [Clip(1, Nine)],
            [
                Mark(FactType.InstanceJoined, Nine.AddHours(-1)),
                Mark(FactType.InstanceLeft, Nine.AddMinutes(-2)),
            ]);

        Assert.Equal([1L], picked);
    }

    [Fact]
    public void AnotherInstanceOrAnotherWorldIsNotTheSamePlace()
    {
        var picked = ClipsNearAPerson.Pick(
            [Clip(1, Nine), Clip(2, Nine, world: "wrld_dog")],
            [Mark(FactType.InstancePresenceObserved, Nine.AddMinutes(-30), instance: "11111")]);

        Assert.Empty(picked);

        // Same number, other world: instance numbers are only unique within a world.
        Assert.Equal(
            [1L],
            ClipsNearAPerson.Pick(
                [Clip(1, Nine), Clip(2, Nine, world: "wrld_dog")],
                [Mark(FactType.InstancePresenceObserved, Nine.AddMinutes(-30))]));
    }

    [Fact]
    public void ArrivingAfterTheClipWasSavedIsNotBeingInIt()
    {
        var picked = ClipsNearAPerson.Pick(
            [Clip(1, Nine)],
            [Mark(FactType.InstanceJoined, Nine.AddMinutes(1))]);

        Assert.Empty(picked);
    }

    [Fact]
    public void NothingOlderThanTheLongestStayIsBelieved()
    {
        var picked = ClipsNearAPerson.Pick(
            [Clip(1, Nine)],
            [Mark(FactType.InstanceJoined, Nine - TimeInInstance.LongestStay - TimeSpan.FromMinutes(1))]);

        Assert.Empty(picked);
    }

    [Fact]
    public void FactsThatAreNotPresenceSayNothingAboutWhereSomebodyWas()
    {
        var picked = ClipsNearAPerson.Pick(
            [Clip(1, Nine)],
            [Mark(FactType.GroupInstanceKick, Nine.AddMinutes(-1))]);

        Assert.Empty(picked);
    }

    [Fact]
    public void ClipsComeOldestFirst()
    {
        var picked = ClipsNearAPerson.Pick(
            [Clip(2, Nine.AddMinutes(10)), Clip(1, Nine)],
            [Mark(FactType.InstanceJoined, Nine.AddHours(-1))]);

        Assert.Equal([1L, 2L], picked);
    }
}
