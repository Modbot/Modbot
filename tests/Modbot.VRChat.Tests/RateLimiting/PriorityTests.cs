using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.RateLimiting;

/// <summary>
/// Serialisation and preemption: spec 4.1's "one call at a time", spec 4.3.3's "the priority
/// queue is the budget allocator, not a nicety".
/// </summary>
public class PriorityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VRChatEndpoint Members =
        new(VRChatEndpointClass.GroupsMembers, "grp_test");

    private static readonly VRChatEndpoint Ban =
        new(VRChatEndpointClass.ModerationWrite, "grp_test");

    [Fact]
    public async Task TheGateHandsTheNextTurnToTheHighestPriorityWaiter()
    {
        var gate = new PriorityGate();
        var held = await gate.EnterAsync(VRChatCallPriority.Background, Ct);

        // Queued in the order a sync-heavy system produces them: background first, and the
        // moderator arrives afterwards.
        var background = gate.EnterAsync(VRChatCallPriority.Background, Ct);
        var interactive = gate.EnterAsync(VRChatCallPriority.Interactive, Ct);

        Assert.False(background.IsCompleted);
        Assert.False(interactive.IsCompleted);

        held.Dispose();

        (await interactive).Dispose();
        Assert.False(background.IsCompleted);

        (await background).Dispose();
    }

    [Fact]
    public async Task EqualPrioritiesKeepTheirArrivalOrder()
    {
        var gate = new PriorityGate();
        var held = await gate.EnterAsync(VRChatCallPriority.Background, Ct);

        var first = gate.EnterAsync(VRChatCallPriority.Background, Ct);
        var second = gate.EnterAsync(VRChatCallPriority.Background, Ct);

        held.Dispose();

        (await first).Dispose();
        (await second).Dispose();
    }

    [Fact]
    public async Task AnInteractiveCallPreemptsQueuedBackgroundSync()
    {
        var harness = new LimiterHarness(LimiterHarness.Unpaced());

        var held = await harness.Limiter.AcquireAsync(Members, VRChatCallPriority.Background, Ct);

        var background = harness.Limiter.AcquireAsync(Members, VRChatCallPriority.Background, Ct);
        var moderation = harness.Limiter.AcquireAsync(Ban, VRChatCallPriority.Interactive, Ct);

        Assert.False(background.IsCompleted);

        await held.DisposeAsync();

        // A ban must not wait behind a member page (spec 4.2).
        await (await moderation).DisposeAsync();
        await (await background).DisposeAsync();
    }

    [Fact]
    public async Task CallsInALaneNeverOverlap()
    {
        var harness = new LimiterHarness(LimiterHarness.Unpaced());
        var concurrent = 0;
        var peak = 0;

        await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
        {
            var lease = await harness.Limiter.AcquireAsync(Members, ct: Ct);
            await using (lease)
            {
                var now = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref peak, now);

                await Task.Yield();
                await lease.ReportAsync(200, Ct);

                Interlocked.Decrement(ref concurrent);
            }
        }));

        // One authenticated session, one call at a time (spec 4.1).
        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task SeparateLanesRunInParallel()
    {
        var harness = new LimiterHarness(LimiterHarness.Unpaced());

        var group = await harness.Limiter.AcquireAsync(Members, ct: Ct);

        // Spec 4.2.5: profile sync has its own lane precisely so it is not stuck behind the group
        // queue. If this blocked, the 1 req/s the exemption buys would be unreachable.
        var profile = harness.Limiter.AcquireAsync(
            new VRChatEndpoint(VRChatEndpointClass.UsersRead), ct: Ct);

        await (await profile).DisposeAsync();
        await group.DisposeAsync();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (value <= seen)
                return;
        }
        while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }
}
