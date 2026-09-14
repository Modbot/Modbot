using System.Net;
using Modbot.Api.Features.Health;
using Modbot.Api.Tests.Fakes;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Tests.Features.Health;

/// <summary>
/// The distinction the whole screen exists for: waiting on purpose versus broken.
/// </summary>
public class GateStatusTests
{
    private static BucketHealth Bucket(
        bool coldStopped = false, bool alerting = false, DateTimeOffset? until = null) =>
        new("groups.auditlog", "groups.auditlog", null, 0.125, 1.0, coldStopped, until, alerting, 0, null);

    [Fact]
    public void RateLimited_IsWaitingOnPurpose()
    {
        var health = GateHealthReader.Describe(VRChatSessionState.RateLimited, [Bucket(coldStopped: true)]);

        Assert.Equal(GateStatus.WaitingOnPurpose, health.Status);
        Assert.Equal(1, health.ColdStoppedBuckets);
    }

    [Fact]
    public void WafBlocked_NeedsAnOperator()
    {
        // Identical symptom to a cold stop -- traffic stopped, data not arriving -- and the
        // opposite remedy. A proxy fixes this one and waiting never does.
        var health = GateHealthReader.Describe(VRChatSessionState.WafBlocked, []);

        Assert.Equal(GateStatus.NeedsOperator, health.Status);
        Assert.Contains("proxy", health.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unconfigured_IsNotAFault()
    {
        var health = GateHealthReader.Describe(VRChatSessionState.Unconfigured, []);

        Assert.Equal(GateStatus.NotConfigured, health.Status);
    }

    [Fact]
    public void AnExhaustedBucket_OutranksAHealthySession()
    {
        // Spec 4.3.1 step 4: after a small number of failed probes the bucket stays stopped and
        // the operator is alerted. A gate reporting Healthy while one class has given up is a
        // deployment with a hole in it, and "healthy" is the wrong headline for that.
        var health = GateHealthReader.Describe(
            VRChatSessionState.Healthy, [Bucket(coldStopped: true, alerting: true)]);

        Assert.Equal(GateStatus.NeedsOperator, health.Status);
        Assert.Equal(1, health.AlertingBuckets);
    }

    [Fact]
    public void AColdStopUnderAHealthySession_IsStillWaiting()
    {
        var until = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        var health = GateHealthReader.Describe(
            VRChatSessionState.Healthy, [Bucket(coldStopped: true, until: until)]);

        Assert.Equal(GateStatus.WaitingOnPurpose, health.Status);
        Assert.Equal(until, health.ColdStopEndsAt);
    }

    [Fact]
    public void Healthy_IsWorking()
    {
        var health = GateHealthReader.Describe(VRChatSessionState.Healthy, [Bucket()]);

        Assert.Equal(GateStatus.Working, health.Status);
        Assert.Null(health.ColdStopEndsAt);
    }
}

[Collection(nameof(PostgresCollection))]
public class SyncHealthEndpointTests
{
    private readonly PostgresFixture _db;

    public SyncHealthEndpointTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task GateHealth_IsReadableByAnySignedInAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().SignedInAs();
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);

        // Deliberately a permission with nothing to do with logs or settings: a moderator whose
        // action did nothing has to be able to tell waiting from broken.
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, ct);
        var health = await host.GetJsonAsync<GateHealth>("/api/health/gate", cookie, ct);

        Assert.Equal(GateStatus.Working, health.Status);
    }

    [Fact]
    public async Task SyncHealth_NeedsViewOperationalLog()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var response = await host.GetAsync("/api/health/sync", cookie, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SyncHealth_CarriesThePollRateAndItsReason()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        host.Diagnostics.RecordPollRate(new PollRateDecision(
            TimeSpan.FromMinutes(5),
            "nothing new for 6 polls; holding at the slowest rate until something happens",
            6,
            host.Clock.UtcNow));

        host.Diagnostics.RecordAuditLogRun(new SyncRunReport(
            SyncOutcome.Quiet, host.Clock.UtcNow, TimeSpan.FromSeconds(0.4), "no new entries"));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, ct);
        var health = await host.GetJsonAsync<SyncHealth>("/api/health/sync", cookie, ct);

        Assert.True(health.SyncRunningInThisProcess);
        Assert.Equal(300, health.AuditLogPollRate!.IntervalSeconds);
        Assert.Equal(6, health.AuditLogPollRate.ConsecutiveQuietPolls);

        // The reason is the whole point. An interval without one leaves a quiet group and a stuck
        // producer looking identical (spec 4.2.3).
        Assert.Contains("nothing new", health.AuditLogPollRate.Reason, StringComparison.Ordinal);
        Assert.Equal("Quiet", health.LastAuditLogRun!.Outcome);
    }

    /// <summary>
    /// "Catch-up complete" can mean the whole log or the most recent 7,500 entries of it, and the
    /// cursor cannot tell an operator which. The horizon can.
    /// </summary>
    [Fact]
    public async Task TheHistoryHorizon_IsReportedWhenTheWalkStoppedAtTheCap()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        host.Diagnostics.RecordHistoryHorizon(7_600);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, ct);
        var health = await host.GetJsonAsync<SyncHealth>("/api/health/sync", cookie, ct);

        Assert.Equal(7_600, health.AuditLogHistoryHorizon!.EntriesRead);
        Assert.Equal(host.Clock.UtcNow, health.AuditLogHistoryHorizon.ReachedAt);
    }

    [Fact]
    public async Task UnmappedEvents_AreReportedWithASampleToLookUp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        host.Diagnostics.RecordUnmappedEvent("group.something.new", "gaud_1", "Somebody did a thing");
        host.Diagnostics.RecordUnmappedEvent("group.something.new", "gaud_2", "Again");

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, ct);
        var health = await host.GetJsonAsync<SyncHealth>("/api/health/sync", cookie, ct);

        var unmapped = Assert.Single(health.UnmappedAuditEvents);

        Assert.Equal(2, unmapped.Count);
        Assert.Equal("gaud_1", unmapped.SampleEntryId);
    }

    [Fact]
    public async Task AHostWithNoProducers_SaysSoRatherThanLookingIdle()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, withSync: false);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, ct);
        var health = await host.GetJsonAsync<SyncHealth>("/api/health/sync", cookie, ct);

        // "Nothing is running" and "everything is idle" produce the same empty fields and mean
        // different things, so the difference is a field rather than an inference.
        Assert.False(health.SyncRunningInThisProcess);
        Assert.Null(health.AuditLogPollRate);
    }

    [Fact]
    public async Task TheServerClock_TravelsWithTheResponse()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        host.Clock.UtcNow = new DateTimeOffset(2026, 7, 4, 8, 30, 0, TimeSpan.Zero);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, ct);
        var health = await host.GetJsonAsync<SyncHealth>("/api/health/sync", cookie, ct);

        Assert.Equal(host.Clock.UtcNow, health.Now);
    }
}

/// <summary>
/// Every enum on the health payload is written as its name, not its number.
/// </summary>
/// <remarks>
/// <para>
/// This is a regression test for a screen that crashed on load. <c>DiscordBotState</c> had no
/// string converter, so the payload carried <c>"state": 1</c> where the page's lookup table is
/// keyed on <c>"NotConfigured"</c>. The lookup missed, the next line read a field off the miss,
/// and the whole Health screen threw during render over one card.
/// </para>
/// <para>
/// The page has since been hardened to survive a value it does not know, but that is the second
/// line of defence. This is the first: an enum that reaches a browser is part of the contract,
/// and a number is not the contract. The test covers the whole payload rather than the one enum
/// that broke, because the next one added would fail the same way and for the same reason.
/// </para>
/// </remarks>
public class HealthPayloadEnumTests
{
    private static readonly System.Text.Json.JsonSerializerOptions Web =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    [Fact]
    public void TheDiscordBotStateIsWrittenAsItsName()
    {
        var snapshot = new Modbot.Core.Discord.DiscordBotSnapshot(
            Modbot.Core.Discord.DiscordBotState.NotConfigured,
            ConnectedSince: null,
            LastError: null,
            LastErrorAt: null,
            CommandsRegistered: 0,
            LogChannelConfigured: false,
            LastPostedAt: null,
            PostedInThisProcess: 0);

        var json = System.Text.Json.JsonSerializer.Serialize(snapshot, Web);

        Assert.Contains("\"state\":\"NotConfigured\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"state\":1", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Modbot.Core.Discord.DiscordBotState.Connecting, "Connecting")]
    [InlineData(Modbot.Core.Discord.DiscordBotState.Connected, "Connected")]
    [InlineData(Modbot.Core.Discord.DiscordBotState.Disconnected, "Disconnected")]
    [InlineData(Modbot.Core.Discord.DiscordBotState.Failed, "Failed")]
    public void EveryBotStateHasTheNameTheScreenLooksUp(
        Modbot.Core.Discord.DiscordBotState state, string expected)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(state, Web);

        Assert.Equal($"\"{expected}\"", json);
    }

    [Fact]
    public void TheGateStatusIsWrittenAsItsName()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(GateStatus.NotConfigured, Web);

        Assert.Equal("\"NotConfigured\"", json);
    }
}
