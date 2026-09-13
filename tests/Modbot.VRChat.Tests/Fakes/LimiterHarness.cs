using Modbot.TestSupport;
using Modbot.VRChat.RateLimiting;

namespace Modbot.VRChat.Tests.Fakes;

/// <summary>
/// A limiter wired to a fake clock, a simulated waiter and a shared store.
/// </summary>
/// <remarks>
/// The store outlives the limiter on purpose: <see cref="Restart"/> is how a test reproduces a
/// redeploy or a crash-loop, which is the failure spec 4.3.2 exists to prevent.
/// </remarks>
public sealed class LimiterHarness
{
    public LimiterHarness(RateLimitOptions? options = null, FakeClock? clock = null)
    {
        Clock = clock ?? new FakeClock();
        Delays = new TestDelayScheduler(Clock);
        Store = new RecordingRateLimitStore();
        Options = options ?? new RateLimitOptions();
        Limiter = new InProcessRateLimiter(Store, Clock, Delays, Options);
    }

    /// <summary>
    /// Budgets so large that nothing ever waits, for the tests that are about ordering rather
    /// than pacing. Mixing the two would make those tests depend on the clock advancing from
    /// several threads at once, which is a race, not a test.
    /// </summary>
    public static RateLimitOptions Unpaced() => new()
    {
        Classes = VRChatRateLimits.Defaults.ToDictionary(
            pair => pair.Key,
            pair => pair.Value with
            {
                HardMaxPerSecond = 1_000,
                DefaultCeilingPerSecond = 10_000,
                BurstTokens = 1_000,
            },
            StringComparer.Ordinal),
    };

    public FakeClock Clock { get; }

    public TestDelayScheduler Delays { get; }

    public RecordingRateLimitStore Store { get; }

    public RateLimitOptions Options { get; }

    public InProcessRateLimiter Limiter { get; private set; }

    /// <summary>Drops the limiter and builds a new one over the same persisted state.</summary>
    public InProcessRateLimiter Restart()
    {
        Limiter = new InProcessRateLimiter(Store, Clock, Delays, Options);
        return Limiter;
    }

    /// <summary>Acquires, reports the given status, and releases -- one complete call.</summary>
    public async Task<IRateLimitLease> CallAsync(
        VRChatEndpoint endpoint,
        int status = 200,
        VRChatCallPriority priority = VRChatCallPriority.Background,
        CancellationToken ct = default)
    {
        var lease = await Limiter.AcquireAsync(endpoint, priority, ct);
        await using (lease)
        {
            if (lease.IsAcquired)
                await lease.ReportAsync(status, ct);
        }

        return lease;
    }

    public async Task<RateLimitBucketHealth> HealthAsync(string bucket)
    {
        var all = await Limiter.DescribeAsync(TestContext.Current.CancellationToken);
        return all.Single(b => b.Name == bucket);
    }
}
