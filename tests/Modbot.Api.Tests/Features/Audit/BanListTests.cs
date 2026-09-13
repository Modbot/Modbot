using System.Net;
using System.Text.Json.Nodes;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Audit;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Audit;

/// <summary>
/// The ban list, and the coverage that stops it being read as the group's ban list.
/// </summary>
/// <remarks>
/// The coverage assertions are the ones that matter. A list of real rows carries no signal that
/// it is partial, and the failure mode is a moderator concluding from an absence that somebody is
/// not banned — so the window travels with the data and is tested like data.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class BanListTests
{
    private readonly PostgresFixture _db;

    public BanListTests(PostgresFixture db) => _db = db;

    private static readonly DateTimeOffset Day = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static FactRecord Fact(string type, string subject, DateTimeOffset at) => new()
    {
        Type = type,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = subject,
        ActorPlatform = FactPlatform.VRChat,
        ActorId = "usr_mod",
        Source = FactSource.AuditLog,
        Data = new JsonObject { ["actorDisplayName"] = "RedZu" },
    };

    [Fact]
    public async Task ABannedSubject_IsListedAsBanned()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        await host.WriteFactAsync(Fact(FactType.MemberBanned, "usr_a", Day), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var list = await host.GetJsonAsync<BanListResponse>("/api/audit/bans", cookie, ct);

        var entry = Assert.Single(list.Bans);

        Assert.Equal("Banned", entry.Status);
        Assert.Equal("usr_a", entry.SubjectId);
        Assert.Equal("RedZu", entry.ActorName);
    }

    [Fact]
    public async Task ALaterUnban_FlipsTheStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        await host.WriteFactAsync(Fact(FactType.MemberBanned, "usr_a", Day), ct);
        await host.WriteFactAsync(Fact(FactType.MemberUnbanned, "usr_a", Day.AddHours(1)), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var list = await host.GetJsonAsync<BanListResponse>("/api/audit/bans", cookie, ct);

        var entry = Assert.Single(list.Bans);

        Assert.Equal("Unbanned", entry.Status);
        Assert.Equal(Day.AddHours(1), entry.UnbannedAt);
    }

    [Fact]
    public async Task ARebanAfterAnUnban_IsBannedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        await host.WriteFactAsync(Fact(FactType.MemberBanned, "usr_a", Day), ct);
        await host.WriteFactAsync(Fact(FactType.MemberUnbanned, "usr_a", Day.AddHours(1)), ct);
        await host.WriteFactAsync(Fact(FactType.MemberBanned, "usr_a", Day.AddHours(2)), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var list = await host.GetJsonAsync<BanListResponse>("/api/audit/bans", cookie, ct);

        Assert.Equal("Banned", Assert.Single(list.Bans).Status);
    }

    [Fact]
    public async Task AnUnbanWithNoRecordedBan_IsStillListed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        // Somebody banned before Modbot's coverage window and unbanned inside it. Dropping them
        // would hide the clearest available evidence that bans exist which Modbot never saw.
        await host.WriteFactAsync(Fact(FactType.MemberUnbanned, "usr_old", Day), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var list = await host.GetJsonAsync<BanListResponse>("/api/audit/bans", cookie, ct);

        var entry = Assert.Single(list.Bans);

        Assert.Equal("Unbanned", entry.Status);
        Assert.Null(entry.BannedAt);
    }

    [Fact]
    public async Task ExcludingUnbanned_LeavesOnlyLiveBans()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        await host.WriteFactAsync(Fact(FactType.MemberBanned, "usr_a", Day), ct);
        await host.WriteFactAsync(Fact(FactType.MemberBanned, "usr_b", Day), ct);
        await host.WriteFactAsync(Fact(FactType.MemberUnbanned, "usr_b", Day.AddHours(1)), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var list = await host.GetJsonAsync<BanListResponse>(
            "/api/audit/bans?includeUnbanned=false", cookie, ct);

        Assert.Equal(["usr_a"], list.Bans.Select(b => b.SubjectId));
    }

    [Fact]
    public async Task Coverage_SaysHowFarBackTheListReaches()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        await host.WriteFactAsync(Fact(FactType.MemberBanned, "usr_a", Day), ct);
        await host.WriteFactAsync(Fact(FactType.MemberBanned, "usr_b", Day.AddDays(3)), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var list = await host.GetJsonAsync<BanListResponse>("/api/audit/bans", cookie, ct);

        Assert.Equal(Day, list.Coverage.EarliestRecord);
        Assert.Equal(Day.AddDays(3), list.Coverage.LatestRecord);
        Assert.Equal(host.Clock.UtcNow, list.Coverage.FirstSyncedAt);
        Assert.Equal(2, list.Coverage.BannedCount);

        // Until the backfill is done the window is still growing backwards, so the list is not
        // yet at its full extent -- a distinction the screen has to be able to state.
        Assert.False(list.Coverage.BackfillComplete);
    }

    [Fact]
    public async Task AnEmptyLog_ReportsAnEmptyWindow_NotZeroBans()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var list = await host.GetJsonAsync<BanListResponse>("/api/audit/bans", cookie, ct);

        Assert.Empty(list.Bans);
        Assert.Null(list.Coverage.EarliestRecord);
        Assert.Null(list.Coverage.FirstSyncedAt);
    }

    [Fact]
    public async Task ViewOperationalLogAlone_CannotReadTheBanList()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, ct);
        var response = await host.GetAsync("/api/audit/bans", cookie, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
