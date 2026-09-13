using Modbot.Api.Features.Analytics.Worlds;
using Modbot.Api.Tests.Features.Audit;
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

        var t = host.Clock.UtcNow.AddHours(-6);

        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_a", t, "wrld_a", "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstancePresenceObserved, "usr_b", t.AddMinutes(10), "wrld_a", "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_a", t.AddMinutes(30), "wrld_a", "1"), ct);

        // Opened but nobody with the client went in: known from the audit log alone.
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceCreated, "wrld_b:2", t, actor: "usr_mod", worldId: "wrld_b", instanceId: "2"), ct);
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
}
