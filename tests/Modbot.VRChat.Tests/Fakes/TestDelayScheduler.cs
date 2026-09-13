using Modbot.TestSupport;
using Modbot.VRChat.RateLimiting;

namespace Modbot.VRChat.Tests.Fakes;

/// <summary>
/// Turns a wait into a clock advance, so a fifteen-minute cold stop costs a test nothing.
/// </summary>
/// <remarks>
/// The alternative is a suite that either takes hours or only asserts the parts of the design
/// that happen to be fast, which for spec 4.3.1 is none of the interesting ones.
/// </remarks>
public sealed class TestDelayScheduler(FakeClock clock) : IDelayScheduler
{
    /// <summary>Total simulated time, so a test can assert on the rate work was issued at.</summary>
    public TimeSpan TotalDelay { get; private set; }

    public int Delays { get; private set; }

    public Task DelayAsync(TimeSpan delay, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (delay <= TimeSpan.Zero)
            return Task.CompletedTask;

        Delays++;
        TotalDelay += delay;
        clock.Advance(delay);

        return Task.CompletedTask;
    }
}
