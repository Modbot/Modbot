using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Analytics.Worlds;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Api.Tests.Features.Places;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Analytics;

[Collection(nameof(PostgresCollection))]
public class WorldsAnalyticsTests
{
    private readonly PostgresFixture _db;

    public WorldsAnalyticsTests(PostgresFixture db) => _db = db;

    /// <summary>
    /// A person's time is from when a client first saw them to when it saw them leave; with no
    /// leave, to the last report from that instance. Never beyond what somebody was watching.
    /// </summary>
    [Fact]
    public async Task TimeSeen_IsSummedPerWorld_FromPresenceSessions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await ManagedGroupAsync(host, ct);

        var t = host.Clock.UtcNow.AddHours(-6);

        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_a", t, "wrld_a", "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstancePresenceObserved, "usr_b", t.AddMinutes(10), "wrld_a", "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_a", t.AddMinutes(30), "wrld_a", "1"), ct);

        // Opened but nobody with the client went in: known from Modbot's instance list alone.
        await PlacesFixtures.InstanceAsync(host, "wrld_b", "2", t, t.AddMinutes(20), t.AddMinutes(20), ct);
        await host.RebuildDailyTotalsAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<WorldsAnalytics>("/api/analytics/worlds?days=30", cookie, ct);

        Assert.Equal(["wrld_a", "wrld_b"], page.Worlds.Select(w => w.WorldId));

        var a = page.Worlds[0];
        Assert.Equal(50m, a.MinutesSeen);      // 30 for usr_a, 20 for usr_b up to the last report
        Assert.Equal(2, a.Visitors);
        Assert.Equal(2, a.Visits);
        Assert.Equal(t.AddMinutes(30), a.LastSeenAt);

        var b = page.Worlds[1];
        Assert.Equal(0m, b.MinutesSeen);
        Assert.Equal(1m, b.InstancesOpened);

        Assert.Equal(3, page.PresenceReports);
    }

    /// <summary>
    /// The column said four beside a popup that listed two: it counted the audit log's create
    /// entries, and a world's instances that Modbot saw open were fewer. It counts the same rows the
    /// popup lists, whatever the audit log says.
    /// </summary>
    [Fact]
    public async Task InstancesOpened_CountsTheInstanceList_NotTheAuditLogsCreateEntries()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await ManagedGroupAsync(host, ct);

        var t = host.Clock.UtcNow.AddHours(-8);

        await PlacesFixtures.InstanceAsync(host, "wrld_home", "1", t, t.AddMinutes(30), t.AddMinutes(30), ct);
        await PlacesFixtures.InstanceAsync(host, "wrld_home", "2", t.AddHours(1), t.AddHours(2), t.AddHours(2), ct);

        for (var number = 1; number <= 4; number++)
        {
            await host.WriteFactAsync(
                AuditFact(FactType.GroupInstanceCreated, $"wrld_home:{number}", t.AddMinutes(number), actor: "usr_mod", worldId: "wrld_home", instanceId: number.ToString()), ct);
        }

        await host.RebuildDailyTotalsAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<WorldsAnalytics>("/api/analytics/worlds?days=30", cookie, ct);

        var home = Assert.Single(page.Worlds);
        Assert.Equal(2, home.Instances);
        Assert.Equal(2m, home.InstancesOpened);
    }

    [Fact]
    public async Task VisitorsPerDay_ComeFromTheDailyTotals_ForTheBusiestWorlds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddDays(-1);

        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_a", t, "wrld_a", "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_b", t.AddMinutes(1), "wrld_a", "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_a", t.AddMinutes(2), "wrld_a", "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_c", t.AddMinutes(3), "wrld_b", "7"), ct);
        await host.RebuildDailyTotalsAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<WorldsAnalytics>("/api/analytics/worlds?days=30", cookie, ct);

        Assert.Equal(["wrld_a", "wrld_b"], page.VisitorsPerDay.Select(s => s.WorldId));
        Assert.Equal(2m, Assert.Single(page.VisitorsPerDay[0].Points).Value);
        Assert.Equal(1m, Assert.Single(page.VisitorsPerDay[1].Points).Value);
    }

    /// <summary>
    /// A world nobody from the team went into still shows how long it was open and how full it got,
    /// and ranks above a world a companion saw one person in for longer than it saw anybody.
    /// </summary>
    [Fact]
    public async Task InstancesTimeOpenAndMostAtOnce_ComeFromTheInstances_AndDecideTheOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await ManagedGroupAsync(host, ct);

        var t = host.Clock.UtcNow.AddHours(-10);

        // Karaoke: one instance, four hours, twelve people, no companion.
        var karaoke = await PlacesFixtures.InstanceAsync(host, "wrld_karaoke", "1", t, t.AddHours(4), t.AddHours(4), ct);
        await SetPeakAsync(host, karaoke.Id, 12, ct);

        // Black Cat: two short instances, one person, and a companion there.
        await PlacesFixtures.InstanceAsync(host, "wrld_cat", "2", t, t.AddMinutes(13), t.AddMinutes(13), ct);
        await PlacesFixtures.InstanceAsync(host, "wrld_cat", "3", t.AddMinutes(13), t.AddMinutes(14), t.AddMinutes(14), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_a", t, "wrld_cat", "2"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_a", t.AddMinutes(13), "wrld_cat", "2"), ct);

        // Somebody else's instance of the same world: not ours.
        var other = await PlacesFixtures.InstanceAsync(host, "wrld_cat", "4", t, t.AddHours(9), t.AddHours(9), ct);
        await SetGroupAsync(host, other.Id, "grp_other", ct);

        await host.RebuildDailyTotalsAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<WorldsAnalytics>("/api/analytics/worlds?days=30", cookie, ct);

        Assert.Equal(["wrld_karaoke", "wrld_cat"], page.Worlds.Select(w => w.WorldId));

        var k = page.Worlds[0];
        Assert.Equal(1, k.Instances);
        Assert.Equal(240m, k.MinutesOpen);
        Assert.Equal(12, k.MostAtOnce);
        Assert.Equal(t, k.LastOpenedAt);
        Assert.Equal(0, k.Visitors);

        var c = page.Worlds[1];
        Assert.Equal(2, c.Instances);
        Assert.Equal(14m, c.MinutesOpen);
        Assert.Equal(7, c.MostAtOnce);
        Assert.Equal(t.AddMinutes(13), c.LastOpenedAt);
        Assert.Equal(1, c.Visitors);
    }

    [Fact]
    public async Task WithNoPresenceReports_ThePageStillAnswers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<WorldsAnalytics>("/api/analytics/worlds?days=7", cookie, ct);

        Assert.Empty(page.Worlds);
        Assert.Empty(page.VisitorsPerDay);
        Assert.Equal(0, page.PresenceReports);
    }

    private static async Task ManagedGroupAsync(ReadSurfaceTestHost host, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await db.GetSettingsAsync(ct);
        settings.ManagedGroupId = "grp_1";
        await db.SaveChangesAsync(ct);
    }

    private static async Task SetGroupAsync(ReadSurfaceTestHost host, Guid id, string group, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var instance = await db.VRChatInstances.FindAsync([id], ct);
        instance!.GroupId = group;
        await db.SaveChangesAsync(ct);
    }

    private static async Task SetPeakAsync(ReadSurfaceTestHost host, Guid id, int peak, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var instance = await db.VRChatInstances.FindAsync([id], ct);
        instance!.PeakUserCount = peak;
        await db.SaveChangesAsync(ct);
    }
}
