using System.Text;
using Modbot.Client.CloudBackup;
using Modbot.Client.Ingest;
using Modbot.Client.LogReading;
using Modbot.Client.Pipeline;
using Modbot.Client.Presentation;
using Modbot.Client.Time;
using Modbot.TestSupport;

namespace Modbot.Client.Tests.CloudBackup;

public sealed class CloudLogBackupTests : IDisposable
{
    private static readonly TimeZoneInfo PlusTwo =
        TimeZoneInfo.CreateCustomTimeZone("Test/PlusTwo", TimeSpan.FromHours(2), "Plus two", "Plus two");

    private readonly string _directory = Directory.CreateTempSubdirectory("modbot-cloud-backup-").FullName;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeCloudClient _cloud = new();
    private readonly MemoryInstallStore _installs = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var sent in _cloud.Sent)
            sent.Body.Dispose();

        Directory.Delete(_directory, recursive: true);
    }

    private string Outbox => Path.Combine(_directory, "cloud");

    private CloudLogBackup Backup(bool enabled = true, BackoffPolicy? backoff = null) => new(new CloudBackupOptions(
        Outbox,
        _clock,
        _cloud,
        _installs,
        "2026.9.0",
        Enabled: enabled,
        UserProfile: @"C:\Users\rin",
        TimeZone: PlusTwo,
        Backoff: backoff ?? new BackoffPolicy(jitter: () => 1.0)));

    /// <summary>Queues lines and waits out a batch, so the next pump has one closed batch to send.</summary>
    private async Task QueueAndCloseAsync(CloudLogBackup backup, params ReadLogLine[] lines)
    {
        backup.Offer(lines);
        await backup.PumpAsync(Ct);
        _clock.Advance(CloudOutbox.MaxBatchAge);
    }

    private int BatchFiles() => Directory.Exists(Outbox) ? Directory.GetFiles(Outbox, "batch-*.json.gz").Length : 0;

    [Fact]
    public void TheSettingIsOnByDefault()
    {
        Assert.True(ClientSettings.Default.SendLogsToCloud);
        Assert.True(ClientSettings.Load(Path.Combine(_directory, "missing.json")).SendLogsToCloud);

        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, """{ "pairingPage": "https://modbot.example/pair" }""");
        Assert.True(ClientSettings.Load(path).SendLogsToCloud);
    }

    [Fact]
    public void TurningItOffIsSavedWithoutLosingTheRestOfTheFile()
    {
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, """{ "pairingPage": "https://modbot.example/pair", "checkForUpdates": false }""");

        Assert.True(ClientSettings.SaveSwitch(path, ClientSettings.SendLogsToCloudField, false));

        var loaded = ClientSettings.Load(path);
        Assert.False(loaded.SendLogsToCloud);
        Assert.False(loaded.CheckForUpdates);
        Assert.Equal("https://modbot.example/pair", loaded.PairingPage.ToString());

        // A file that is not JSON is not overwritten.
        File.WriteAllText(path, "{ not json");
        Assert.False(ClientSettings.SaveSwitch(path, ClientSettings.SendLogsToCloudField, true));
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    [Fact]
    public async Task AnUnpairedClientSendsToTheDefaultCloud()
    {
        var backup = Backup();
        backup.Destination = CloudDestination.Resolve([]);

        await QueueAndCloseAsync(backup, Lines.Live(0), Lines.Live(50));
        Assert.True(await backup.PumpAsync(Ct));

        Assert.Equal([new Uri("https://cloud.modbot.co")], _cloud.Registered);
        var (install, body) = Assert.Single(_cloud.Sent);
        Assert.Equal(new Uri("https://cloud.modbot.co"), install.Endpoint);
        Assert.Equal("the-secret", _installs.Find(install.Endpoint)!.Secret);

        var root = body.RootElement;
        Assert.Equal("2026.9.0", root.GetProperty("clientVersion").GetString());
        Assert.Equal(_clock.UtcNow, root.GetProperty("sentAt").GetDateTimeOffset());
        Assert.Equal(2, root.GetProperty("lines").GetArrayLength());

        var line = root.GetProperty("lines")[1];
        Assert.Equal(Lines.File, line.GetProperty("file").GetString());
        Assert.Equal(50, line.GetProperty("offset").GetInt64());
        Assert.Equal("2026-09-15T10:00:00", line.GetProperty("loggedAt").GetString());
        Assert.Equal(120, line.GetProperty("utcOffsetMinutes").GetInt32());

        Assert.Equal(0, BatchFiles());
        Assert.Equal(0, backup.Status.Queued);
    }

    [Fact]
    public async Task TurningItOffStopsSendingAndDropsTheQueue()
    {
        var backup = Backup();
        await QueueAndCloseAsync(backup, Lines.Live(0));
        backup.Offer([Lines.Live(10)]);
        Assert.True(backup.Status.Queued > 0);

        backup.Enabled = false;

        Assert.Equal(CloudBackupState.Off, backup.Status.State);
        Assert.Equal(0, backup.Status.Queued);
        Assert.Equal(0, BatchFiles());
        Assert.False(backup.WantsLines);

        backup.Offer([Lines.Live(20)]);
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.False(await backup.PumpAsync(Ct));
        Assert.Empty(_cloud.Sent);
    }

    [Fact]
    public async Task TurningItOffCancelsABatchAlreadyOnItsWay()
    {
        var backup = Backup();
        await QueueAndCloseAsync(backup, Lines.Live(0));
        _cloud.Hang = new TaskCompletionSource();

        var pump = backup.PumpAsync(Ct);
        await _cloud.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        backup.Enabled = false;

        Assert.False(await pump.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Empty(_cloud.Sent);
        Assert.Equal(0, BatchFiles());
    }

    [Fact]
    public async Task TurningItBackOnSendsOnlyFromThatMoment()
    {
        var backup = Backup();
        backup.Offer([Lines.Live(0)]);
        backup.Enabled = false;
        backup.Offer([Lines.Live(10)]);

        backup.Enabled = true;
        await QueueAndCloseAsync(backup, Lines.Live(20));
        await backup.PumpAsync(Ct);

        var body = Assert.Single(_cloud.Sent).Body;
        var line = Assert.Single(body.RootElement.GetProperty("lines").EnumerateArray());
        Assert.Equal(20, line.GetProperty("offset").GetInt64());

        // Nor does a restart send the history from while it was off.
        var restarted = Backup();
        restarted.Offer([Lines.Replay(0), Lines.Replay(10), Lines.Replay(20)]);
        _clock.Advance(CloudOutbox.MaxBatchAge);
        await restarted.PumpAsync(Ct);
        Assert.Equal(0, restarted.Status.Queued);
    }

    [Fact]
    public async Task APairedServerThatTurnedItOffStopsSendingAndQueuing()
    {
        var backup = Backup();
        await QueueAndCloseAsync(backup, Lines.Live(0));

        var connection = ServerConnections.Make(_clock, _directory, "cats");
        connection.Cloud = new ServerCloudAnswer(null, Disabled: true, null);
        backup.Destination = CloudDestination.Resolve([connection]);

        Assert.Equal(CloudBackupState.TurnedOffByServer, backup.Status.State);
        Assert.Equal(0, backup.Status.Queued);
        Assert.False(backup.WantsLines);

        backup.Offer([Lines.Live(10)]);
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.False(await backup.PumpAsync(Ct));
        Assert.Empty(_cloud.Sent);
        Assert.Empty(_cloud.Registered);
    }

    [Fact]
    public async Task APairedServerThatHasNotAnsweredHoldsSendingButNotQueuing()
    {
        var backup = Backup();
        var connection = ServerConnections.Make(_clock, _directory, "cats");
        backup.Destination = CloudDestination.Resolve([connection]);

        await QueueAndCloseAsync(backup, Lines.Live(0));
        Assert.False(await backup.PumpAsync(Ct));
        Assert.Equal(CloudBackupState.WaitingForServer, backup.Status.State);
        Assert.Equal(1, backup.Status.Queued);

        connection.Cloud = new ServerCloudAnswer(new Uri("https://cloud.group.example"), false, "server-7");
        backup.Destination = CloudDestination.Resolve([connection]);

        Assert.True(await backup.PumpAsync(Ct));
        var (install, body) = Assert.Single(_cloud.Sent);
        Assert.Equal(new Uri("https://cloud.group.example"), install.Endpoint);
        Assert.Equal("server-7", body.RootElement.GetProperty("modbotServerId").GetString());
    }

    [Fact]
    public async Task TheOutboxSurvivesARestart()
    {
        var first = Backup();
        first.Destination = new CloudDestination(CloudDestinationKind.Wait, null, null);
        await QueueAndCloseAsync(first, Lines.Live(0), Lines.Live(10), Lines.Live(20));
        await first.PumpAsync(Ct);
        Assert.Equal(1, BatchFiles());

        // A line left in the open batch when the client closed is kept too.
        first.Offer([Lines.Live(30)]);
        await first.PumpAsync(Ct);

        var second = Backup();
        Assert.Equal(4, second.Status.Queued);

        Assert.True(await second.PumpAsync(Ct));
        Assert.True(await second.PumpAsync(Ct));

        Assert.Equal(4, _cloud.LinesSent);
        Assert.Equal(0, BatchFiles());
    }

    [Fact]
    public async Task ARestartSendsWhatWasWrittenWhileTheClientWasClosed()
    {
        var first = Backup();
        await QueueAndCloseAsync(first, Lines.Live(0), Lines.Live(10));
        await first.PumpAsync(Ct);

        var second = Backup();
        second.Offer([Lines.Replay(0), Lines.Replay(10), Lines.Replay(20), Lines.Replay(30)]);
        _clock.Advance(CloudOutbox.MaxBatchAge);
        await second.PumpAsync(Ct);
        await second.PumpAsync(Ct);

        var offsets = _cloud.Sent.Last().Body.RootElement.GetProperty("lines").EnumerateArray()
            .Select(l => l.GetProperty("offset").GetInt64()).ToList();
        Assert.Equal([20L, 30L], offsets);
    }

    [Fact]
    public async Task AFirstStartDoesNotUploadOldLog()
    {
        var backup = Backup();
        backup.Offer([Lines.Replay(0), Lines.Replay(10)]);
        _clock.Advance(CloudOutbox.MaxBatchAge);

        Assert.False(await backup.PumpAsync(Ct));
        Assert.Equal(0, backup.Status.Queued);
    }

    [Fact]
    public async Task TroubleBacksOffAndRetryAfterIsHonoured()
    {
        var backup = Backup();
        await QueueAndCloseAsync(backup, Lines.Live(0));

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
    }

    [Fact]
    public async Task AForgottenInstallRegistersAgain()
    {
        var backup = Backup();
        await QueueAndCloseAsync(backup, Lines.Live(0));

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
        await QueueAndCloseAsync(backup, Lines.Live(0), Lines.Live(10));

        _cloud.Answers.Enqueue(new IngestResult(IngestOutcome.Malformed));
        await backup.PumpAsync(Ct);

        Assert.Equal(0, BatchFiles());
        Assert.Equal(2, backup.Status.Dropped);
    }

    [Fact]
    public async Task TheUserFolderIsHiddenAndOnlyTheFileNameIsSent()
    {
        var backup = Backup();
        await QueueAndCloseAsync(backup, Lines.Live(0, @"2026.09.15 10:00:00 Debug      -  Loading C:\Users\Rin\AppData\LocalLow\VRChat\x and C:/Users/rin/y"));
        await backup.PumpAsync(Ct);

        var line = _cloud.Sent[0].Body.RootElement.GetProperty("lines")[0];
        Assert.Equal(@"2026.09.15 10:00:00 Debug      -  Loading %USERPROFILE%\AppData\LocalLow\VRChat\x and %USERPROFILE%/y", line.GetProperty("text").GetString());
        Assert.DoesNotContain("\\", line.GetProperty("file").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLogReaderNeverWaitsOnTheBackup()
    {
        var logs = Directory.CreateDirectory(Path.Combine(_directory, "logs")).FullName;
        var logFile = Path.Combine(logs, Lines.File);
        File.WriteAllText(logFile, "");

        var backup = Backup();
        var observer = new PresenceObserver(new VRChatLogTail(logs), _clock, lines: backup);
        var engine = new ClientEngine(observer, _clock);

        // The first pass primes the reader; everything after it is live.
        await engine.TickAsync(Ct);

        File.AppendAllText(logFile, "2026.09.15 10:00:00 Debug      -  [Behaviour] OnPlayerJoined Rin (usr_1)\n", Encoding.UTF8);
        await engine.TickAsync(Ct);
        _clock.Advance(CloudOutbox.MaxBatchAge);

        // Cloud hangs on the first batch, and stays hung.
        _cloud.Hang = new TaskCompletionSource();
        var pump = backup.PumpAsync(Ct);
        await _cloud.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Meanwhile the reader goes on reading, and each turn finishes.
        for (var i = 0; i < 20; i++)
        {
            File.AppendAllText(logFile, $"2026.09.15 10:00:{i:00} Debug      -  [IK Debug Log] fps {i}\n", Encoding.UTF8);
            var tick = engine.TickAsync(Ct);
            Assert.True(tick.IsCompleted, "A reader turn waited on the backup.");
            await tick;
        }

        Assert.False(pump.IsCompleted);
        Assert.Equal(21, backup.Status.Queued);
        Assert.Equal(21, engine.LogHealth.LinesRead);

        _cloud.Hang.SetResult();
        Assert.True(await pump.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        var parsed = _cloud.Sent[0].Body.RootElement.GetProperty("lines")[0].GetProperty("event");
        Assert.Equal("PlayerJoined", parsed.GetProperty("type").GetString());
        Assert.Equal("usr_1", parsed.GetProperty("data").GetProperty("userId").GetString());
    }

    [Fact]
    public void TheQueueInMemoryIsBounded()
    {
        var backup = Backup();
        backup.Destination = new CloudDestination(CloudDestinationKind.Wait, null, null);

        backup.Offer([.. Enumerable.Range(0, CloudLogBackup.QueueLimit + 5).Select(i => Lines.Live(i))]);

        Assert.Equal(CloudLogBackup.QueueLimit, backup.Status.Queued);
        Assert.Equal(5, backup.Status.Dropped);
    }

    [Fact]
    public void TheOutboxDropsTheOldestBatchesPastItsCap()
    {
        var outbox = new CloudOutbox(Outbox, cap: CloudOutbox.MaxBytesPerBatch);

        // Random text compresses poorly, so a few full batches pass a 512 KB cap.
        var random = new Random(7);
        for (var batch = 0; batch < 6; batch++)
        {
            var lines = Enumerable.Range(0, CloudOutbox.MaxLinesPerBatch).Select(i => new BackupLine(
                Lines.File,
                (batch * 10_000) + i,
                new string([.. Enumerable.Range(0, 400).Select(_ => (char)random.Next(33, 126))]),
                null,
                null,
                null)).ToList();
            outbox.Append(lines, _clock.UtcNow);
        }

        Assert.True(outbox.DroppedLines > 0);
        Assert.True(outbox.Batches()[0].Sequence > 1);
    }
}
internal static class ServerConnections
{
    public static ServerConnection Make(FakeClock clock, string directory, string serverId)
    {
        var pairing = new ServerPairing(serverId, new Uri($"https://{serverId}.example"), "token", "grp_1");
        var serverClock = new ServerClock(clock);

        return new ServerConnection(
            pairing,
            new FileEventBuffer(Path.Combine(directory, $"{serverId}-queue.jsonl"), clock),
            new PresenceEventMapper(new LogTimestampConverter(TimeZoneInfo.Utc), serverClock),
            serverClock,
            new NoTransport(),
            clock,
            "2026.9.0");
    }

    private sealed class NoTransport : IIngestTransport
    {
        public Task<IngestResult> SendAsync(ServerPairing pairing, EventBatch batch, CancellationToken cancellationToken) =>
            Task.FromResult(new IngestResult(IngestOutcome.Accepted));
    }
}
