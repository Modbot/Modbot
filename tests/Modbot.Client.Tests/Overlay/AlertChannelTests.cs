using Modbot.Client.Ingest;
using Modbot.Client.Overlay;
using Modbot.TestSupport;

namespace Modbot.Client.Tests.Overlay;

/// <summary>
/// The long poll is the client's only push channel, and the thing most likely to break it is not
/// a Modbot bug — it is a proxy or load balancer that hangs up on idle connections below the
/// requested wait. Read as failures those would back the channel off into uselessness while never
/// delivering anything, which is the worst of both.
/// </summary>
public class AlertChannelTests
{
    private sealed class ScriptedReads : IOverlayReadClient
    {
        private readonly Queue<ReadResult<FlaggedJoinAlert>> _answers = new();

        public List<int> WaitsAsked { get; } = [];

        public void Enqueue(ReadResult<FlaggedJoinAlert> answer) => _answers.Enqueue(answer);

        public Task<ReadResult<FlaggedJoinAlert>> WaitForAlertAsync(
            ServerPairing pairing, int waitSeconds, CancellationToken cancellationToken)
        {
            WaitsAsked.Add(waitSeconds);
            return Task.FromResult(_answers.Count > 0
                ? _answers.Dequeue()
                : new ReadResult<FlaggedJoinAlert>(ReadOutcome.NothingWaiting));
        }

        public Task<ReadResult<InstanceContext>> GetContextAsync(
            ServerPairing pairing, string instanceId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ReadResult<UserSummary>> GetUserAsync(
            ServerPairing pairing, string subjectId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private static readonly ServerPairing Pairing =
        new("cats", new Uri("https://modbot.example"), "token", "grp_cats");

    private static FlaggedJoinAlert Alert() => new(
        "alert-1", "usr_8f2c", "Rin", "39911", "two prior kicks", 2,
        new DateTimeOffset(2026, 9, 12, 20, 14, 7, TimeSpan.Zero));

    [Fact]
    public async Task DeliversAnAlert()
    {
        var reads = new ScriptedReads();
        reads.Enqueue(new ReadResult<FlaggedJoinAlert>(ReadOutcome.Fetched, Alert(), TimeSpan.FromSeconds(3)));
        var channel = new AlertChannel(reads, new FakeClock());

        var poll = await channel.PollOnceAsync(Pairing, TestContext.Current.CancellationToken);

        Assert.Equal(AlertPollOutcome.Alert, poll.Outcome);
        Assert.Equal("usr_8f2c", poll.Alert!.SubjectId);
        Assert.Equal(AlertChannel.DefaultWaitSeconds, poll.WaitUsedSeconds);
    }

    [Fact]
    public async Task AQuietWaitIsTheOrdinaryAnswerAndChangesNothing()
    {
        var reads = new ScriptedReads();
        reads.Enqueue(new ReadResult<FlaggedJoinAlert>(ReadOutcome.NothingWaiting, Elapsed: TimeSpan.FromSeconds(30)));
        var channel = new AlertChannel(reads, new FakeClock());

        var poll = await channel.PollOnceAsync(Pairing, TestContext.Current.CancellationToken);

        Assert.Equal(AlertPollOutcome.Quiet, poll.Outcome);
        Assert.False(channel.IsBackingOff);
        Assert.Equal(AlertChannel.DefaultWaitSeconds, channel.WaitSeconds);
    }

    [Fact]
    public async Task AProxyThatKillsTheConnectionAtThirtySecondsShortensTheWaitInsteadOfBackingOff()
    {
        // The symptom of an idle-connection cap: the request dies at almost exactly the same
        // duration every time. Treating that as an outage would turn the one push channel into a
        // retry loop that still never delivers anything.
        var reads = new ScriptedReads();
        reads.Enqueue(new ReadResult<FlaggedJoinAlert>(ReadOutcome.Unreachable, Elapsed: TimeSpan.FromSeconds(29.5)));
        var channel = new AlertChannel(reads, new FakeClock());

        var poll = await channel.PollOnceAsync(Pairing, TestContext.Current.CancellationToken);

        Assert.Equal(AlertPollOutcome.ConnectionCutShort, poll.Outcome);
        Assert.False(channel.IsBackingOff);
        Assert.True(channel.WaitSeconds < AlertChannel.DefaultWaitSeconds);
        Assert.True(channel.WaitSeconds >= AlertChannel.MinimumWaitSeconds);
    }

    [Fact]
    public async Task TheShortenedWaitIsActuallyUsedOnTheNextPoll()
    {
        var reads = new ScriptedReads();
        reads.Enqueue(new ReadResult<FlaggedJoinAlert>(ReadOutcome.Unreachable, Elapsed: TimeSpan.FromSeconds(20)));
        var channel = new AlertChannel(reads, new FakeClock());

        await channel.PollOnceAsync(Pairing, TestContext.Current.CancellationToken);
        await channel.PollOnceAsync(Pairing, TestContext.Current.CancellationToken);

        Assert.Equal([30, 16], reads.WaitsAsked);
    }

    [Fact]
    public async Task NeverShortensBelowThePointWhereLongPollingIsStillWorthDoing()
    {
        var reads = new ScriptedReads();
        var channel = new AlertChannel(reads, new FakeClock());

        for (var i = 0; i < 10; i++)
        {
            reads.Enqueue(new ReadResult<FlaggedJoinAlert>(
                ReadOutcome.Unreachable, Elapsed: TimeSpan.FromSeconds(channel.WaitSeconds * 0.95)));
            await channel.PollOnceAsync(Pairing, TestContext.Current.CancellationToken);
        }

        Assert.Equal(AlertChannel.MinimumWaitSeconds, channel.WaitSeconds);
    }

    [Fact]
    public async Task CreepsBackTowardsTheFullWaitOnceTheProxyIsGone()
    {
        var reads = new ScriptedReads();
        reads.Enqueue(new ReadResult<FlaggedJoinAlert>(ReadOutcome.Unreachable, Elapsed: TimeSpan.FromSeconds(20)));
        var channel = new AlertChannel(reads, new FakeClock());

        await channel.PollOnceAsync(Pairing, TestContext.Current.CancellationToken);
        var shortened = channel.WaitSeconds;

        reads.Enqueue(new ReadResult<FlaggedJoinAlert>(ReadOutcome.NothingWaiting, Elapsed: TimeSpan.FromSeconds(16)));
        await channel.PollOnceAsync(Pairing, TestContext.Current.CancellationToken);

        Assert.True(channel.WaitSeconds > shortened);
    }

    [Fact]
    public async Task AFailureThatArrivesImmediatelyIsARealOutageAndIsBackedOff()
    {
        var reads = new ScriptedReads();
        reads.Enqueue(new ReadResult<FlaggedJoinAlert>(ReadOutcome.Unreachable, Elapsed: TimeSpan.FromMilliseconds(40)));
        var channel = new AlertChannel(reads, new FakeClock());

        var poll = await channel.PollOnceAsync(Pairing, TestContext.Current.CancellationToken);

        Assert.Equal(AlertPollOutcome.Failed, poll.Outcome);
        Assert.True(channel.IsBackingOff);
        Assert.Equal(AlertChannel.DefaultWaitSeconds, channel.WaitSeconds);
    }

    [Fact]
    public async Task BackoffStopsTheChannelMakingRequestsUntilItExpires()
    {
        var reads = new ScriptedReads();
        reads.Enqueue(new ReadResult<FlaggedJoinAlert>(ReadOutcome.Unreachable, Elapsed: TimeSpan.Zero));

        var clock = new FakeClock();
        var channel = new AlertChannel(reads, clock, new BackoffPolicy(jitter: () => 1.0));

        await channel.PollOnceAsync(Pairing, TestContext.Current.CancellationToken);
        await channel.PollOnceAsync(Pairing, TestContext.Current.CancellationToken);

        Assert.Single(reads.WaitsAsked);

        clock.Advance(TimeSpan.FromMinutes(10));
        await channel.PollOnceAsync(Pairing, TestContext.Current.CancellationToken);

        Assert.Equal(2, reads.WaitsAsked.Count);
    }

    [Fact]
    public async Task A401StopsTheChannelForGoodRatherThanRetrying()
    {
        // A revoked moderator's client must stop, and be seen to stop.
        var reads = new ScriptedReads();
        reads.Enqueue(new ReadResult<FlaggedJoinAlert>(ReadOutcome.Unauthorised, Elapsed: TimeSpan.FromSeconds(1)));
        var channel = new AlertChannel(reads, new FakeClock());

        var poll = await channel.PollOnceAsync(Pairing, TestContext.Current.CancellationToken);

        Assert.Equal(AlertPollOutcome.Unauthorised, poll.Outcome);
        Assert.True(channel.IsStopped);

        await channel.PollOnceAsync(Pairing, TestContext.Current.CancellationToken);
        Assert.Single(reads.WaitsAsked);
    }
}
