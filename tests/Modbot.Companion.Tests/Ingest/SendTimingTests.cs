using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Time;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Ingest;

/// <summary>
/// When a connection sends: within two seconds of somebody arriving or leaving, grouped, and
/// never sooner than the server or the backoff allows.
/// </summary>
public sealed class SendTimingTests : IDisposable
{
    private sealed class CountingTransport : IIngestTransport
    {
        public List<EventBatch> Sent { get; } = [];

        public IngestOutcome Answer { get; set; } = IngestOutcome.Accepted;

        public Task<IngestResult> SendAsync(ServerPairing pairing, EventBatch batch, CancellationToken ct)
        {
            Sent.Add(batch);
            return Task.FromResult(new IngestResult(Answer, Answer == IngestOutcome.Accepted ? batch.Events.Count : 0));
        }
    }

    private static readonly TimeZoneInfo Utc =
        TimeZoneInfo.CreateCustomTimeZone("Test/Utc", TimeSpan.Zero, "UTC", "UTC");

    private readonly string _directory = Directory.CreateTempSubdirectory("modbot-timing-").FullName;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 12, 20, 0, 0, TimeSpan.Zero));
    private readonly CountingTransport _transport = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string BufferPath => Path.Combine(_directory, "cats.jsonl");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ServerConnection Connection()
    {
        var pairing = new ServerPairing("cats", new Uri("https://modbot.example"), "token", "grp_cats");
        var serverClock = new ServerClock(_clock);

        return new ServerConnection(
            pairing,
            new FileEventBuffer(BufferPath, _clock),
            new PresenceEventMapper(new LogTimestampConverter(Utc), serverClock),
            serverClock,
            _transport,
            _clock,
            clientVersion: "2026.9.0",
            backoff: new BackoffPolicy(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), 2.0, () => 0.0));
    }

    private static ObservedPresence Seen(PresenceKind kind, string subjectId = "usr_a")
    {
        Assert.True(InstanceLocation.TryParse("wrld_w:85019~group(grp_cats)~region(use)", out var location));

        return new ObservedPresence(
            kind,
            new DateTime(2026, 9, 12, 20, 0, 0),
            subjectId,
            "somebody",
            location,
            kind == PresenceKind.AvatarChanged ? "Pengus" : null);
    }

    [Theory]
    [InlineData(PresenceKind.Joined)]
    [InlineData(PresenceKind.Left)]
    [InlineData(PresenceKind.PresenceObserved)]
    [InlineData(PresenceKind.LogStopped)]
    public void SomebodyArrivingOrLeaving_IsSentWithinTwoSeconds(PresenceKind kind)
    {
        var connection = Connection();
        connection.Accept(Seen(kind));

        Assert.False(connection.IsDueToSend());

        _clock.Advance(ServerConnection.DefaultChangeDelay);
        Assert.True(connection.IsDueToSend());
    }

    [Fact]
    public async Task ChangesThatLandTogether_GoInOneRequest()
    {
        var connection = Connection();

        connection.Accept(Seen(PresenceKind.Joined, "usr_a"));
        _clock.Advance(TimeSpan.FromMilliseconds(700));
        connection.Accept(Seen(PresenceKind.Joined, "usr_b"));
        _clock.Advance(TimeSpan.FromMilliseconds(700));
        connection.Accept(Seen(PresenceKind.Left, "usr_c"));

        Assert.Null(await connection.PumpAsync(Ct));

        _clock.Advance(TimeSpan.FromMilliseconds(600));
        Assert.NotNull(await connection.PumpAsync(Ct));

        Assert.Equal(3, Assert.Single(_transport.Sent).Events.Count);
    }

    [Fact]
    public void AStreamOfArrivalsCannotKeepPushingTheSendBack()
    {
        var connection = Connection();

        for (var i = 0; i < 4; i++)
        {
            connection.Accept(Seen(PresenceKind.Joined, $"usr_{i}"));
            _clock.Advance(TimeSpan.FromMilliseconds(500));
        }

        Assert.True(connection.IsDueToSend());
    }

    [Fact]
    public void AnAvatarChange_WaitsForTheUsualBatch()
    {
        var connection = Connection();
        connection.Accept(Seen(PresenceKind.AvatarChanged));

        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(connection.IsDueToSend());

        _clock.Advance(ServerConnection.DefaultBatchInterval);
        Assert.True(connection.IsDueToSend());
    }

    [Fact]
    public async Task AQuietRoomSendsNothingExtra()
    {
        var connection = Connection();
        connection.Accept(Seen(PresenceKind.Joined));

        _clock.Advance(ServerConnection.DefaultChangeDelay);
        Assert.NotNull(await connection.PumpAsync(Ct));

        for (var second = 0; second < 120; second++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Null(await connection.PumpAsync(Ct));
        }

        Assert.Single(_transport.Sent);
    }

    [Fact]
    public async Task ABackoffStillWins_HoweverLivelyTheRoom()
    {
        var connection = Connection();
        _transport.Answer = IngestOutcome.ServerTrouble;

        connection.Accept(Seen(PresenceKind.Joined, "usr_a"));
        _clock.Advance(ServerConnection.DefaultChangeDelay);
        Assert.NotNull(await connection.PumpAsync(Ct));

        connection.Accept(Seen(PresenceKind.Joined, "usr_b"));
        _clock.Advance(ServerConnection.DefaultChangeDelay);

        // The policy above waits ten seconds after one failure. Arrivals do not jump the queue.
        Assert.False(connection.IsDueToSend());

        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(connection.IsDueToSend());
    }

    [Fact]
    public async Task TheRestOfABurstBiggerThanABatch_FollowsWithinSeconds()
    {
        var connection = Connection();

        for (var i = 0; i < ServerConnection.DefaultBatchSize + 10; i++)
            connection.Accept(Seen(PresenceKind.PresenceObserved, $"usr_{i}"));

        Assert.NotNull(await connection.PumpAsync(Ct));
        Assert.Equal(10, connection.Pending);
        Assert.False(connection.IsDueToSend());

        _clock.Advance(ServerConnection.DefaultChangeDelay);
        Assert.True(connection.IsDueToSend());
    }

    [Fact]
    public void ChangesLeftInTheBufferFromTheLastRun_GoAtTheNextChance()
    {
        Connection().Accept(Seen(PresenceKind.Left));

        var restarted = Connection();
        Assert.Equal(1, restarted.Pending);

        _clock.Advance(ServerConnection.DefaultChangeDelay);
        Assert.True(restarted.IsDueToSend());
    }

    [Fact]
    public void AStoppedLogGoesOnTheWireUnderItsOwnName()
    {
        var connection = Connection();
        connection.Accept(Seen(PresenceKind.LogStopped, "usr_mod"));

        var line = File.ReadAllLines(BufferPath).Single();

        Assert.Contains("\"LogStopped\"", line, StringComparison.Ordinal);
        Assert.Contains("usr_mod", line, StringComparison.Ordinal);
    }
}
