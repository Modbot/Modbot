using Modbot.Cloud.Common;
using Modbot.Cloud.Engine;
using Modbot.Cloud.Features.LogBackup;

namespace Modbot.Cloud.Tests;

public class UnitTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnUnmeasuredClockIsCorrectedByWhatCloudObserved()
    {
        var reading = ClockCorrection.Read(Now, Now.AddSeconds(-2), null, "unknown");

        Assert.Equal(TimeSpan.FromSeconds(2), reading.Applied);
        Assert.False(reading.Disagrees);
    }

    [Theory]
    [InlineData("good", 1_500, false)]
    [InlineData("fair", 1_500, false)]
    [InlineData("poor", 2_000, false)]
    public void TheClientsMeasureIsUsedOnlyWhenItIsGoodOrFair(string confidence, long appliedMs, bool disagrees)
    {
        var reading = ClockCorrection.Read(Now, Now.AddSeconds(-2), 1_500, confidence);

        Assert.Equal(TimeSpan.FromMilliseconds(appliedMs), reading.Applied);
        Assert.Equal(disagrees, reading.Disagrees);
    }

    [Fact]
    public void ALogTimeIsReadWithItsOffset()
    {
        var at = ClockCorrection.OccurredAt(new DateTime(2026, 9, 15, 20, 0, 0), -300, TimeSpan.FromSeconds(1));

        Assert.Equal(new DateTimeOffset(2026, 9, 16, 1, 0, 1, TimeSpan.Zero), at);
        Assert.Null(ClockCorrection.OccurredAt(new DateTime(2026, 9, 15), null, TimeSpan.Zero));
        Assert.Null(ClockCorrection.OccurredAt(DateTime.MinValue, 60, TimeSpan.Zero));
    }

    [Fact]
    public void AWindowLimitSaysHowLongToWait()
    {
        var time = new ManualTime(Now);
        var limit = new WindowLimit(3, TimeSpan.FromMinutes(1), time);

        Assert.Null(limit.TryTake("a", 2));
        Assert.Null(limit.TryTake("a"));

        time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(TimeSpan.FromSeconds(40), limit.TryTake("a"));
        Assert.Null(limit.TryTake("b", 3));

        time.Advance(TimeSpan.FromSeconds(40));
        Assert.Null(limit.TryTake("a", 3));
    }

    [Fact]
    public void PartitionNamesAreOnlyTrustedWhenCloudWouldHaveMadeThem()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), PartitionMaintainer.UpperBound("log_line", "log_line_2026_09"));
        Assert.Null(PartitionMaintainer.UpperBound("log_line", "log_event_2026_09"));
        Assert.Null(PartitionMaintainer.UpperBound("log_line", "log_line_2026_09; DROP TABLE install"));
        Assert.Null(PartitionMaintainer.UpperBound("install", "install_2026_09"));
    }

    [Fact]
    public void UnknownClientEventsAreUnrecognisedWithTheirOwnName()
    {
        Assert.Equal((LogEventTypes.AvatarSwitched, null), LogEventTypes.Classify("AvatarSwitched"));
        Assert.Equal((LogEventTypes.Unrecognised, "Teleported"), LogEventTypes.Classify("Teleported"));
    }
}
