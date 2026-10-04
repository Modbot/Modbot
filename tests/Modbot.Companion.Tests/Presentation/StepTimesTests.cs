using System.Diagnostics;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// Timing the steps of one piece of work for the log: each step takes the time since the one
/// before it, and the slowest is named first.
/// </summary>
public class StepTimesTests
{
    private static long Ms(double ms) => (long)(ms * Stopwatch.Frequency / 1000);

    [Fact]
    public void EachStepTakesTheTimeSinceTheOneBeforeIt()
    {
        var now = Ms(1000);
        var times = new StepTimes(() => now);

        now += Ms(5);
        times.Done("panel status");
        now += Ms(300);
        times.Done("clips");
        now += Ms(20);
        times.Done("window");

        Assert.Equal(new[] { "panel status", "clips", "window" }, times.Steps.Select(s => s.Step));
        Assert.Equal(5, times.Steps[0].Took.TotalMilliseconds, 1);
        Assert.Equal(300, times.Steps[1].Took.TotalMilliseconds, 1);
        Assert.Equal(20, times.Steps[2].Took.TotalMilliseconds, 1);
        Assert.Equal(325, times.Total.TotalMilliseconds, 1);
    }

    [Fact]
    public void TheSlowestStepIsNamedFirst()
    {
        var now = 0L;
        var times = new StepTimes(() => now);

        now += Ms(5);
        times.Done("panel status");
        now += Ms(300);
        times.Done("clips");
        now += Ms(20);
        times.Done("window");

        Assert.Equal("clips 300 ms, window 20 ms, panel status 5 ms", times.Describe());
    }

    [Fact]
    public void NothingDoneYetTookNoTime()
    {
        var times = new StepTimes(() => 42);

        Assert.Empty(times.Steps);
        Assert.Equal(TimeSpan.Zero, times.Total);
        Assert.Equal("", times.Describe());
    }
}
