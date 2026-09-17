using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Journal;
using Modbot.Companion.Time;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Ingest;

public sealed class ServerConnectionTests : IDisposable
{
    private sealed class ScriptedTransport : IIngestTransport
    {
        private readonly Queue<IngestResult> _answers = new();

        public List<EventBatch> Sent { get; } = [];

        public Exception? Throw { get; set; }

        public ScriptedTransport Then(IngestResult result)
        {
            _answers.Enqueue(result);
            return this;
        }

        public Task<IngestResult> SendAsync(ServerPairing pairing, EventBatch batch, CancellationToken ct)
        {
            Sent.Add(batch);

            if (Throw is { } fault)
                throw fault;

            return Task.FromResult(_answers.Count > 0
                ? _answers.Dequeue()
                : new IngestResult(IngestOutcome.Accepted, batch.Events.Count));
        }
    }

    private static readonly TimeZoneInfo Utc =
        TimeZoneInfo.CreateCustomTimeZone("Test/Utc", TimeSpan.Zero, "UTC", "UTC");

    private readonly string _directory = Directory.CreateTempSubdirectory("modbot-connection-").FullName;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 12, 20, 0, 0, TimeSpan.Zero));
    private readonly ScriptedTransport _transport = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private ServerConnection Connection(
        string serverId = "cats",
        string group = "grp_cats",
        SentJournal? journal = null)
    {
        var pairing = new ServerPairing(serverId, new Uri("https://modbot.example"), "token", group);
        var buffer = new FileEventBuffer(Path.Combine(_directory, $"{serverId}.jsonl"), _clock);
        var serverClock = new ServerClock(_clock);

        return new ServerConnection(
            pairing,
            buffer,
            new PresenceEventMapper(new LogTimestampConverter(Utc), serverClock),
            serverClock,
            _transport,
            _clock,
            clientVersion: "2026.9.0",
            backoff: new BackoffPolicy(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5), 2.0, () => 0.0),
            journal: journal);
    }

    private static ObservedPresence Observation(string subjectId = "usr_a", string group = "grp_cats")
    {
        Assert.True(InstanceLocation.TryParse($"wrld_w:85019~group({group})~region(use)", out var location));

        return new ObservedPresence(
            PresenceKind.Joined,
            new DateTime(2026, 9, 12, 20, 0, 0),
            subjectId,
            "somebody",
            location);
    }

    private static void Fill(ServerConnection connection, int count)
    {
        for (var i = 0; i < count; i++)
            connection.Accept(Observation($"usr_{i}"));
    }

    // --- Batching ------------------------------------------------------------------------------

    [Fact]
    public void DoesNotSendAnEmptyBuffer()
    {
        Assert.False(Connection().IsDueToSend());
    }

    [Fact]
    public void SendsOnceTheBurstFillsABatch()
    {
        // Walking into a busy instance produces forty observations at once.
        var connection = Connection();
        Fill(connection, ServerConnection.DefaultBatchSize);

        Assert.True(connection.IsDueToSend());
    }

    [Fact]
    public void ATrickleWaitsForTheTimer()
    {
        var connection = Connection();
        connection.Accept(Observation());

        Assert.False(connection.IsDueToSend());

        _clock.Advance(ServerConnection.DefaultBatchInterval);
        Assert.True(connection.IsDueToSend());
    }

    [Fact]
    public async Task ABatchNeverExceedsTheProtocolCap()
    {
        var connection = Connection();
        Fill(connection, 600);

        await connection.PumpAsync(TestContext.Current.CancellationToken);

        Assert.True(_transport.Sent[0].Events.Count <= EventBatch.MaxEvents);
    }

    [Fact]
    public async Task AcceptedEventsLeaveTheBuffer()
    {
        var connection = Connection();
        Fill(connection, 50);

        await connection.PumpAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, connection.Pending);
    }

    [Fact]
    public async Task PartialAcceptanceIsSuccess()
    {
        // Eleven of forty-eight already reported by another moderator is a completely successful
        // request. Deduplication is expected, not a failure to report.
        _transport.Then(new IngestResult(IngestOutcome.Accepted, Accepted: 37, Deduplicated: 11, Rejected: 2));
        var connection = Connection();
        Fill(connection, 50);

        await connection.PumpAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionState.Healthy, connection.State);
        Assert.Equal(0, connection.Pending);
        Assert.Equal(11, connection.DeduplicatedTotal);
    }

    [Fact]
    public async Task TheBatchCarriesTheClockOffsetAndItsConfidence()
    {
        var connection = Connection();
        connection.ServerClock.Add(new ClockSample(
            _clock.UtcNow, _clock.UtcNow - TimeSpan.FromMilliseconds(412), _clock.UtcNow));
        Fill(connection, 50);

        await connection.PumpAsync(TestContext.Current.CancellationToken);

        Assert.Equal(-412, _transport.Sent[0].ClockOffsetMs);
        Assert.Equal("poor", _transport.Sent[0].ClockConfidence);
    }

    // --- Failures ------------------------------------------------------------------------------

    [Fact]
    public async Task NothingIsLostWhenTheNetworkIsGone()
    {
        _transport.Throw = new HttpRequestException("no route to host");
        var connection = Connection();
        Fill(connection, 50);

        var result = await connection.PumpAsync(TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.NetworkFailure, result!.Outcome);
        Assert.Equal(50, connection.Pending);
    }

    [Fact]
    public async Task ATransientFailureBacksOffAndComesBack()
    {
        _transport.Then(new IngestResult(IngestOutcome.ServerTrouble));
        var connection = Connection();
        Fill(connection, 50);

        await connection.PumpAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ConnectionState.Waiting, connection.State);
        Assert.False(connection.IsDueToSend());

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(connection.IsDueToSend());

        await connection.PumpAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, connection.Pending);
        Assert.Equal(ConnectionState.Healthy, connection.State);
    }

    [Fact]
    public async Task ARetryReusesTheSameBatchIdAndTheSameEventIds()
    {
        // The server may have processed a request whose answer never arrived. Stable ids are what
        // make the repeat recognisable instead of a duplicate.
        _transport.Then(new IngestResult(IngestOutcome.ServerTrouble));
        var connection = Connection();
        Fill(connection, 50);

        await connection.PumpAsync(TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await connection.PumpAsync(TestContext.Current.CancellationToken);

        Assert.Equal(_transport.Sent[0].BatchId, _transport.Sent[1].BatchId);
        Assert.Equal(
            _transport.Sent[0].Events.Select(e => e.ClientEventId),
            _transport.Sent[1].Events.Select(e => e.ClientEventId));
    }

    [Fact]
    public async Task AMalformedBatchIsDroppedRatherThanRetriedForever()
    {
        // A retry loop on a permanent error is how a buffer fills up and reporting stops
        // altogether. Dropped, counted, and worth an alarm.
        _transport.Then(new IngestResult(IngestOutcome.Malformed, Code: "invalid_batch"));
        var connection = Connection();
        Fill(connection, 50);

        await connection.PumpAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, connection.Pending);
        Assert.Equal(1, connection.MalformedBatches);
    }

    [Fact]
    public async Task ARevokedTokenStopsThePairingVisibly()
    {
        _transport.Then(new IngestResult(IngestOutcome.Unauthorised, Code: "token_revoked"));
        var connection = Connection();
        Fill(connection, 50);

        await connection.PumpAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionState.Stopped, connection.State);
        Assert.False(connection.IsDueToSend());

        // Nothing more is sent, however long we wait.
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Null(await connection.PumpAsync(TestContext.Current.CancellationToken));
        Assert.Single(_transport.Sent);
    }

    [Fact]
    public async Task AStoppedPairingStopsObservingToo()
    {
        _transport.Then(new IngestResult(IngestOutcome.Unauthorised));
        var connection = Connection();
        Fill(connection, 50);
        await connection.PumpAsync(TestContext.Current.CancellationToken);

        var pendingBefore = connection.Pending;
        connection.Accept(Observation("usr_late"));

        Assert.Equal(pendingBefore, connection.Pending);
    }

    [Fact]
    public async Task AVersionMismatchAsksForRenegotiationRatherThanRetrying()
    {
        _transport.Then(new IngestResult(IngestOutcome.VersionUnsupported, Code: "api_version_unsupported"));
        var connection = Connection();
        Fill(connection, 50);

        await connection.PumpAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ConnectionState.NeedsRenegotiation, connection.State);
        Assert.Equal(50, connection.Pending);

        connection.Renegotiated(apiVersion: 6);
        Assert.Equal(ConnectionState.Healthy, connection.State);
        Assert.Equal(6, connection.Pairing.ApiVersion);
        Assert.EndsWith("/api/v6/client/events", connection.Pairing.EventsEndpoint.AbsolutePath);
    }

    [Fact]
    public async Task ABatchTooLargeIsHalvedAndNothingIsDropped()
    {
        _transport.Then(new IngestResult(IngestOutcome.TooLarge));
        var connection = Connection();
        Fill(connection, 50);

        await connection.PumpAsync(TestContext.Current.CancellationToken);
        Assert.Equal(50, connection.Pending);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await connection.PumpAsync(TestContext.Current.CancellationToken);

        Assert.Equal(25, _transport.Sent[1].Events.Count);
    }

    [Fact]
    public async Task RetryAfterIsHonoured()
    {
        // Modbot sends Retry-After even though VRChat does not, so there is nothing to guess.
        _transport.Then(new IngestResult(IngestOutcome.RateLimited, RetryAfter: TimeSpan.FromMinutes(10)));
        var connection = Connection();
        Fill(connection, 50);

        await connection.PumpAsync(TestContext.Current.CancellationToken);

        _clock.Advance(TimeSpan.FromMinutes(9));
        Assert.False(connection.IsDueToSend());

        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(connection.IsDueToSend());
    }

    [Fact]
    public async Task BackoffGrowsWithConsecutiveFailuresAndThenResets()
    {
        _transport.Then(new IngestResult(IngestOutcome.ServerTrouble))
                  .Then(new IngestResult(IngestOutcome.ServerTrouble));
        var connection = Connection();
        Fill(connection, 50);

        // First failure: a one-second wait with jitter pinned to its minimum.
        await connection.PumpAsync(TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False(connection.IsDueToSend());

        _clock.Advance(TimeSpan.FromMilliseconds(600));
        Assert.True(connection.IsDueToSend());

        await connection.PumpAsync(TestContext.Current.CancellationToken);

        // Second failure in a row waits twice as long: what would have been enough a moment ago no
        // longer is.
        _clock.Advance(TimeSpan.FromMilliseconds(1100));
        Assert.False(connection.IsDueToSend());

        _clock.Advance(TimeSpan.FromMinutes(5));
        await connection.PumpAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ConnectionState.Healthy, connection.State);
    }

    // --- Pause ---------------------------------------------------------------------------------

    [Fact]
    public void PausingStopsObservationBeingCapturedAtAll()
    {
        // Pausing means Modbot stops reporting what you do. It does not mean saving it up to report
        // when you unpause.
        var connection = Connection();
        connection.IsPaused = true;

        Fill(connection, 50);

        Assert.Equal(0, connection.Pending);
        Assert.Equal(ConnectionState.Paused, connection.State);
    }

    [Fact]
    public async Task PausingStopsTransmissionImmediately()
    {
        var connection = Connection();
        Fill(connection, 50);

        connection.IsPaused = true;

        Assert.Null(await connection.PumpAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_transport.Sent);
    }

    [Fact]
    public async Task WhatWasBufferedBeforeThePauseIsStillSentAfterwards()
    {
        var connection = Connection();
        Fill(connection, 50);
        connection.IsPaused = true;

        connection.IsPaused = false;
        await connection.PumpAsync(TestContext.Current.CancellationToken);

        Assert.Equal(50, _transport.Sent[0].Events.Count);
    }

    // --- Transport rules ------------------------------------------------------------------------

    [Fact]
    public void PlainHttpIsRefusedRatherThanWarnedAbout()
    {
        Assert.Throws<ArgumentException>(() =>
            new ServerPairing("cats", new Uri("http://modbot.example"), "token", "grp_cats"));
    }

    [Fact]
    public void TheEndpointsCarryTheNegotiatedVersion()
    {
        var pairing = new ServerPairing("cats", new Uri("https://modbot.example"), "t", "grp_cats", apiVersion: 4);

        Assert.Equal("/api/v4/client/events", pairing.EventsEndpoint.AbsolutePath);
        Assert.Equal("/api/v4/client/time", pairing.TimeEndpoint.AbsolutePath);
    }

    [Fact]
    public void TheTokenIsNotInTheServersOwnDescription()
    {
        // ToString ends up in log files.
        var pairing = new ServerPairing("cats", new Uri("https://modbot.example"), "SECRET-TOKEN", "grp_cats");

        Assert.DoesNotContain("SECRET-TOKEN", pairing.ToString());
    }

    [Fact]
    public async Task OneEventGoingToTheServerAndToModbotCloudIsOneRowOnTheEventsScreen()
    {
        // The two halves of the client never speak to each other: one reports to the paired
        // server, the other backs up to Modbot Cloud, and each gives the event its own
        // clientEventId. What ties their lines together is the key both work out from the
        // observation, so an event seen once is counted once.
        var journal = new SentJournal(Path.Combine(_directory, "sent.jsonl"), _clock);
        var connection = Connection(journal: journal);
        var observation = Observation();

        connection.Accept(observation);

        var cloudClock = new ServerClock(_clock);
        journal.RecordQueued(
            SentJournal.CloudName,
            JournalDestination.Cloud,
            SentJournal.KeyFor(observation),
            new PresenceEventMapper(new LogTimestampConverter(Utc), cloudClock).MapAnyInstance(observation));

        _clock.Advance(ServerConnection.DefaultBatchInterval);
        await connection.PumpAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(journal.Events());

        Assert.Equal("cats", row.ServerId);
        Assert.Equal(JournalEntryKind.Sent, row.ServerState);
        Assert.Equal(JournalEntryKind.Waiting, row.CloudState);
    }
}
