using Modbot.Api.Features.Analytics.Instances;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Analytics;

[Collection(nameof(PostgresCollection))]
public class InstancesAnalyticsTests
{
    private const string World = "wrld_a";

    private readonly PostgresFixture _db;

    public InstancesAnalyticsTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task OpenedAndClosed_ComeFromTheDailyTotals()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddDays(-1);

        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceCreated, $"{World}:1", t, actor: "usr_mod", worldId: World, instanceId: "1"), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceCreated, $"{World}:2", t.AddHours(1), actor: "usr_mod", worldId: World, instanceId: "2"), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceClosed, $"{World}:1", t.AddHours(2), actor: "usr_mod", worldId: World, instanceId: "1"), ct);
        await host.RebuildDailyTotalsAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<InstancesAnalytics>("/api/analytics/instances?days=30", cookie, ct);

        Assert.Equal(2m, page.Opened.Sum(p => p.Value));
        Assert.Equal(1m, page.Closed.Sum(p => p.Value));
    }

    /// <summary>
    /// One instance with both ends, one that was last seen in a kick and never closed, one that
    /// was opened and never heard of again. Two of them overlapped.
    /// </summary>
    [Fact]
    public async Task Lifetimes_UseTheCloseWhenThereIsOne_AndTheLastThingSeenOtherwise()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-12);

        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceCreated, $"{World}:1", t, actor: "usr_mod", worldId: World, instanceId: "1"), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceClosed, $"{World}:1", t.AddMinutes(60), actor: "usr_mod", worldId: World, instanceId: "1"), ct);

        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceCreated, $"{World}:2", t.AddMinutes(30), actor: "usr_mod", worldId: World, instanceId: "2"), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceKick, "usr_x", t.AddMinutes(90), actor: "usr_mod", worldId: World, instanceId: "2"), ct);

        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceCreated, $"{World}:3", t.AddMinutes(120), actor: "usr_mod", worldId: World, instanceId: "3"), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<InstancesAnalytics>("/api/analytics/instances?days=7", cookie, ct);

        Assert.Equal(3, page.InstancesOpened);
        Assert.Equal(1, page.InstancesWithBothEnds);
        Assert.Equal(60m, page.TypicalMinutesOpen);

        // Instances 1 and 2 overlapped between t+30 and t+60; instance 3 opened alone.
        Assert.Equal(2m, page.MostOpenAtOnce.Max(p => p.Value));
    }

    [Fact]
    public async Task MostPeopleInOne_IsTheLargestKnownPopulation_PerDay()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-6);

        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_a", t, World, "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_b", t.AddMinutes(1), World, "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstancePresenceObserved, "usr_c", t.AddMinutes(2), World, "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_a", t.AddMinutes(3), World, "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_d", t.AddMinutes(4), World, "1"), ct);

        // A second instance with one person does not add to the first's count.
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_e", t.AddMinutes(5), World, "2"), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<InstancesAnalytics>("/api/analytics/instances?days=7", cookie, ct);

        var day = Assert.Single(page.MostPeopleInOne);
        Assert.Equal(3m, day.Value);
    }

    [Fact]
    public async Task HourOfWeek_BucketsArrivalsAndOpenings_InUtc()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        host.Clock.UtcNow = new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero);

        // Tuesday 16 June 2026, 20:15 UTC -> (2 - 1) * 24 + 20 = 44.
        var tuesdayEvening = new DateTimeOffset(2026, 6, 16, 20, 15, 0, TimeSpan.Zero);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceCreated, $"{World}:1", tuesdayEvening, actor: "usr_mod", worldId: World, instanceId: "1"), ct);

        // Monday 15 June 2026, 00:30 UTC -> bucket 0.
        var mondayNight = new DateTimeOffset(2026, 6, 15, 0, 30, 0, TimeSpan.Zero);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_a", mondayNight, World, "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstancePresenceObserved, "usr_b", mondayNight.AddMinutes(5), World, "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_a", mondayNight.AddMinutes(10), World, "1"), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<InstancesAnalytics>("/api/analytics/instances?days=7", cookie, ct);

        Assert.Equal(168, page.HourOfWeek.Arrivals.Count);
        Assert.Equal(168, page.HourOfWeek.Opened.Count);
        Assert.Equal(1m, page.HourOfWeek.Opened[44]);
        Assert.Equal(2m, page.HourOfWeek.Arrivals[0]);
        Assert.Equal(2m, page.HourOfWeek.Arrivals.Sum());
        Assert.Equal(1m, page.HourOfWeek.Opened.Sum());
    }

    [Fact]
    public async Task WithNothingRecorded_ThePageStillAnswers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<InstancesAnalytics>("/api/analytics/instances?days=7", cookie, ct);

        Assert.Empty(page.Opened);
        Assert.Empty(page.MostOpenAtOnce);
        Assert.Null(page.TypicalMinutesOpen);
        Assert.Equal(0m, page.HourOfWeek.Arrivals.Sum());
    }
}
