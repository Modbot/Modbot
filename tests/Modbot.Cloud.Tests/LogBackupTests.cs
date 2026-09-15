using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Engine;
using Modbot.Cloud.Features.LogBackup;

namespace Modbot.Cloud.Tests;

[Collection(nameof(PostgresCollection))]
public class LogBackupTests(PostgresFixture db)
{
    private const string File = "output_log_2026-09-15_10-00-00.txt";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(int Stored, int Duplicates)> ReadResultAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return (body.GetProperty("stored").GetInt32(), body.GetProperty("duplicates").GetInt32());
    }

    [Fact]
    public async Task ABatchWithoutAKnownInstallIsRefused()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, _) = await host.RegisterAsync();
        var batch = host.Batch(CloudTestHost.Line(File, 0, "a line"));

        using var none = await host.PostBatchAsync(null, batch);
        using var wrongSecret = await host.PostBatchAsync($"{id}.not-the-secret", batch);
        using var unknown = await host.PostBatchAsync($"{Guid.NewGuid()}.whatever", batch);

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);

        await using var engine = db.NewEngineContext();
        Assert.Equal(0, await engine.LogLines.CountAsync(Ct));
    }

    [Fact]
    public async Task LinesAreStoredWithTheirThreeTimes()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, bearer) = await host.RegisterAsync();

        // The client's clock is ten seconds slow, and it wrote this line at 11:59:00 in UTC+2.
        var sentAt = CloudTestHost.Start.AddSeconds(-10);
        host.Time.Advance(TimeSpan.FromMilliseconds(250));

        using var response = await host.PostBatchAsync(bearer, CloudTestHost.Batch(sentAt, null, "unknown",
            CloudTestHost.Line(File, 120, "2026.09.15 13:59:00 Debug      -  hello", new DateTime(2026, 9, 15, 13, 59, 0), 120)));

        Assert.Equal((1, 0), await ReadResultAsync(response));

        await using var engine = db.NewEngineContext();
        var line = await engine.LogLines.SingleAsync(Ct);
        Assert.Equal(CloudTestHost.Start.AddMilliseconds(250), line.ReceivedAt);
        Assert.Equal(sentAt, line.SentAt);
        Assert.Equal(new DateTime(2026, 9, 15, 13, 59, 0), line.LoggedAt);
        Assert.Equal((short)120, line.UtcOffsetMinutes);
        Assert.Equal(id, line.InstallId);
        Assert.Equal(120, line.LineOffset);
        Assert.Equal("2026.09.15 13:59:00 Debug      -  hello", line.Text);

        var file = await engine.LogFiles.SingleAsync(Ct);
        Assert.Equal(file.Id, line.LogFileId);
        Assert.Equal(File, file.Name);
        Assert.Equal(120, file.StoredThrough);

        var clock = await engine.InstallClocks.SingleAsync(Ct);
        Assert.Equal(id, clock.InstallId);
        Assert.Equal(10_250, clock.ObservedOffsetMs);
        Assert.Equal(10_250, clock.AppliedOffsetMs);
        Assert.False(clock.Disagrees);

        var day = await engine.LineDayTotals.SingleAsync(Ct);
        Assert.Equal((new DateOnly(2026, 9, 15), id, 1L), (day.Day, day.InstallId, day.Lines));
    }

    [Fact]
    public async Task RetriesAndReplaysNeverDuplicate()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterAsync();

        var first = host.Batch(
            CloudTestHost.Line(File, 0, "zero"),
            CloudTestHost.Line(File, 5, "five"),
            CloudTestHost.Line(File, 10, "ten"));

        using (var sent = await host.PostBatchAsync(bearer, first))
            Assert.Equal((3, 0), await ReadResultAsync(sent));

        // The same batch again: a retry after a lost answer.
        using (var retried = await host.PostBatchAsync(bearer, first))
            Assert.Equal((0, 3), await ReadResultAsync(retried));

        // A replay after a restart overlaps what was stored and runs on past it; the same offset
        // twice in one batch is stored once.
        using (var replay = await host.PostBatchAsync(bearer, host.Batch(
                   CloudTestHost.Line(File, 5, "five"),
                   CloudTestHost.Line(File, 10, "ten"),
                   CloudTestHost.Line(File, 15, "fifteen"),
                   CloudTestHost.Line(File, 15, "fifteen"))))
        {
            Assert.Equal((1, 3), await ReadResultAsync(replay));
        }

        // Another file from the same install keeps its own offsets.
        using (var otherFile = await host.PostBatchAsync(bearer, host.Batch(CloudTestHost.Line("output_log_other.txt", 0, "zero"))))
            Assert.Equal((1, 0), await ReadResultAsync(otherFile));

        await using var engine = db.NewEngineContext();
        Assert.Equal(["zero", "five", "ten", "fifteen", "zero"], await engine.LogLines.OrderBy(l => l.Id).Select(l => l.Text).ToListAsync(Ct));
        Assert.Equal(5, (await engine.LineDayTotals.SingleAsync(Ct)).Lines);
    }

    [Fact]
    public async Task TwoInstallsSendingTheSameFileNameAreKeptApart()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, first) = await host.RegisterAsync();
        var (_, second) = await host.RegisterAsync();

        using (var a = await host.PostBatchAsync(first, host.Batch(CloudTestHost.Line(File, 0, "a"))))
            Assert.Equal((1, 0), await ReadResultAsync(a));

        using (var b = await host.PostBatchAsync(second, host.Batch(CloudTestHost.Line(File, 0, "b"))))
            Assert.Equal((1, 0), await ReadResultAsync(b));
    }

    [Fact]
    public async Task EventsAreNamedTheWayCloudNamesThemAndUnknownOnesAreKept()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterAsync();
        var logged = new DateTime(2026, 9, 15, 11, 30, 5);

        var batch = host.Batch(
            CloudTestHost.Line(File, 0, "joined", logged, 0, new { type = "PlayerJoined", data = new { displayName = "Rin", userId = "usr_1" } }),
            CloudTestHost.Line(File, 10, "something new", logged, 0, new { type = "SomethingNew", data = new { detail = 1 } }),
            CloudTestHost.Line(File, 20, "plain line", logged, 0));

        using (var response = await host.PostBatchAsync(bearer, batch))
            Assert.Equal((3, 0), await ReadResultAsync(response));

        // Sent again, nothing is counted twice.
        using (var again = await host.PostBatchAsync(bearer, batch))
            Assert.Equal((0, 3), await ReadResultAsync(again));

        await using var engine = db.NewEngineContext();
        var events = await engine.LogEvents.OrderBy(e => e.LineOffset).ToListAsync(Ct);
        Assert.Equal(2, events.Count);

        Assert.Equal(LogEventTypes.PlayerJoined, events[0].Type);
        Assert.Null(events[0].TypeRaw);
        Assert.Equal("client/2026.9.0", events[0].ParsedBy);
        using (var data = JsonDocument.Parse(events[0].Data))
            Assert.Equal("usr_1", data.RootElement.GetProperty("userId").GetString());

        Assert.Equal(LogEventTypes.Unrecognised, events[1].Type);
        Assert.Equal("SomethingNew", events[1].TypeRaw);
        Assert.Contains("detail", events[1].Data, StringComparison.Ordinal);

        var hours = await engine.EventHourTotals.OrderBy(t => t.Type).ToListAsync(Ct);
        Assert.Equal(
            [(LogEventTypes.Unrecognised, 1L), (LogEventTypes.PlayerJoined, 1L)],
            hours.Select(h => (h.Type, h.Events)).OrderBy(h => h.Type, StringComparer.Ordinal).ToList());
        Assert.All(hours, h => Assert.Equal(new DateTimeOffset(2026, 9, 15, 11, 0, 0, TimeSpan.Zero), h.Hour));
    }

    [Fact]
    public async Task EventTimesAreCorrectedByTheClientsOwnMeasureWhenItAgrees()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterAsync();

        // The client's clock is 30 s behind Cloud's and it measured that well; the request took 400 ms.
        var sentAt = CloudTestHost.Start.AddSeconds(-30);
        host.Time.Advance(TimeSpan.FromMilliseconds(400));

        using var response = await host.PostBatchAsync(bearer, CloudTestHost.Batch(sentAt, 30_000, "good",
            CloudTestHost.Line(File, 0, "joined", new DateTime(2026, 9, 15, 13, 0, 0), 120, new { type = "PlayerJoined", data = new { } })));
        await ReadResultAsync(response);

        await using var engine = db.NewEngineContext();
        var logEvent = await engine.LogEvents.SingleAsync(Ct);
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 11, 0, 30, TimeSpan.Zero), logEvent.OccurredAt);

        var clock = await engine.InstallClocks.SingleAsync(Ct);
        Assert.Equal(30_000, clock.ReportedOffsetMs);
        Assert.Equal("good", clock.ReportedConfidence);
        Assert.Equal(30_400, clock.ObservedOffsetMs);
        Assert.Equal(30_000, clock.AppliedOffsetMs);
        Assert.False(clock.Disagrees);
    }

    [Fact]
    public async Task AClientWhoseMeasureDisagreesIsFlaggedAndCloudsOwnMeasureIsUsed()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterAsync();

        using var response = await host.PostBatchAsync(bearer, CloudTestHost.Batch(CloudTestHost.Start, 3_600_000, "good",
            CloudTestHost.Line(File, 0, "line", new DateTime(2026, 9, 15, 12, 0, 0), 0, new { type = "PlayerLeft", data = new { } })));
        await ReadResultAsync(response);

        await using var engine = db.NewEngineContext();
        var clock = await engine.InstallClocks.SingleAsync(Ct);
        Assert.True(clock.Disagrees);
        Assert.Equal(0, clock.AppliedOffsetMs);
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero), (await engine.LogEvents.SingleAsync(Ct)).OccurredAt);
    }

    [Fact]
    public async Task BatchesPerMinuteAreLimitedPerInstall()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterAsync();
        var (_, other) = await host.RegisterAsync();

        for (var i = 0; i < LogBackupLimits.BatchesPerMinute; i++)
        {
            using var ok = await host.PostBatchAsync(bearer, host.Batch(CloudTestHost.Line(File, i, "line")));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        host.Time.Advance(TimeSpan.FromSeconds(45));
        using (var refused = await host.PostBatchAsync(bearer, host.Batch(CloudTestHost.Line(File, 100, "line"))))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(15), refused.Headers.RetryAfter?.Delta);
        }

        using (var otherInstall = await host.PostBatchAsync(other, host.Batch(CloudTestHost.Line(File, 0, "line"))))
            Assert.Equal(HttpStatusCode.OK, otherInstall.StatusCode);

        host.Time.Advance(TimeSpan.FromSeconds(15));
        using (var later = await host.PostBatchAsync(bearer, host.Batch(CloudTestHost.Line(File, 100, "line"))))
            Assert.Equal(HttpStatusCode.OK, later.StatusCode);
    }

    [Fact]
    public async Task LinesPerHourAreLimitedPerInstall()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterAsync();
        var limits = (LogBackupLimits)host.Services.GetService(typeof(LogBackupLimits))!;

        // Use up all but ten of the hour's lines without sending them.
        Assert.Null(limits.Lines.TryTake((await SingleInstallIdAsync()).ToString("N"), LogBackupLimits.LinesPerHour - 10));

        var lines = Enumerable.Range(0, 11).Select(i => CloudTestHost.Line(File, i, "line")).ToArray();
        using var refused = await host.PostBatchAsync(bearer, host.Batch(lines));

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task OversizedAndMalformedBatchesAreRefused()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterAsync();

        var tooMany = Enumerable.Range(0, LogBackupLimits.MaxLinesPerBatch + 1).Select(i => CloudTestHost.Line(File, i, "x")).ToArray();
        using (var response = await host.PostBatchAsync(bearer, host.Batch(tooMany)))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        // Small on the wire, too big once expanded.
        using (var response = await host.PostBatchAsync(bearer, host.Batch(CloudTestHost.Line(File, 0, new string('a', LogBackupLimits.MaxDecompressedBytes)))))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        using (var response = await host.PostBatchAsync(bearer, host.Batch(CloudTestHost.Line(@"C:\Users\rin\output_log.txt", 0, "x"))))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using (var response = await host.PostBatchAsync(bearer, host.Batch(CloudTestHost.Line(File, -1, "x"))))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using (var response = await host.PostBatchAsync(bearer, new { lines = Array.Empty<object>() }))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var engine = db.NewEngineContext();
        Assert.Equal(0, await engine.LogLines.CountAsync(Ct));
    }

    [Fact]
    public async Task LongLinesAreCutAndOddDataIsNotStored()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterAsync();

        using var response = await host.PostBatchAsync(bearer, host.Batch(
            CloudTestHost.Line(File, 0, new string('b', LogLine.MaxTextLength + 50) + "\0", @event: new { type = "PlayerJoined", data = new { big = new string('c', LogEvent.MaxDataBytes) } })));
        Assert.Equal((1, 0), await ReadResultAsync(response));

        await using var engine = db.NewEngineContext();
        Assert.Equal(LogLine.MaxTextLength, (await engine.LogLines.SingleAsync(Ct)).Text.Length);
        Assert.Equal("{}", (await engine.LogEvents.SingleAsync(Ct)).Data);
    }

    [Fact]
    public async Task ASeenBatchUpdatesTheInstall()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, bearer) = await host.RegisterAsync();
        host.Time.Advance(TimeSpan.FromMinutes(5));

        var batch = new
        {
            clientVersion = "2026.9.1",
            sentAt = host.Time.GetUtcNow(),
            modbotServerId = "server-7",
            lines = new[] { CloudTestHost.Line(File, 0, "x") },
        };

        using var response = await host.PostBatchAsync(bearer, batch);
        await ReadResultAsync(response);

        await using var cloud = db.NewCloudContext();
        var install = await cloud.Installs.SingleAsync(i => i.Id == id, Ct);
        Assert.Equal("2026.9.1", install.ClientVersion);
        Assert.Equal("server-7", install.ModbotServerId);
        Assert.Equal(CloudTestHost.Start.AddMinutes(5), install.LastSeenAt);
    }

    [Fact]
    public async Task TimeAnswersWithCloudsClock()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var response = await host.GetAsync("/api/v1/time");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(CloudTestHost.Start, body.GetProperty("serverTime").GetDateTimeOffset());
    }

    private async Task<Guid> SingleInstallIdAsync()
    {
        await using var cloud = db.NewCloudContext();
        return await cloud.Installs.Select(i => i.Id).SingleAsync(Ct);
    }
}
