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

        Assert.True(span.TotalSeconds / step <= ReadingRange.MaxPoints);
    }

    [Fact]
    public void AStepIsNeverShorterThanASecond()
    {
        Assert.Equal(1, ReadingRange.StepSeconds(TimeSpan.Zero));
        Assert.Equal(1, ReadingRange.StepSeconds(TimeSpan.FromSeconds(30)));
    }
}
