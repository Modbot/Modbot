using Modbot.Api.Features.Places;

namespace Modbot.Api.Tests.Features.Places;

public class HeadCountChangesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 20, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int seconds) => T0.AddSeconds(seconds);

    private static HeadCountPoint Reading(int seconds, int people) => new(At(seconds), people, people, null, "page");

    [Fact]
    public void TheFirstReading_HasNoChange_AndARise_IsUp()
    {
        var points = HeadCountChanges.Classify([Reading(0, 4), Reading(30, 7)], []);

        Assert.Equal([null, HeadCountChange.Up], points.Select(p => p.Change));
    }

    [Fact]
    public void ADrop_WithNoKick_IsLeft()
    {
        var points = HeadCountChanges.Classify([Reading(0, 4), Reading(30, 3)], []);

        Assert.Equal(HeadCountChange.Left, points[1].Change);
    }

    [Fact]
    public void ADrop_WithAKickBetweenTheReadings_IsKick()
    {
        var points = HeadCountChanges.Classify([Reading(0, 4), Reading(30, 3)], [At(12)]);

        Assert.Equal(HeadCountChange.Kick, points[1].Change);
    }

    /// <summary>
    /// The reading is stamped when Modbot read the page and the kick when VRChat wrote its log, so a
    /// kick just outside the two readings, by no more than one poll, is still the drop's.
    /// </summary>
    [Fact]
    public void AKickWithinOnePollOfEitherReading_StillCounts()
    {
        var before = HeadCountChanges.Classify([Reading(60, 4), Reading(90, 3)], [At(60 - 30)]);
        var after = HeadCountChanges.Classify([Reading(60, 4), Reading(90, 3)], [At(90 + 30)]);
        var tooEarly = HeadCountChanges.Classify([Reading(60, 4), Reading(90, 3)], [At(60 - 31)]);
        var tooLate = HeadCountChanges.Classify([Reading(60, 4), Reading(90, 3)], [At(90 + 31)]);

        Assert.Equal(HeadCountChange.Kick, before[1].Change);
        Assert.Equal(HeadCountChange.Kick, after[1].Change);
        Assert.Equal(HeadCountChange.Left, tooEarly[1].Change);
        Assert.Equal(HeadCountChange.Left, tooLate[1].Change);
    }

    /// <summary>Two drops close together share a stretch of slack; one kick in it paints one of them.</summary>
    [Fact]
    public void OneKick_ColoursOneDrop()
    {
        var points = HeadCountChanges.Classify(
            [Reading(0, 5), Reading(30, 4), Reading(40, 3)],
            [At(35)]);

        Assert.Equal([null, HeadCountChange.Kick, HeadCountChange.Left], points.Select(p => p.Change));
    }

    [Fact]
    public void ARise_NeverTakesAKick()
    {
        var points = HeadCountChanges.Classify(
            [Reading(0, 4), Reading(30, 5), Reading(60, 4)],
            [At(20)]);

        Assert.Equal([null, HeadCountChange.Up, HeadCountChange.Kick], points.Select(p => p.Change));
    }

    [Fact]
    public void ACountThatDidNotChange_HasNoChange()
    {
        var points = HeadCountChanges.Classify([Reading(0, 4), Reading(30, 4)], [At(10)]);

        Assert.Null(points[1].Change);
    }

    [Fact]
    public void KicksInAnyOrder_AreMatchedOldestFirst()
    {
        var points = HeadCountChanges.Classify(
            [Reading(0, 6), Reading(30, 5), Reading(60, 4)],
            [At(50), At(10)]);

        Assert.Equal([null, HeadCountChange.Kick, HeadCountChange.Kick], points.Select(p => p.Change));
    }
}
