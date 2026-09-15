using Modbot.Client.Ingest;
using Modbot.Client.LogReading;
using Modbot.Client.Pipeline;
using Modbot.Client.Tests.LogReading;
using Modbot.Client.Time;
using Modbot.TestSupport;

namespace Modbot.Client.Tests.Pipeline;

/// <summary>
/// One log, several servers: the multi-server shape end to end, against the real fixture.
/// </summary>
public sealed class ClientEngineTests : IDisposable
{
    private sealed class CapturingTransport : IIngestTransport
    {
        public Dictionary<string, List<ClientEvent>> ByServer { get; } = new(StringComparer.Ordinal);

        public Task<IngestResult> SendAsync(ServerPairing pairing, EventBatch batch, CancellationToken ct)
        {
            if (!ByServer.TryGetValue(pairing.ServerId, out var events))
                ByServer[pairing.ServerId] = events = [];

            events.AddRange(batch.Events);
            return Task.FromResult(new IngestResult(IngestOutcome.Accepted, batch.Events.Count));
        }
    }

    private sealed class FixedTimeProbe(TimeSpan offset) : IServerTimeProbe
    {
        public int Calls { get; private set; }

        public Task<ServerTimeAnswer?> MeasureAsync(ServerPairing pairing, CancellationToken ct)
        {
            Calls++;
            var now = DateTimeOffset.UnixEpoch;
            return Task.FromResult<ServerTimeAnswer?>(new ServerTimeAnswer(new ClockSample(now, now + offset, now), ServerCloudAnswer.NoPreference));
        }
    }

    private static readonly TimeZoneInfo Utc =
        TimeZoneInfo.CreateCustomTimeZone("Test/Utc", TimeSpan.Zero, "UTC", "UTC");

    private readonly string _directory = Directory.CreateTempSubdirectory("modbot-engine-").FullName;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 12, 20, 0, 0, TimeSpan.Zero));
    private readonly CapturingTransport _transport = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string LogDirectory => Path.Combine(_directory, "logs");

    private PresenceObserver Observer()
    {
        Directory.CreateDirectory(LogDirectory);
        return new PresenceObserver(new VRChatLogTail(LogDirectory), _clock);
    }

    private ServerConnection Connection(string serverId, string managedGroupId)
    {
        var pairing = new ServerPairing(
            serverId, new Uri($"https://{serverId}.example"), $"token-{serverId}", managedGroupId);
        var serverClock = new ServerClock(_clock);

        return new ServerConnection(
            pairing,
            new FileEventBuffer(Path.Combine(_directory, $"{serverId}.jsonl"), _clock),
            new PresenceEventMapper(new LogTimestampConverter(Utc), serverClock),
            serverClock,
            _transport,
            _clock,
            clientVersion: "2026.9.0");
    }

    private void CopyFixture()
        => File.Copy(LogFixture.Path, Path.Combine(LogDirectory, "output_log_2026-09-03_20-26-45.txt"));

    private void AppendLive(params string[] lines)
        => File.AppendAllLines(Path.Combine(LogDirectory, "output_log_2026-09-03_20-26-45.txt"), lines);

    private async Task DrainAsync(ClientEngine engine)
    {
        await engine.TickAsync(TestContext.Current.CancellationToken);
        _clock.Advance(ServerConnection.DefaultBatchInterval);
        await engine.TickAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OneLogFeedsEveryServerWithoutBeingReadTwice()
    {
        var observer = Observer();
        CopyFixture();

        var cats = Connection("cats", LogFixture.GroupId);
        var dogs = Connection("dogs", "grp_dogs");
        var engine = new ClientEngine(observer, _clock, [cats, dogs]);

        // Prime on the history, then produce something live in the group instance.
        await engine.TickAsync(TestContext.Current.CancellationToken);
        AppendLive(
            "2026.09.03 21:00:00 Debug      -  [Behaviour] Joining " + LogFixture.GroupLocation,
            "2026.09.03 21:00:01 Debug      -  [Behaviour] OnPlayerJoined roster (usr_roster)",
            "2026.09.03 21:00:02 Debug      -  [Behaviour] OnPlayerJoined bin¹ (" + LogFixture.LocalUserId + ")",
            "2026.09.03 21:00:30 Debug      -  [Behaviour] OnPlayerJoined newcomer (usr_newcomer)");

        await DrainAsync(engine);

        Assert.Equal(3, _transport.ByServer["cats"].Count);
        Assert.False(_transport.ByServer.ContainsKey("dogs"));
    }

    [Fact]
    public async Task AModeratorsOwnPrivateWorldsAreNeverSentToAnybody()
    {
        var observer = Observer();
        CopyFixture();

        var cats = Connection("cats", LogFixture.GroupId);
        var engine = new ClientEngine(observer, _clock, [cats]);
        await engine.TickAsync(TestContext.Current.CancellationToken);

        AppendLive(
            "2026.09.03 21:00:00 Debug      -  [Behaviour] Joining wrld_private:1~private(usr_friend)~nonce(SECRET)",
            "2026.09.03 21:00:01 Debug      -  [Behaviour] OnPlayerJoined bin¹ (" + LogFixture.LocalUserId + ")",
            "2026.09.03 21:00:30 Debug      -  [Behaviour] OnPlayerJoined someone (usr_someone)");

        var tick = await engine.TickAsync(TestContext.Current.CancellationToken);
        await DrainAsync(engine);

        Assert.Equal(0, tick.Routed);
        Assert.Equal(tick.Observed, tick.Dropped);
        Assert.False(_transport.ByServer.ContainsKey("cats"));
    }

    [Fact]
    public async Task TwoGroupsOnOneMachineStaySeparate()
    {
        var observer = Observer();
        CopyFixture();

        var cats = Connection("cats", "grp_cats");
        var dogs = Connection("dogs", "grp_dogs");
        var engine = new ClientEngine(observer, _clock, [cats, dogs]);
        await engine.TickAsync(TestContext.Current.CancellationToken);

        AppendLive(
            "2026.09.03 21:00:00 Debug      -  [Behaviour] Joining wrld_a:1~group(grp_cats)~region(use)",
            "2026.09.03 21:00:01 Debug      -  [Behaviour] OnPlayerJoined bin¹ (" + LogFixture.LocalUserId + ")",
            "2026.09.03 21:00:05 Debug      -  [Behaviour] OnPlayerJoined cat person (usr_cat)",
            "2026.09.03 21:00:10 Debug      -  [Behaviour] OnLeftRoom",
            "2026.09.03 21:00:20 Debug      -  [Behaviour] Joining wrld_b:2~group(grp_dogs)~region(use)",
            "2026.09.03 21:00:21 Debug      -  [Behaviour] OnPlayerJoined bin¹ (" + LogFixture.LocalUserId + ")",
            "2026.09.03 21:00:25 Debug      -  [Behaviour] OnPlayerJoined dog person (usr_dog)");

        await DrainAsync(engine);

        Assert.All(_transport.ByServer["cats"], e => Assert.Equal("grp_cats", e.GroupId));
        Assert.All(_transport.ByServer["dogs"], e => Assert.Equal("grp_dogs", e.GroupId));
        Assert.DoesNotContain(_transport.ByServer["cats"], e => e.SubjectId == "usr_dog");
        Assert.DoesNotContain(_transport.ByServer["dogs"], e => e.SubjectId == "usr_cat");
    }

    [Fact]
    public async Task PausingOneServerDoesNotPauseTheOther()
    {
        var observer = Observer();
        CopyFixture();

        var cats = Connection("cats", "grp_shared");
        var dogs = Connection("dogs", "grp_shared");
        var engine = new ClientEngine(observer, _clock, [cats, dogs]);
        await engine.TickAsync(TestContext.Current.CancellationToken);

        cats.IsPaused = true;

        AppendLive(
            "2026.09.03 21:00:00 Debug      -  [Behaviour] Joining wrld_a:1~group(grp_shared)",
            "2026.09.03 21:00:01 Debug      -  [Behaviour] OnPlayerJoined bin¹ (" + LogFixture.LocalUserId + ")",
            "2026.09.03 21:00:05 Debug      -  [Behaviour] OnPlayerJoined somebody (usr_somebody)");

        await DrainAsync(engine);

        Assert.False(_transport.ByServer.ContainsKey("cats"));
        Assert.NotEmpty(_transport.ByServer["dogs"]);
    }

    [Fact]
    public async Task EachServerGetsItsOwnIdempotencyKeysForTheSameEvent()
    {
        // Two servers, one observation. Different pairings, different idempotency scopes.
        var observer = Observer();
        CopyFixture();

        var cats = Connection("cats", "grp_shared");
        var dogs = Connection("dogs", "grp_shared");
        var engine = new ClientEngine(observer, _clock, [cats, dogs]);
        await engine.TickAsync(TestContext.Current.CancellationToken);

        AppendLive(
            "2026.09.03 21:00:00 Debug      -  [Behaviour] Joining wrld_a:1~group(grp_shared)",
            "2026.09.03 21:00:01 Debug      -  [Behaviour] OnPlayerJoined bin¹ (" + LogFixture.LocalUserId + ")",
            "2026.09.03 21:00:05 Debug      -  [Behaviour] OnPlayerJoined somebody (usr_somebody)");

        await DrainAsync(engine);

        var catIds = _transport.ByServer["cats"].Select(e => e.ClientEventId);
        var dogIds = _transport.ByServer["dogs"].Select(e => e.ClientEventId);

        Assert.Empty(catIds.Intersect(dogIds, StringComparer.Ordinal));
    }

    [Fact]
    public async Task EachServerAppliesItsOwnClockOffset()
    {
        var observer = Observer();
        CopyFixture();

        var cats = Connection("cats", "grp_shared");
        var dogs = Connection("dogs", "grp_shared");
        cats.ServerClock.Add(new ClockSample(_clock.UtcNow, _clock.UtcNow + TimeSpan.FromMinutes(1), _clock.UtcNow));

        var engine = new ClientEngine(observer, _clock, [cats, dogs]);
        await engine.TickAsync(TestContext.Current.CancellationToken);

        AppendLive(
            "2026.09.03 21:00:00 Debug      -  [Behaviour] Joining wrld_a:1~group(grp_shared)",
            "2026.09.03 21:00:01 Debug      -  [Behaviour] OnPlayerJoined bin¹ (" + LogFixture.LocalUserId + ")",
            "2026.09.03 21:00:05 Debug      -  [Behaviour] OnPlayerJoined somebody (usr_somebody)");

        await DrainAsync(engine);

        var catTime = _transport.ByServer["cats"].First(e => e.SubjectId == "usr_somebody").OccurredAt;
        var dogTime = _transport.ByServer["dogs"].First(e => e.SubjectId == "usr_somebody").OccurredAt;

        Assert.Equal(TimeSpan.FromMinutes(1), catTime - dogTime);
    }

    [Fact]
    public async Task TheClockIsResyncedPeriodicallyRatherThanPerBatch()
    {
        var observer = Observer();
        CopyFixture();

        var probe = new FixedTimeProbe(TimeSpan.FromSeconds(5));
        var cats = Connection("cats", LogFixture.GroupId);
        var engine = new ClientEngine(observer, _clock, [cats], probe);

        await engine.TickAsync(TestContext.Current.CancellationToken);
        await engine.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, probe.Calls);

        _clock.Advance(ClientEngine.ClockCheckInterval + TimeSpan.FromMinutes(1));
        await engine.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, probe.Calls);
        Assert.Equal(TimeSpan.FromSeconds(5), cats.ServerClock.Offset);
    }

    [Fact]
    public async Task AQuietTickDoesNothing()
    {
        var observer = Observer();
        CopyFixture();
        var engine = new ClientEngine(observer, _clock, [Connection("cats", LogFixture.GroupId)]);
        await engine.TickAsync(TestContext.Current.CancellationToken);

        var tick = await engine.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new EngineTick(0, 0, 0, 0), tick);
    }

    [Fact]
    public async Task UnpairingLeavesNothingOfThatGroupsDataOnDisk()
    {
        var observer = Observer();
        CopyFixture();

        var buffer = new FileEventBuffer(Path.Combine(_directory, "cats.jsonl"), _clock);
        var serverClock = new ServerClock(_clock);
        var cats = new ServerConnection(
            new ServerPairing("cats", new Uri("https://cats.example"), "t", "grp_cats"),
            buffer,
            new PresenceEventMapper(new LogTimestampConverter(Utc), serverClock),
            serverClock,
            _transport,
            _clock,
            "2026.9.0");

        var engine = new ClientEngine(observer, _clock, [cats]);
        await engine.TickAsync(TestContext.Current.CancellationToken);

        AppendLive(
            "2026.09.03 21:00:00 Debug      -  [Behaviour] Joining wrld_a:1~group(grp_cats)",
            "2026.09.03 21:00:01 Debug      -  [Behaviour] OnPlayerJoined bin¹ (" + LogFixture.LocalUserId + ")",
            "2026.09.03 21:00:05 Debug      -  [Behaviour] OnPlayerJoined somebody (usr_somebody)");
        await engine.TickAsync(TestContext.Current.CancellationToken);
        Assert.True(cats.Pending > 0);

        engine.Remove(cats, buffer);

        Assert.Equal(0, buffer.Count);
        Assert.Empty(File.ReadAllText(Path.Combine(_directory, "cats.jsonl")).Trim());
    }
}
