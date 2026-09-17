using System.Threading.Channels;
using Modbot.Companion.Ingest;
using Modbot.Companion.Overlay;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// The live link: the WebSocket first, long polling when the socket cannot be kept, the socket
/// again later, and a cursor carried across all of it so a dropped connection costs delay and
/// never an event.
/// </summary>
public class LiveLinkTests
{
    private const string Instance = "39911";

    private static readonly ServerPairing Pairing =
        new("cats", new Uri("https://modbot.example"), "token", "grp_cats");

    /// <summary>A server-side socket the test feeds by hand. Delivery runs the link's loop inline.</summary>
    private sealed class FakeSocket : ILiveSocket
    {
        private readonly Channel<LiveSocketMessage?> _incoming = Channel.CreateUnbounded<LiveSocketMessage?>(
            new UnboundedChannelOptions { AllowSynchronousContinuations = true });

        public List<string> Subscribed { get; } = [];

        public bool Closed { get; private set; }

        public void Deliver(LiveSocketMessage message) => _incoming.Writer.TryWrite(message);

        /// <summary>The connection goes away under the client.</summary>
        public void Drop() => _incoming.Writer.TryWrite(null);

        public async Task<LiveSocketMessage?> ReceiveAsync(CancellationToken cancellationToken)
            => await _incoming.Reader.ReadAsync(cancellationToken);

        public Task SubscribeAsync(string instanceId, CancellationToken cancellationToken)
        {
            Subscribed.Add(instanceId);
            return Task.CompletedTask;
        }

        public Task CloseAsync()
        {
            Closed = true;
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private sealed class FakeSockets : ILiveSocketFactory
    {
        public Queue<Func<LiveConnect>> Answers { get; } = new();

        public List<(string Instance, string? After)> Connects { get; } = [];

        public FakeSocket Accept()
        {
            var socket = new FakeSocket();
            Answers.Enqueue(() => new LiveConnect(LiveConnectOutcome.Connected, socket));
            return socket;
        }

        public void Refuse(LiveConnectOutcome outcome = LiveConnectOutcome.Unreachable)
            => Answers.Enqueue(() => new LiveConnect(outcome));

        public Task<LiveConnect> ConnectAsync(ServerPairing pairing, string instanceId, string? after, CancellationToken cancellationToken)
        {
            Connects.Add((instanceId, after));
            return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue()() : new LiveConnect(LiveConnectOutcome.Unreachable));
        }
    }

    private sealed class ScriptedReads : IOverlayReadClient
    {
        public Queue<ReadResult<LivePollPage>> Pages { get; } = new();

        public List<(string Instance, string? After, int Wait)> Polls { get; } = [];

        public Task<ReadResult<LivePollPage>> PollLiveAsync(
            ServerPairing pairing, string instanceId, string? after, int waitSeconds, CancellationToken cancellationToken)
        {
            Polls.Add((instanceId, after, waitSeconds));
            return Task.FromResult(Pages.Count > 0
                ? Pages.Dequeue()
                : new ReadResult<LivePollPage>(ReadOutcome.NothingWaiting, Elapsed: TimeSpan.FromSeconds(waitSeconds)));
        }

        public Task<ReadResult<InstanceContext>> GetContextAsync(ServerPairing pairing, string instanceId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ReadResult<UserSummary>> GetUserAsync(ServerPairing pairing, string subjectId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private static LiveEvent Event(string id, string kind = LiveEventKinds.PersonJoined, string instance = Instance, bool byThisDevice = false) => new(
        id, id, kind, new DateTimeOffset(2026, 9, 16, 20, 0, 0, TimeSpan.Zero), instance,
        new LivePerson("usr_" + id, "Person " + id, null, kind == LiveEventKinds.FlaggedJoin ? RosterStanding.Flagged : RosterStanding.Ordinary, kind == LiveEventKinds.FlaggedJoin ? 2 : 0, []),
        kind == LiveEventKinds.FlaggedJoin, kind == LiveEventKinds.FlaggedJoin ? "2 prior moderation actions" : null, byThisDevice);

    private static (LiveLink Link, FakeSockets Sockets, ScriptedReads Reads, FakeClock Clock) Build(bool withSockets = true)
    {
        var clock = new FakeClock();
        var sockets = new FakeSockets();
        var reads = new ScriptedReads();
        var link = new LiveLink(withSockets ? sockets : null, reads, clock, Pairing, new BackoffPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), jitter: () => 0));

        return (link, sockets, reads, clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSocketComesFirst_AndItsEventsArriveWithTheCursorRemembered()
    {
        var (link, sockets, reads, _) = Build();
        var socket = sockets.Accept();

        Assert.Equal("Off", link.Word);

        link.Follow(Instance);
        await link.PumpAsync(Ct);
        Assert.Equal(LiveLinkState.Connecting, link.State);

        await link.PumpAsync(Ct);
        Assert.Equal(LiveLinkState.Live, link.State);
        Assert.Equal("Live", link.Word);
        Assert.Equal([(Instance, (string?)null)], sockets.Connects);

        socket.Deliver(new LiveSocketMessage("hello", Cursor: "10"));
        socket.Deliver(new LiveSocketMessage("event", Event("11")));
        socket.Deliver(new LiveSocketMessage("event", Event("12", LiveEventKinds.FlaggedJoin)));
        socket.Deliver(new LiveSocketMessage("heartbeat", Cursor: "14"));

        await link.PumpAsync(Ct);
        var events = link.Drain();

        Assert.Equal(["11", "12"], events.Select(e => e.Id));
        Assert.Equal("14", link.Cursor);
        Assert.Empty(link.Drain());
        Assert.Empty(reads.Polls);
    }

    [Fact]
    public async Task ADroppedSocket_IsReopenedFromTheLastCursor_AfterABackoff()
    {
        var (link, sockets, _, clock) = Build();
        var first = sockets.Accept();

        link.Follow(Instance);
        await link.PumpAsync(Ct);
        await link.PumpAsync(Ct);
        first.Deliver(new LiveSocketMessage("event", Event("21")));

        first.Drop();
        await link.PumpAsync(Ct);
        Assert.Equal(LiveLinkState.Connecting, link.State);
        Assert.Single(sockets.Connects);

        // Not straight away.
        await link.PumpAsync(Ct);
        Assert.Single(sockets.Connects);

        sockets.Accept();
        clock.Advance(TimeSpan.FromSeconds(31));
        await link.PumpAsync(Ct);
        await link.PumpAsync(Ct);

        Assert.Equal(2, sockets.Connects.Count);
        Assert.Equal((Instance, "21"), sockets.Connects[1]);
        Assert.Equal(LiveLinkState.Live, link.State);
    }

    [Fact]
    public async Task ThreeFailuresInTwoMinutes_MeanPollingForAWhile_ThenTheSocketAgain()
    {
        var (link, sockets, reads, clock) = Build();
        link.Follow(Instance);

        for (var attempt = 0; attempt < LiveLink.DropsBeforePolling; attempt++)
        {
            sockets.Refuse();
            await link.PumpAsync(Ct);
            await link.PumpAsync(Ct);
            clock.Advance(TimeSpan.FromSeconds(31));
        }

        Assert.Equal(LiveLink.DropsBeforePolling, sockets.Connects.Count);

        // The same events, by polling, with the cursor carried on.
        reads.Pages.Enqueue(new ReadResult<LivePollPage>(ReadOutcome.Fetched, new LivePollPage([Event("31"), Event("32")], "32", false)));
        await link.PumpAsync(Ct);
        Assert.Equal(LiveLinkState.Polling, link.State);
        Assert.Equal("Polling", link.Word);

        await link.PumpAsync(Ct);
        Assert.Equal(["31", "32"], link.Drain().Select(e => e.Id));
        Assert.Equal("32", link.Cursor);

        await link.PumpAsync(Ct);
        Assert.Equal((Instance, "32", LiveLink.PollWaitSeconds), reads.Polls[^1]);
        Assert.Equal(LiveLink.DropsBeforePolling, sockets.Connects.Count);

        // The spell ends, and the socket is tried again from where polling got to.
        sockets.Accept();
        clock.Advance(LiveLink.PollingSpell + TimeSpan.FromSeconds(1));
        await link.PumpAsync(Ct);
        await link.PumpAsync(Ct);
        await link.PumpAsync(Ct);

        Assert.Equal(LiveLinkState.Live, link.State);
        Assert.Equal((Instance, "32"), sockets.Connects[^1]);
    }

    [Fact]
    public async Task WithNoSocketToOpen_ItPollsForGood()
    {
        var (link, _, reads, _) = Build(withSockets: false);
        link.Follow(Instance);

        reads.Pages.Enqueue(new ReadResult<LivePollPage>(ReadOutcome.Fetched, new LivePollPage([Event("41")], "41", false)));
        await link.PumpAsync(Ct);
        await link.PumpAsync(Ct);
        await link.PumpAsync(Ct);

        Assert.Equal(LiveLinkState.Polling, link.State);
        Assert.Single(link.Drain());
        Assert.Equal((Instance, "41", LiveLink.PollWaitSeconds), reads.Polls[^1]);
    }

    [Fact]
    public async Task AnUnreachableServerWhilePolling_BacksOff_AndAQuietPollDoesNot()
    {
        var (link, _, reads, clock) = Build(withSockets: false);
        link.Follow(Instance);

        reads.Pages.Enqueue(new ReadResult<LivePollPage>(ReadOutcome.Unreachable));
        await link.PumpAsync(Ct);
        await link.PumpAsync(Ct);
        await link.PumpAsync(Ct);
        Assert.Single(reads.Polls);

        clock.Advance(TimeSpan.FromSeconds(31));
        await link.PumpAsync(Ct);
        Assert.Equal(2, reads.Polls.Count);

        // A quiet wait is the ordinary answer and changes nothing: the next poll goes at once.
        await link.PumpAsync(Ct);
        Assert.Equal(3, reads.Polls.Count);
    }

    [Fact]
    public async Task ARejectedToken_StopsTheLink_ForGood()
    {
        var (link, sockets, reads, clock) = Build();
        link.Follow(Instance);

        sockets.Refuse(LiveConnectOutcome.Unauthorised);
        await link.PumpAsync(Ct);
        await link.PumpAsync(Ct);

        Assert.Equal(LiveLinkState.Stopped, link.State);
        Assert.True(link.IsStopped);
        Assert.Equal("Stopped", link.Word);

        clock.Advance(TimeSpan.FromHours(1));
        await link.PumpAsync(Ct);
        Assert.Single(sockets.Connects);
        Assert.Empty(reads.Polls);
    }

    [Fact]
    public async Task WalkingIntoAnotherInstance_IsASubscribeOnTheOpenSocket()
    {
        var (link, sockets, _, _) = Build();
        var socket = sockets.Accept();

        link.Follow(Instance);
        await link.PumpAsync(Ct);
        await link.PumpAsync(Ct);

        link.Follow("85019");
        await link.PumpAsync(Ct);

        Assert.Equal(["85019"], socket.Subscribed);
        Assert.Single(sockets.Connects);
        Assert.Equal(LiveLinkState.Live, link.State);
    }

    [Fact]
    public async Task LeavingEveryGroupInstance_ClosesTheSocket()
    {
        var (link, sockets, _, _) = Build();
        var socket = sockets.Accept();

        link.Follow(Instance);
        await link.PumpAsync(Ct);
        await link.PumpAsync(Ct);
        Assert.Equal(LiveLinkState.Live, link.State);

        link.Follow(null);
        await link.PumpAsync(Ct);

        Assert.True(socket.Closed);
        Assert.Equal(LiveLinkState.Off, link.State);
        Assert.Equal("Off", link.Word);
        Assert.Single(sockets.Connects);
    }

    [Fact]
    public void OnlyAFlaggedJoinBecomesACard()
    {
        var flagged = Event("51", LiveEventKinds.FlaggedJoin).ToAlert();

        Assert.NotNull(flagged);
        Assert.Equal("51", flagged.AlertId);
        Assert.Equal("usr_51", flagged.SubjectId);
        Assert.Equal(Instance, flagged.InstanceId);
        Assert.Equal("2 prior moderation actions", flagged.Reason);
        Assert.Equal(2, flagged.PriorActions);

        Assert.Null(Event("52").ToAlert());
        Assert.Null(Event("53", LiveEventKinds.PersonLeft).ToAlert());
    }
}
