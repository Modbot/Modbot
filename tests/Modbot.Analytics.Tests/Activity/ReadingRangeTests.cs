using Modbot.Analytics.Activity;

namespace Modbot.Analytics.Tests.Activity;

/// <summary>
/// The four ranges a chart of readings offers, and the step that keeps a long one drawable.
/// </summary>
public class ReadingRangeTests
{
    [Theory]
    [InlineData("day")]
    [InlineData("week")]
    [InlineData("month")]
    [InlineData("all")]
    public void TheFourWords_AreRanges(string range) => Assert.True(ReadingRange.IsRange(range));

    [Theory]
    [InlineData("year")]
    [InlineData("Week")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElse_IsNot(string? range) => Assert.False(ReadingRange.IsRange(range));

    [Fact]
    public void AllTime_HasNoSpan_BecauseItReachesBackToTheFirstReading()
        => Assert.Null(ReadingRange.SpanOf(ReadingRange.All));

    [Fact]
    public void AWordThatIsNotARange_Throws()
        => Assert.Throws<ArgumentException>(() => ReadingRange.SpanOf("fortnight"));

    /// <summary>
    /// Whatever the window, the step is short enough that the series fits in the points a line can
    /// draw. A head count changes every half minute at most, so a day's step is shorter than the
    /// poll rate and a day comes back as every reading.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(30)]
    [InlineData(1830)]
    public void AWindowNeverComesBackAsMoreThanTheMostPoints(int days)
    {
        var span = TimeSpan.FromDays(days);
        var step = ReadingRange.StepSeconds(span);

        // A window that starts part-way into a step touches one more step than it divides into.
        Assert.True(Math.Ceiling(span.TotalSeconds / step) + 1 <= ReadingRange.MaxPoints);
    }

    [Fact]
    public void AStepIsNeverShorterThanASecond()
    {
        Assert.Equal(1, ReadingRange.StepSeconds(TimeSpan.Zero));
        Assert.Equal(1, ReadingRange.StepSeconds(TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// Clock-friendly lengths: three minutes for a day, half an hour for a week, two hours for a
    /// month, and whole days past that.
    /// </summary>
    [Theory]
    [InlineData(1, 180)]
    [InlineData(7, 1800)]
    [InlineData(30, 7200)]
    [InlineData(400, 86400)]
    [InlineData(1830, 4 * 86400)]
    public void TheStep_IsTheShortestLengthOnTheList_ThatFits(int days, int expected)
        => Assert.Equal(expected, ReadingRange.StepSeconds(TimeSpan.FromDays(days)));

    /// <summary>
    /// "All time" grows by the minute. Its step must not grow with it, or every boundary moves and a
    /// reload draws the same evening at a different height.
    /// </summary>
    [Fact]
    public void AGrowingWindow_KeepsItsStep()
    {
        var span = TimeSpan.FromDays(100);
        var step = ReadingRange.StepSeconds(span);

        for (var minutes = 0; minutes < 24 * 60; minutes += 7)
            Assert.Equal(step, ReadingRange.StepSeconds(span + TimeSpan.FromMinutes(minutes)));
    }

    /// <summary>A step is a place on the clock, not a distance from the window's start.</summary>
    [Fact]
    public void AMoment_IsInTheSameStep_WhateverTheWindow()
    {
        var at = new DateTimeOffset(2026, 9, 25, 20, 15, 0, TimeSpan.Zero);

        // 20:15 is in the half hour that starts at 20:00, counted from the epoch.
        Assert.Equal(
            ReadingRange.StepOf(new DateTimeOffset(2026, 9, 25, 20, 0, 0, TimeSpan.Zero), 1800),
            ReadingRange.StepOf(at, 1800));
        Assert.NotEqual(
            ReadingRange.StepOf(new DateTimeOffset(2026, 9, 25, 19, 59, 59, TimeSpan.Zero), 1800),
            ReadingRange.StepOf(at, 1800));
        Assert.Equal(at.ToUnixTimeSeconds() / 1800, ReadingRange.StepOf(at, 1800));
    }
}
