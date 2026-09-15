using System.Text;
using Modbot.Client.CloudBackup;
using Modbot.Client.Ingest;
using Modbot.Client.Instances;
using Modbot.Client.LogReading;
using Modbot.Client.Pipeline;
using Modbot.Client.Time;
using Modbot.TestSupport;

namespace Modbot.Client.Tests.CloudBackup;

public sealed class CloudEventBackupTests : IDisposable
{
    private static readonly TimeZoneInfo PlusTwo =
        TimeZoneInfo.CreateCustomTimeZone("Test/PlusTwo", TimeSpan.FromHours(2), "Plus two", "Plus two");

    private readonly string _directory = Directory.CreateTempSubdirectory("modbot-cloud-backup-").FullName;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeCloudClient _cloud = new();
    private readonly MemoryInstallStore _installs = new();
    private readonly CountingIds _ids = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var sent in _cloud.Sent)
            sent.Body.Dispose();

        Directory.Delete(_directory, recursive: true);
    }

    private string Outbox => Path.Combine(_directory, "cloud");

    private CloudEventBackup Backup(bool enabled = true, Uri? endpoint = null) => new(new CloudBackupOptions(
        Outbox,
        _clock,
        _cloud,
        _installs,
        "2026.9.0",
        Endpoint: endpoint,
        Enabled: enabled,
        TimeZone: PlusTwo,
        Backoff: new BackoffPolicy(jitter: () => 1.0),
        Ids: _ids));

    /// <summary>Queues observations and waits out a batch, so the next pump has one closed batch to send.</summary>
    private async Task QueueAndCloseAsync(CloudEventBackup backup, params ObservedPresence[] observations)
    {
        backup.Offer(observations);
        await backup.PumpAsync(Ct);
        _clock.Advance(CloudOutbox.MaxBatchAge);
    }

    private int BatchFiles() => Directory.Exists(Outbox) ? Directory.GetFiles(Outbox, "batch-*.json.gz").Length : 0;

    [Fact]
    public async Task AnUnpairedClientSendsItsEventsToTheDefaultCloud()
    {
        var backup = Backup();

        await QueueAndCloseAsync(backup, Observations.Joined("usr_1"), Observations.Joined("usr_2", second: 5));
        Assert.True(await backup.PumpAsync(Ct));

        Assert.Equal([new Uri("https://cloud.modbot.co")], _cloud.Registered);
        var (install, body) = Assert.Single(_cloud.Sent);
        Assert.Equal(new Uri("https://cloud.modbot.co"), install.Endpoint);
        Assert.Equal("the-secret", _installs.Find(install.Endpoint)!.Secret);

        var root = body.RootElement;
        Assert.Equal("2026.9.0", root.GetProperty("clientVersion").GetString());
        Assert.Equal(_clock.UtcNow, root.GetProperty("sentAt").GetDateTimeOffset());
        Assert.False(root.TryGetProperty("lines", out _));

        // Nothing in a batch ties it to a paired Modbot server.
        Assert.False(root.TryGetProperty("modbotServerId", out _));

        // The client protocol's event, exactly: id, type, time corrected from local, subject, place.
        var sent = root.GetProperty("events")[1];
        Assert.Equal("event-2", sent.GetProperty("clientEventId").GetString());
        Assert.Equal("InstanceJoined", sent.GetProperty("type").GetString());
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 8, 0, 5, TimeSpan.Zero), sent.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal("usr_2", sent.GetProperty("subjectId").GetString());
        Assert.Equal("wrld_1", sent.GetProperty("worldId").GetString());
        Assert.Equal("39911", sent.GetProperty("instanceId").GetString());
        Assert.Equal("grp_cats", sent.GetProperty("groupId").GetString());
        Assert.Equal("Rin", sent.GetProperty("data").GetProperty("displayName").GetString());

        Assert.Equal(0, BatchFiles());
        Assert.Equal(0, backup.Status.Queued);
    }

    [Fact]
    public async Task EveryInstanceIsBackedUpButNoInstanceSecretLeaves()
    {
        var backup = Backup();

        await QueueAndCloseAsync(backup, Observations.Joined("usr_1", Observations.PrivateLocation));
        await backup.PumpAsync(Ct);

        var body = Assert.Single(_cloud.Sent).Body;
        var sent = body.RootElement.GetProperty("events")[0];

        Assert.Equal("wrld_2", sent.GetProperty("worldId").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, sent.GetProperty("groupId").ValueKind);
        Assert.DoesNotContain("secret-nonce-value", body.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("nonce", body.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANamedCloudIsUsedInsteadOfTheDefault()
    {
        var backup = Backup(endpoint: new Uri("https://cloud.group.example"));

        await QueueAndCloseAsync(backup, Observations.Joined());
        Assert.True(await backup.PumpAsync(Ct));

        Assert.Equal([new Uri("https://cloud.group.example")], _cloud.Registered);
        Assert.Equal(new Uri("https://cloud.group.example"), Assert.Single(_cloud.Sent).Install.Endpoint);
    }

    [Fact]
    public async Task AClientStartedWithTheBackupOffSendsNothingAndDropsWhatWasQueued()
    {
        // Queued by an earlier run that had the backup on, and never sent.
        _cloud.Registration = new CloudRegistration(IngestOutcome.NetworkFailure);
        var earlier = Backup();
        await QueueAndCloseAsync(earlier, Observations.Joined("usr_1"));
        await earlier.PumpAsync(Ct);
        earlier.Offer([Observations.Joined("usr_2")]);
        Assert.True(earlier.Status.Queued > 0);

        _cloud.Registration = new CloudRegistration(IngestOutcome.Accepted, Guid.NewGuid(), "the-secret");
        _cloud.Registered.Clear();

        var backup = Backup(enabled: false);

        Assert.False(backup.Enabled);
        Assert.Equal(CloudBackupState.Off, backup.Status.State);
        Assert.Equal(0, backup.Status.Queued);
        Assert.Equal(0, BatchFiles());

        backup.Offer([Observations.Joined("usr_3")]);
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.False(await backup.PumpAsync(Ct));
        Assert.Equal(0, backup.Status.Queued);
        Assert.Empty(_cloud.Sent);
        Assert.Empty(_cloud.Registered);
    }

    [Fact]
    public async Task StartingAgainWithTheBackupOnSendsOnlyFromThatMoment()
    {
        var off = Backup(enabled: false);
        off.Offer([Observations.Joined("usr_while_off")]);
        await off.PumpAsync(Ct);

        var on = Backup();
        await QueueAndCloseAsync(on, Observations.Joined("usr_after"));
        await on.PumpAsync(Ct);

        var sent = Assert.Single(Assert.Single(_cloud.Sent).Body.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal("usr_after", sent.GetProperty("subjectId").GetString());
    }

    [Fact]
    public async Task TheOutboxSurvivesARestartWithTheSameEventIds()
    {
        // The first run cannot reach Cloud, so its batches stay on disk.
        _cloud.Registration = new CloudRegistration(IngestOutcome.NetworkFailure);
        var first = Backup();
        await QueueAndCloseAsync(first, Observations.Joined("usr_1"), Observations.Joined("usr_2"), Observations.Joined("usr_3"));
        await first.PumpAsync(Ct);
        Assert.Equal(1, BatchFiles());

        // An event left in the open batch when the client closed is kept too.
        first.Offer([Observations.Joined("usr_4")]);
        await first.PumpAsync(Ct);
        Assert.Empty(_cloud.Sent);

        _cloud.Registration = new CloudRegistration(IngestOutcome.Accepted, Guid.Parse("11111111-2222-3333-4444-555555555555"), "the-secret");
        var second = Backup();
        Assert.Equal(4, second.Status.Queued);

        Assert.True(await second.PumpAsync(Ct));
        Assert.True(await second.PumpAsync(Ct));

        Assert.Equal(4, _cloud.EventsSent);
        Assert.Equal(
            ["event-1", "event-2", "event-3", "event-4"],
            _cloud.Sent.SelectMany(s => s.Body.RootElement.GetProperty("events").EnumerateArray())
                .Select(e => e.GetProperty("clientEventId").GetString()));
        Assert.Equal(0, BatchFiles());
    }

    [Fact]
    public async Task TroubleBacksOffAndRetryAfterIsHonoured()
    {
        var backup = Backup();
        await QueueAndCloseAsync(backup, Observations.Joined());

        _cloud.Answers.Enqueue(new IngestResult(IngestOutcome.ServerTrouble));
        Assert.False(await backup.PumpAsync(Ct));
        Assert.Equal(CloudBackupState.Retrying, backup.Status.State);

        // Two seconds on the first failure, with the jitter pinned at its top.
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await backup.PumpAsync(Ct));
        Assert.Single(_cloud.Sent);

        _cloud.Answers.Enqueue(new IngestResult(IngestOutcome.RateLimited, RetryAfter: TimeSpan.FromSeconds(90)));
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await backup.PumpAsync(Ct));
        Assert.Equal(2, _cloud.Sent.Count);

        _clock.Advance(TimeSpan.FromSeconds(89));
        Assert.False(await backup.PumpAsync(Ct));
        Assert.Equal(2, _cloud.Sent.Count);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await backup.PumpAsync(Ct));
        Assert.Equal(CloudBackupState.Sending, backup.Status.State);
        Assert.Equal(0, BatchFiles());

        // Every attempt carried the same events, so Cloud can recognise the retries.
        Assert.All(_cloud.Sent, s => Assert.Equal("event-1", s.Body.RootElement.GetProperty("events")[0].GetProperty("clientEventId").GetString()));
    }

    [Fact]
    public async Task AForgottenInstallRegistersAgain()
    {
        var backup = Backup();
        await QueueAndCloseAsync(backup, Observations.Joined());

        _cloud.Answers.Enqueue(new IngestResult(IngestOutcome.Unauthorised));
        Assert.False(await backup.PumpAsync(Ct));
        Assert.Empty(_installs.Installs);

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(await backup.PumpAsync(Ct));
        Assert.Equal(2, _cloud.Registered.Count);
    }

    [Fact]
    public async Task ABatchCloudRefusesForGoodIsDroppedAndCounted()
    {
        var backup = Backup();
        await QueueAndCloseAsync(backup, Observations.Joined("usr_1"), Observations.Joined("usr_2"));

        _cloud.Answers.Enqueue(new IngestResult(IngestOutcome.Malformed));
        await backup.PumpAsync(Ct);

        Assert.Equal(0, BatchFiles());
        Assert.Equal(2, backup.Status.Dropped);
    }

    [Fact]
    public async Task TheLogReaderNeverWaitsOnTheBackup()
    {
        var logs = Directory.CreateDirectory(Path.Combine(_directory, "logs")).FullName;
        var logFile = Path.Combine(logs, "output_log_2026-09-15_10-00-00.txt");
        File.WriteAllText(logFile, "");

        var backup = Backup();
        var engine = new ClientEngine(new PresenceObserver(new VRChatLogTail(logs), _clock), _clock, backup: backup);

        // The first pass primes the reader; everything after it is live.
        await engine.TickAsync(Ct);

        // A private, non-group instance: no Modbot server is told, and the backup still is.
        File.AppendAllText(
            logFile,
            "2026.09.15 10:00:00 Debug      -  [Behaviour] Joining wrld_2:77777~private(usr_owner)~nonce(n)~region(eu)\n"
            + "2026.09.15 10:00:01 Debug      -  [Behaviour] Initialized PlayerAPI \"Rin\" is local\n"
            + "2026.09.15 10:00:01 Debug      -  [Behaviour] OnPlayerJoined Rin (usr_me)\n",
            Encoding.UTF8);

        for (var i = 0; i < 5; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(5));
            await engine.TickAsync(Ct);
        }

        _clock.Advance(CloudOutbox.MaxBatchAge);

        // Cloud hangs on the first batch, and stays hung.
        _cloud.Hang = new TaskCompletionSource();
        var pump = backup.PumpAsync(Ct);
        await _cloud.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Meanwhile the reader goes on reading, and each turn finishes.
        for (var i = 0; i < 20; i++)
        {
            File.AppendAllText(logFile, $"2026.09.15 10:01:{i:00} Debug      -  [IK Debug Log] fps {i}\n", Encoding.UTF8);
            var tick = engine.TickAsync(Ct);
            Assert.True(tick.IsCompleted, "A reader turn waited on the backup.");
            await tick;
        }

        Assert.False(pump.IsCompleted);

        _cloud.Hang.SetResult();
        Assert.True(await pump.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.True(_cloud.EventsSent > 0);
        Assert.Equal("usr_me", _cloud.Sent[0].Body.RootElement.GetProperty("events")[0].GetProperty("subjectId").GetString());
    }

    [Fact]
    public void TheQueueInMemoryIsBounded()
    {
        var backup = Backup();

        backup.Offer([.. Enumerable.Range(0, CloudEventBackup.QueueLimit + 5).Select(i => Observations.Joined($"usr_{i}"))]);

        Assert.Equal(CloudEventBackup.QueueLimit, backup.Status.Queued);
        Assert.Equal(5, backup.Status.Dropped);
    }

    [Fact]
    public void TheOutboxDropsTheOldestBatchesPastItsCap()
    {
        var outbox = new CloudOutbox(Outbox, cap: CloudOutbox.MaxBytesPerBatch);

        // Random text compresses poorly, so a few full batches pass a small cap.
        var random = new Random(7);
        for (var batch = 0; batch < 6; batch++)
        {
            var events = Enumerable.Range(0, CloudOutbox.MaxEventsPerBatch)
                .Select(_ => "\"" + new string([.. Enumerable.Range(0, 600).Select(_ => (char)random.Next(65, 90))]) + "\"")
                .ToList();
            outbox.Append(events, _clock.UtcNow);
        }

        Assert.True(outbox.DroppedEvents > 0);
        Assert.True(outbox.Batches()[0].Sequence > 1);
    }
}
