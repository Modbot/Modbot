using System.Text;
using System.Threading.Channels;
using Modbot.Companion.Ingest;
using Modbot.Companion.Overlay;
using Modbot.Shared.Clips;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// The clip link: a small live connection per paired server that names no instance, so clip
/// requests reach the companion whatever the panels and the moderator are doing.
/// </summary>
public class ClipLinkTests
{
    private static readonly ServerPairing Pairing =
        new("cats", new Uri("https://modbot.example"), "token", "grp_cats");

    private sealed class FakeSocket : ILiveSocket
    {
        private readonly Channel<LiveSocketMessage?> _incoming = Channel.CreateUnbounded<LiveSocketMessage?>(
            new UnboundedChannelOptions { AllowSynchronousContinuations = true });

        public bool Closed { get; private set; }

        public void Deliver(LiveSocketMessage message) => _incoming.Writer.TryWrite(message);

        public async Task<LiveSocketMessage?> ReceiveAsync(CancellationToken cancellationToken)
            => await _incoming.Reader.ReadAsync(cancellationToken);

        public Task SubscribeAsync(string instanceId, string? worldId, CancellationToken cancellationToken)
            => Task.CompletedTask;

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

        public List<string?> ConnectInstances { get; } = [];

        public FakeSocket Accept()
        {
            var socket = new FakeSocket();
            Answers.Enqueue(() => new LiveConnect(LiveConnectOutcome.Connected, socket));
            return socket;
        }

        public void Refuse(LiveConnectOutcome outcome)
            => Answers.Enqueue(() => new LiveConnect(outcome));

        public Task<LiveConnect> ConnectAsync(ServerPairing pairing, string? instanceId, string? worldId, string? after, CancellationToken cancellationToken)
        {
            ConnectInstances.Add(instanceId);
            return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue()() : new LiveConnect(LiveConnectOutcome.Unreachable));
        }
    }

    private sealed class Reads : IOverlayReadClient
    {
        public Queue<ReadResult<LivePollPage>> Pages { get; } = new();

        public int RequestPolls { get; private set; }

        public Task<ReadResult<LivePollPage>> PollRequestsAsync(ServerPairing pairing, string? after, int waitSeconds, CancellationToken cancellationToken)
        {
            RequestPolls++;
            return Task.FromResult(Pages.Count > 0
                ? Pages.Dequeue()
                : new ReadResult<LivePollPage>(ReadOutcome.NothingWaiting));
        }

        public Task<ReadResult<LivePollPage>> PollLiveAsync(
            ServerPairing pairing, string instanceId, string? worldId, string? after, int waitSeconds, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ReadResult<InstanceContext>> GetContextAsync(ServerPairing pairing, string instanceId, string? worldId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ReadResult<UserSummary>> GetUserAsync(ServerPairing pairing, string subjectId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private static ClipRequestMessage Request(Guid id) => new(
        ClipRequestWire.Kind, id, ClipRequestWire.Ban,
        new DateTimeOffset(2026, 10, 24, 20, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 10, 24, 20, 15, 0, TimeSpan.Zero));

    private static LiveSocketMessage Message(ClipRequestMessage request) => new(ClipRequestWire.Kind, ClipRequest: request);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task Connect(ClipLinks links)
    {
        await links.PumpAsync(Ct);
        await links.PumpAsync(Ct);
    }

    [Fact]
    public async Task AWantedServerConnectsWithNoInstance_AndARequestOnTheSocketIsRaised()
    {
        var clock = new FakeClock();
        var sockets = new FakeSockets();
        var socket = sockets.Accept();
        using var links = new ClipLinks(sockets, new Reads(), clock);
        var raised = new List<ClipRequestMessage>();
        links.ClipRequested += (_, r) => raised.Add(r);

        links.Want([Pairing]);
        await Connect(links);

        Assert.Equal([(string?)null], sockets.ConnectInstances);
        Assert.Equal("Live", links.Words["cats"]);

        var id = Guid.NewGuid();
        socket.Deliver(Message(Request(id)));
        await links.PumpAsync(Ct);

        Assert.Equal([id], raised.Select(r => r.RequestId));
    }

    [Fact]
    public async Task TheSameRequestOnTheSocketAndInAPollIsRaisedOnce()
    {
        var clock = new FakeClock();
        var sockets = new FakeSockets();
        var reads = new Reads();
        sockets.Refuse(LiveConnectOutcome.Unreachable);
        var id = Guid.NewGuid();
        var raised = 0;

        using var link = new ClipLink(Pairing, sockets, reads, clock);
        link.ClipRequested += (_, _) => raised++;

        reads.Pages.Enqueue(new(ReadOutcome.Fetched, new LivePollPage([], "5", false, [Request(id), Request(id)])));
        await link.PumpAsync(Ct);
        await link.PumpAsync(Ct);
        await link.PumpAsync(Ct);

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task ASwitchedOffServerKeepsItsLinkForTheGraceAndThenClosesIt()
    {
        var clock = new FakeClock();
        var sockets = new FakeSockets();
        var socket = sockets.Accept();
        using var links = new ClipLinks(sockets, new Reads(), clock);

        links.Want([Pairing]);
        await Connect(links);

        links.Want([]);
        await links.PumpAsync(Ct);
        Assert.False(socket.Closed);
        Assert.True(links.Words.ContainsKey("cats"));

        clock.Advance(ClipLinks.CloseDelay + TimeSpan.FromSeconds(1));
        await links.PumpAsync(Ct);

        Assert.True(socket.Closed);
        Assert.Empty(links.Words);
    }

    [Fact]
    public async Task ANewTokenReplacesTheLink()
    {
        var clock = new FakeClock();
        var sockets = new FakeSockets();
        var first = sockets.Accept();
        var second = sockets.Accept();
        using var links = new ClipLinks(sockets, new Reads(), clock);

        links.Want([Pairing]);
        await Connect(links);

        links.Want([new ServerPairing("cats", new Uri("https://modbot.example"), "other", "grp_cats")]);
        await Connect(links);

        Assert.True(first.Closed);
        Assert.False(second.Closed);
        Assert.Equal(2, sockets.ConnectInstances.Count);
    }

    [Fact]
    public async Task ARejectedTokenStopsTheLink()
    {
        var clock = new FakeClock();
        var sockets = new FakeSockets();
        sockets.Refuse(LiveConnectOutcome.Unauthorised);
        using var link = new ClipLink(Pairing, sockets, new Reads(), clock);

        await link.PumpAsync(Ct);
        await link.PumpAsync(Ct);

        Assert.Equal(LiveLinkState.Stopped, link.State);
    }

    [Fact]
    public void AClipRequestMessageIsParsedFromItsFlatJson()
    {
        var id = Guid.NewGuid();
        var json = $$"""{"kind":"clip_request","requestId":"{{id}}","action":"ban","occurredAt":"2026-10-24T20:00:00+00:00","expiresAt":"2026-10-24T20:15:00+00:00"}""";

        var message = ClientLiveSocketFactory.ParseMessage(Encoding.UTF8.GetBytes(json));

        Assert.NotNull(message?.ClipRequest);
        Assert.Equal(id, message.ClipRequest.RequestId);
        Assert.Equal(ClipRequestWire.Ban, message.ClipRequest.Action);
    }

    [Fact]
    public void WithNoInstanceTheLiveEndpointsNameNoneAndThePollStillCarriesTheClipSettings()
    {
        var socket = Pairing.LiveSocketEndpoint(null, "7").Query;
        var poll = Pairing.LivePollEndpoint(null, "7", 20, clips: new ClipSettingsReport(true, true, 10, true)).Query;

        Assert.DoesNotContain("instanceId", socket, StringComparison.Ordinal);
        Assert.DoesNotContain("moderation", socket, StringComparison.Ordinal);
        Assert.DoesNotContain("instanceId", poll, StringComparison.Ordinal);
        Assert.DoesNotContain("moderation", poll, StringComparison.Ordinal);
        Assert.Contains("clipsSendForActions=true", poll, StringComparison.Ordinal);
        Assert.Contains("wait=20", poll, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOverlaysLinkIgnoresClipRequests_AndTheClipLinkIgnoresPresence()
    {
        var clock = new FakeClock();
        var sockets = new FakeSockets();
        var overlaySocket = sockets.Accept();
        var clipSocket = sockets.Accept();
        var id = Guid.NewGuid();

        using var overlay = new LiveLink(sockets, new Reads(), clock, Pairing);
        overlay.Follow("39911");
        await overlay.PumpAsync(Ct);
        await overlay.PumpAsync(Ct);
        overlaySocket.Deliver(Message(Request(id)));
        await overlay.PumpAsync(Ct);
        Assert.Empty(overlay.DrainClipRequests());

        using var clip = new LiveLink(sockets, new Reads(), clock, Pairing, clipsOnly: true);
        await clip.PumpAsync(Ct);
        await clip.PumpAsync(Ct);
        clipSocket.Deliver(new LiveSocketMessage("event", new LiveEvent(
            "1", "1", LiveEventKinds.PersonJoined, new DateTimeOffset(2026, 10, 24, 20, 0, 0, TimeSpan.Zero), "39911",
            new LivePerson("usr_1", "Person", null, RosterStanding.Ordinary, 0, []), false, null, false, null)));
        clipSocket.Deliver(Message(Request(id)));
        await clip.PumpAsync(Ct);

        Assert.Empty(clip.Drain());
        Assert.Equal([id], clip.DrainClipRequests().Select(r => r.RequestId));
    }
}
