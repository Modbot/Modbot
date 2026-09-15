using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Cloud.Engine;
using Modbot.Cloud.Features.EventBackup;

namespace Modbot.Cloud.Tests;

[Collection(nameof(PostgresCollection))]
public class EventBackupTests(PostgresFixture db)
{
    private static readonly DateTimeOffset Happened = new(2026, 9, 15, 11, 30, 5, TimeSpan.Zero);

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
        var batch = host.Batch(CloudTestHost.Event("e1", Happened));

        using var none = await host.PostBatchAsync(null, batch);
        using var wrongSecret = await host.PostBatchAsync($"{id}.not-the-secret", batch);
        using var unknown = await host.PostBatchAsync($"{Guid.NewGuid()}.whatever", batch);

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);

        await using var engine = db.NewEngineContext();
        Assert.Equal(0, await engine.Events.CountAsync(Ct));
    }

    [Fact]
    public async Task EventsAreStoredWithTheirThreeTimesForAnyInstance()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, bearer) = await host.RegisterAsync();

        // The client never measured Cloud's clock, and its own is ten seconds slow.
        var sentAt = CloudTestHost.Start.AddSeconds(-10);
        host.Time.Advance(TimeSpan.FromMilliseconds(250));

        using var response = await host.PostBatchAsync(bearer, CloudTestHost.Batch(sentAt, null, "unknown",
            CloudTestHost.Event("e1", Happened, instanceId: "999~private(usr_2)", groupId: null),
            CloudTestHost.Event("e2", Happened, type: "InstanceLeft", groupId: "grp_1")));

        Assert.Equal((2, 0), await ReadResultAsync(response));

        await using var engine = db.NewEngineContext();
        var stored = await engine.Events.OrderBy(e => e.ClientEventId).ToListAsync(Ct);

        var first = stored[0];
        Assert.Equal(id, first.InstallId);
        Assert.Equal(CloudTestHost.Start.AddMilliseconds(250), first.ReceivedAt);
        Assert.Equal(sentAt, first.SentAt);
        Assert.Equal(Happened.AddMilliseconds(10_250), first.OccurredAt);
        Assert.Equal(10_250, first.ClockAdjustmentMs);
        Assert.Equal(EventTypes.InstanceJoined, first.Type);
        Assert.Null(first.GroupId);
        Assert.Equal("999~private(usr_2)", first.InstanceId);
        using (var data = JsonDocument.Parse(first.Data))
            Assert.Equal("Rin", data.RootElement.GetProperty("displayName").GetString());

        Assert.Equal(EventTypes.InstanceLeft, stored[1].Type);
        Assert.Equal("grp_1", stored[1].GroupId);

        var clock = await engine.InstallClocks.SingleAsync(Ct);
        Assert.Equal(10_250, clock.ObservedOffsetMs);
        Assert.Equal(10_250, clock.AppliedOffsetMs);
        Assert.False(clock.Disagrees);

        var day = await engine.EventDayTotals.SingleAsync(Ct);
        Assert.Equal((new DateOnly(2026, 9, 15), id, 2L), (day.Day, day.InstallId, day.Events));
    }

    [Fact]
    public async Task RetriesNeverDuplicate()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterAsync();
        var (_, other) = await host.RegisterAsync();

        var first = host.Batch(CloudTestHost.Event("a", Happened), CloudTestHost.Event("b", Happened));

        using (var sent = await host.PostBatchAsync(bearer, first))
            Assert.Equal((2, 0), await ReadResultAsync(sent));

        // The same batch again, a month later: a retry after a lost answer.
        host.Time.Advance(TimeSpan.FromDays(31));
        using (var retried = await host.PostBatchAsync(bearer, first))
            Assert.Equal((0, 2), await ReadResultAsync(retried));

        // An outbox that closed the same event twice after a crash, and the same id twice in a batch.
        using (var overlap = await host.PostBatchAsync(bearer, host.Batch(
                   CloudTestHost.Event("b", Happened), CloudTestHost.Event("c", Happened), CloudTestHost.Event("c", Happened))))
        {
            Assert.Equal((1, 2), await ReadResultAsync(overlap));
        }

        // Event ids are per install: another install's "a" is its own event.
        using (var otherInstall = await host.PostBatchAsync(other, host.Batch(CloudTestHost.Event("a", Happened))))
            Assert.Equal((1, 0), await ReadResultAsync(otherInstall));

        await using var engine = db.NewEngineContext();
        Assert.Equal(4, await engine.Events.CountAsync(Ct));
        Assert.Equal(4, await engine.EventHourTotals.SumAsync(t => t.Events, Ct));
    }

    [Fact]
    public async Task UnknownEventTypesKeepTheirRawTypeAndData()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterAsync();

        using (var response = await host.PostBatchAsync(bearer, host.Batch(
                   CloudTestHost.Event("new", Happened, type: "InstanceTeleported", data: new { detail = "kept" }))))
        {
            Assert.Equal((1, 0), await ReadResultAsync(response));
        }

        await using var engine = db.NewEngineContext();
        var stored = await engine.Events.SingleAsync(Ct);
        Assert.Equal(EventTypes.Unrecognised, stored.Type);
        Assert.Equal("InstanceTeleported", stored.TypeRaw);
        Assert.Contains("kept", stored.Data, StringComparison.Ordinal);

        var hour = await engine.EventHourTotals.SingleAsync(Ct);
        Assert.Equal((EventTypes.Unrecognised, new DateTimeOffset(2026, 9, 15, 11, 0, 0, TimeSpan.Zero), 1L), (hour.Type, hour.Hour, hour.Events));
    }

    [Fact]
    public async Task ATrustedClientMeasureIsLeftAsItIs()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterAsync();

        // The client measured itself 30 s behind, well, and already corrected the event time by it.
        var sentAt = CloudTestHost.Start.AddSeconds(-30);
        host.Time.Advance(TimeSpan.FromMilliseconds(400));

        using var response = await host.PostBatchAsync(bearer, CloudTestHost.Batch(sentAt, 30_000, "good", CloudTestHost.Event("e", Happened)));
        await ReadResultAsync(response);

        await using var engine = db.NewEngineContext();
        var stored = await engine.Events.SingleAsync(Ct);
        Assert.Equal(Happened, stored.OccurredAt);
        Assert.Equal(0, stored.ClockAdjustmentMs);

        var clock = await engine.InstallClocks.SingleAsync(Ct);
        Assert.Equal((30_000L, "good", 30_400L, 30_000L), (clock.ReportedOffsetMs, clock.ReportedConfidence, clock.ObservedOffsetMs, clock.AppliedOffsetMs));
    }

    [Fact]
    public async Task AClientWhoseMeasureDisagreesIsFlaggedAndCorrectedByCloud()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterAsync();

        // Claims an hour's offset, but its sent time matches Cloud's clock.
        using var response = await host.PostBatchAsync(bearer, CloudTestHost.Batch(CloudTestHost.Start, 3_600_000, "good", CloudTestHost.Event("e", Happened)));
        await ReadResultAsync(response);

        await using var engine = db.NewEngineContext();
        Assert.True((await engine.InstallClocks.SingleAsync(Ct)).Disagrees);

        var stored = await engine.Events.SingleAsync(Ct);
        Assert.Equal(-3_600_000, stored.ClockAdjustmentMs);
        Assert.Equal(Happened.AddHours(-1), stored.OccurredAt);
    }

    [Fact]
    public async Task BatchesPerMinuteAreLimitedPerInstall()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterAsync();
        var (_, other) = await host.RegisterAsync();

        for (var i = 0; i < EventBackupLimits.BatchesPerMinute; i++)
        {
            using var ok = await host.PostBatchAsync(bearer, host.Batch(CloudTestHost.Event($"e{i}", Happened)));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        host.Time.Advance(TimeSpan.FromSeconds(45));
        using (var refused = await host.PostBatchAsync(bearer, host.Batch(CloudTestHost.Event("late", Happened))))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(15), refused.Headers.RetryAfter?.Delta);
        }

        using (var otherInstall = await host.PostBatchAsync(other, host.Batch(CloudTestHost.Event("x", Happened))))
            Assert.Equal(HttpStatusCode.OK, otherInstall.StatusCode);

        host.Time.Advance(TimeSpan.FromSeconds(15));
        using (var later = await host.PostBatchAsync(bearer, host.Batch(CloudTestHost.Event("late", Happened))))
            Assert.Equal(HttpStatusCode.OK, later.StatusCode);
    }

    [Fact]
    public async Task EventsPerHourAreLimitedPerInstall()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, bearer) = await host.RegisterAsync();
        var limits = host.Services.GetRequiredService<EventBackupLimits>();

        Assert.Null(limits.Events.TryTake(id.ToString("N"), EventBackupLimits.EventsPerHour - 1));

        using var refused = await host.PostBatchAsync(bearer, host.Batch(CloudTestHost.Event("a", Happened), CloudTestHost.Event("b", Happened)));

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task OversizedAndMalformedBatchesAreRefused()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterAsync();

        var tooMany = Enumerable.Range(0, EventBackupLimits.MaxEventsPerBatch + 1).Select(i => CloudTestHost.Event($"e{i}", Happened)).ToArray();
        using (var response = await host.PostBatchAsync(bearer, host.Batch(tooMany)))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        // Small on the wire, too big once expanded.
        using (var response = await host.PostBatchAsync(bearer, host.Batch(
                   CloudTestHost.Event("big", Happened, data: new { displayName = new string('a', EventBackupLimits.MaxDecompressedBytes) }))))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }

        using (var response = await host.PostBatchAsync(bearer, host.Batch(new { type = "InstanceJoined", occurredAt = Happened })))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using (var response = await host.PostBatchAsync(bearer, new { events = Array.Empty<object>() }))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var engine = db.NewEngineContext();
        Assert.Equal(0, await engine.Events.CountAsync(Ct));
    }

    [Fact]
    public async Task LargeDataIsNotStored()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await host.RegisterAsync();

        using var response = await host.PostBatchAsync(bearer, host.Batch(
            CloudTestHost.Event("e", Happened, data: new { avatarName = new string('c', StoredEvent.MaxDataBytes) })));
        Assert.Equal((1, 0), await ReadResultAsync(response));

        await using var engine = db.NewEngineContext();
        Assert.Equal("{}", (await engine.Events.SingleAsync(Ct)).Data);
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
            events = new[] { CloudTestHost.Event("e", Happened) },
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
}
