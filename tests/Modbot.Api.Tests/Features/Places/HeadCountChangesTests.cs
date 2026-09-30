using Modbot.Api.Features.Places;

namespace Modbot.Api.Tests.Features.Places;

public class HeadCountChangesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 20, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int seconds) => T0.AddSeconds(seconds);

    private static HeadCountPoint Reading(int seconds, int people, bool unsure = false)
        => new(At(seconds), people, unsure ? null : people, null, "page", unsure ? people : null, unsure);

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

    /// <summary>
    /// Readings are stored only on change, so in a quiet instance hours can pass between two. The drop
    /// still happened within one poll of the reading that showed it, and a kick from earlier in the
    /// evening is not its cause.
    /// </summary>
    [Fact]
    public void InAQuietInstance_AnOldKick_DoesNotColourALaterDrop()
    {
        var threeHours = 3 * 3600;

        var old = HeadCountChanges.Classify([Reading(0, 5), Reading(threeHours, 4)], [At(10)]);
        var recent = HeadCountChanges.Classify([Reading(0, 5), Reading(threeHours, 4)], [At(threeHours - 45)]);
        var tooOld = HeadCountChanges.Classify([Reading(0, 5), Reading(threeHours, 4)], [At(threeHours - 61)]);

        Assert.Equal(HeadCountChange.Left, old[1].Change);
        Assert.Equal(HeadCountChange.Kick, recent[1].Change);
        Assert.Equal(HeadCountChange.Left, tooOld[1].Change);
    }

    /// <summary>
    /// "80?" is n_users standing in for a missing userCount, and it runs high. Going from it to a
    /// real count, or back, is the source changing, not people moving.
    /// </summary>
    [Fact]
    public void AChangeBetweenUnsureAndSure_IsNoChange()
    {
        var toSure = HeadCountChanges.Classify([Reading(0, 80, unsure: true), Reading(30, 51)], [At(10)]);
        var toUnsure = HeadCountChanges.Classify([Reading(0, 51), Reading(30, 80, unsure: true)], []);
        var bothUnsure = HeadCountChanges.Classify([Reading(0, 80, unsure: true), Reading(30, 70, unsure: true)], [At(10)]);

        Assert.Null(toSure[1].Change);
        Assert.Null(toUnsure[1].Change);
        Assert.Equal(HeadCountChange.Kick, bothUnsure[1].Change);
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
