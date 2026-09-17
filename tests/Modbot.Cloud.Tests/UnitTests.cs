using Modbot.Cloud.Common;
using Modbot.Cloud.Engine;
using Modbot.Cloud.Features.EventBackup;

namespace Modbot.Cloud.Tests;

public class UnitTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnUnmeasuredClockIsCorrectedByWhatCloudObserved()
    {
        var reading = ClockCorrection.Read(Now, Now.AddSeconds(-2), null, "unknown");

        Assert.Equal(TimeSpan.FromSeconds(2), reading.Applied);
        Assert.Equal(TimeSpan.FromSeconds(2), reading.Adjustment);
        Assert.False(reading.Disagrees);
    }

    [Theory]
    [InlineData("good", 1_500, 0)]
    [InlineData("fair", 1_500, 0)]
    [InlineData("poor", 2_000, 500)]
    public void TheClientsMeasureStandsOnlyWhenItIsGoodOrFair(string confidence, long appliedMs, long adjustmentMs)
    {
        var reading = ClockCorrection.Read(Now, Now.AddSeconds(-2), 1_500, confidence);

        Assert.Equal(TimeSpan.FromMilliseconds(appliedMs), reading.Applied);
        Assert.Equal(TimeSpan.FromMilliseconds(adjustmentMs), reading.Adjustment);
        Assert.False(reading.Disagrees);
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
    public void CompanionEventTypesUseTheServersNamesAndUnknownOnesKeepTheirOwn()
    {
        Assert.Equal(("vrchat.instance.join", null), EventTypes.Classify("InstanceJoined"));
        Assert.Equal(("vrchat.instance.log-stopped", null), EventTypes.Classify("LogStopped"));
        Assert.Equal((EventTypes.Unrecognised, "InstanceTeleported"), EventTypes.Classify("InstanceTeleported"));
    }
}
