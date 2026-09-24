using Modbot.Api.Features.Auth.Login;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Auth;

/// <summary>
/// The slowdown on repeated attempts (accounts and access design §7), and the sweep that stops
/// a guessing run leaving a count behind per address for the life of the process.
/// </summary>
public class AttemptSlowdownTests
{
    private static LoginSlowdown With(FakeClock clock) => new(clock);

    [Fact]
    public void TheWaitDoublesAndIsCapped()
    {
        Assert.Equal(TimeSpan.Zero, AttemptSlowdown.WaitFor(0));
        Assert.Equal(TimeSpan.FromSeconds(1), AttemptSlowdown.WaitFor(1));
        Assert.Equal(TimeSpan.FromSeconds(2), AttemptSlowdown.WaitFor(2));
        Assert.Equal(TimeSpan.FromSeconds(4), AttemptSlowdown.WaitFor(3));
        Assert.Equal(AttemptSlowdown.MaxWait, AttemptSlowdown.WaitFor(20));
    }

    [Fact]
    public void ASuccessClearsTheCount()
    {
        var clock = new FakeClock();
        var slowdown = With(clock);

        slowdown.RecordFailure("owner", "10.0.0.1");
        slowdown.RecordFailure("owner", "10.0.0.1");
        Assert.True(slowdown.WaitFor("owner", "10.0.0.1") > TimeSpan.Zero);

        slowdown.RecordSuccess("owner", "10.0.0.1");
        Assert.Equal(TimeSpan.Zero, slowdown.WaitFor("owner", "10.0.0.1"));
    }

    [Fact]
    public void CountsPastTheWindowAreSweptOutOnceTheStoreHasGrown()
    {
        var clock = new FakeClock();
        var slowdown = With(clock);

        // A guessing run from two thousand addresses, none of them ever seen again.
        for (var i = 0; i < 2_000; i++)
            slowdown.RecordFailure($"user{i}", $"10.1.{i / 256}.{i % 256}");

        var afterTheRun = slowdown.Held;
        Assert.True(afterTheRun > 1_000, $"expected the run to be held, held {afterTheRun}");

        clock.Advance(AttemptSlowdown.Window + TimeSpan.FromMinutes(1));

        // The attacker keeps going, long after. A sweep rides along once the attempts have paid
        // for the walk: as many attempts as there are counts to look at.
        for (var i = 0; i < 6_000; i++)
            slowdown.RecordFailure("latecomer", "10.9.9.9");

        // Two keys survive: the username and the address of the attempt that is still recent.
        Assert.Equal(2, slowdown.Held);
    }

    [Fact]
    public void ASweepDoesNotForgetACountThatIsStillRecent()
    {
        var clock = new FakeClock();
        var slowdown = With(clock);

        slowdown.RecordFailure("owner", "10.0.0.1");
        slowdown.RecordFailure("owner", "10.0.0.1");

        for (var i = 0; i < 2_000; i++)
            slowdown.RecordFailure($"user{i}", $"10.1.{i / 256}.{i % 256}");

        Assert.True(slowdown.WaitFor("owner", "10.0.0.1") > TimeSpan.Zero);
    }

    [Fact]
    public void AQuietModbotHoldsWhatItSawAndSweepsNothing()
    {
        var clock = new FakeClock();
        var slowdown = With(clock);

        slowdown.RecordFailure("owner", "10.0.0.1");

        Assert.Equal(2, slowdown.Held);
        Assert.Equal(TimeSpan.FromSeconds(1), slowdown.WaitFor("owner", "10.0.0.1"));
    }
}
