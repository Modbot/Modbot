using Modbot.Analytics.Activity;

namespace Modbot.Analytics.Tests.Activity;

/// <summary>
/// When a peak is thin — the whole point of carrying coverage beside the number.
/// </summary>
/// <remarks>
/// The failure this guards against is the quiet one: a figure drawn from two hours of counting in a
/// week looks exactly like a figure drawn from the whole week. Nothing in "48" says which, so the
/// flag has to, and the rule behind the flag has to be the one the page's wording claims.
/// </remarks>
public class CoverageTests
{
    /// <summary>A week in which instances were open for twenty hours in all.</summary>
    private static InstanceCoverage Week(decimal minutesCounted) =>
        new(WindowDays: 7, DaysCounted: 3, InstancesOpen: 8, InstancesCounted: 4,
            MinutesInstancesWereOpen: 1200m, MinutesCounted: minutesCounted);

    [Fact]
    public void CountingMostOfTheOpenTime_IsNotThin()
    {
        Assert.False(Week(1200m).Thin);
        Assert.False(Week(900m).Thin);
    }

    /// <summary>The brief's own case: two hours of counting in a week is not the week's peak.</summary>
    [Fact]
    public void TwoHoursOfCountingInAWeek_IsThin()
    {
        Assert.True(Week(120m).Thin);
    }

    [Fact]
    public void ExactlyHalf_IsNotThin()
    {
        Assert.False(Week(600m).Thin);
        Assert.True(Week(599m).Thin);
    }

    /// <summary>
    /// The denominator is open time, never calendar time. A quiet group that ran one evening in
    /// ninety days and was counted throughout it has nothing missing, and calling its figures thin
    /// would mark every small community unreliable.
    /// </summary>
    [Fact]
    public void AQuietGroupCountedThroughout_IsNotThin()
    {
        var coverage = new InstanceCoverage(
            WindowDays: 90, DaysCounted: 1, InstancesOpen: 1, InstancesCounted: 1,
            MinutesInstancesWereOpen: 180m, MinutesCounted: 180m);

        Assert.False(coverage.Thin);
    }

    /// <summary>Nothing was open, so there is nothing to have missed.</summary>
    [Fact]
    public void NothingOpen_IsNotThin()
    {
        Assert.False(InstanceCoverage.Nothing.Thin);
        Assert.False((InstanceCoverage.Nothing with { WindowDays = 30 }).Thin);
    }

    [Fact]
    public void MemberCountReadings_AreThinWhenFewerThanHalfTheDaysCarryOne()
    {
        Assert.False(new MemberCountCoverage(WindowDays: 30, DaysWithReadings: 30, Readings: 8640).Thin);
        Assert.False(new MemberCountCoverage(WindowDays: 30, DaysWithReadings: 15, Readings: 4320).Thin);
        Assert.True(new MemberCountCoverage(WindowDays: 30, DaysWithReadings: 2, Readings: 576).Thin);
    }

    [Fact]
    public void AnEmptyWindow_IsNotThin()
    {
        Assert.False(MemberCountCoverage.Nothing.Thin);
    }
}
