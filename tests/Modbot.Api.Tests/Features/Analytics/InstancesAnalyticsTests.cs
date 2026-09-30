using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Analytics;
using Modbot.Api.Features.Analytics.Instances;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Api.Tests.Features.Places;
using Modbot.Core.Data;
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
    /// was opened and never heard of again. Two of them overlapped. How many were open at once still
    /// comes from the audit log; how long they stayed open does not (see the tests below).
    /// </summary>
    [Fact]
    public async Task MostOpenAtOnce_UsesTheCloseWhenThereIsOne_AndTheLastThingSeenOtherwise()
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

        // Instances 1 and 2 overlapped between t+30 and t+60; instance 3 opened alone.
        Assert.Equal(2m, page.MostOpenAtOnce.Max(p => p.Value));
    }

    /// <summary>
    /// The user's own install: instances that all emptied out and dropped off the group's list, and
    /// no close entry in the audit log. The typical time open used to be "—" beside a table listing
    /// them with their lengths. It now counts every instance that ended, however it ended, while
    /// "Closed" still counts only what a moderator closed by hand.
    /// </summary>
    [Fact]
    public async Task TypicalTimeOpen_CountsEveryEndedInstance_HoweverItEnded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await ManagedGroupAsync(host, ct);

        host.Clock.UtcNow = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        var evening = new DateTimeOffset(2026, 9, 25, 0, 20, 0, TimeSpan.Zero);

        // Dropped off the list after 30 minutes.
        await PlacesFixtures.InstanceAsync(host, World, "1", evening, evening.AddMinutes(30), evening.AddMinutes(30), ct);

        // Closed by a moderator after 90 minutes: the list dropped it too, and the audit log says who.
        var closedByHand = evening.AddMinutes(40);
        await PlacesFixtures.InstanceAsync(host, World, "2", closedByHand, closedByHand.AddMinutes(90), closedByHand.AddMinutes(90), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceClosed, $"{World}:2", closedByHand.AddMinutes(90).AddSeconds(-20), actor: "usr_mod", worldId: World, instanceId: "2"), ct);

        // Went quiet after an hour, two days earlier.
        var earlier = evening.AddDays(-2);
        var quiet = await PlacesFixtures.InstanceAsync(host, World, "3", earlier, earlier.AddMinutes(60), earlier.AddMinutes(60), ct);
        await SetClosedByAsync(host, quiet.Id, "time", ct);

        // Still open, and another group's instance: neither counts.
        await PlacesFixtures.InstanceAsync(host, World, "4", evening, host.Clock.UtcNow, null, ct);
        var other = await PlacesFixtures.InstanceAsync(host, World, "5", evening, evening.AddMinutes(500), evening.AddMinutes(500), ct);
        await SetGroupAsync(host, other.Id, "grp_other", ct);

        await host.RebuildDailyTotalsAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<InstancesAnalytics>("/api/analytics/instances?days=7", cookie, ct);

        Assert.Equal(3, page.InstancesWithBothEnds);
        Assert.Equal(60m, page.TypicalMinutesOpen);

        // Per day an instance ended, and no row at all for a day nothing ended: not nought.
        Assert.Equal(
            [new DayValue(new DateOnly(2026, 9, 23), 60m), new DayValue(new DateOnly(2026, 9, 25), 60m)],
            page.TypicalMinutesOpenPerDay);

        // "Closed" is still a moderator's close, and only that.
        Assert.Equal(1m, page.Closed.Sum(p => p.Value));
    }

    [Fact]
    public void TypicalTimeOpenPerDay_IsTheMedianOfTheInstancesThatEndedThatDay()
    {
        var day = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);

        var perDay = InstancesAnalyticsQuery.TypicalMinutesOpenPerDay(
        [
            new(day.AddHours(1), day.AddHours(1).AddMinutes(20)),
            new(day.AddHours(2), day.AddHours(2).AddMinutes(40)),

            // Ran past midnight: it lands on the day it finished.
            new(day.AddHours(23), day.AddHours(25)),

            // A clock that disagrees gives nought, never a negative length. Its end is a few hours
            // into the day, so it lands on that day, not on the one before midnight.
            new(day.AddDays(2).AddHours(3), day.AddDays(2).AddHours(3).AddMinutes(-5)),
        ]);

        Assert.Equal(
            [
                new DayValue(new DateOnly(2026, 9, 25), 30m),
                new DayValue(new DateOnly(2026, 9, 26), 120m),
                new DayValue(new DateOnly(2026, 9, 27), 0m),
            ],
            perDay);
    }

    /// <summary>
    /// The table said "closed" for every ended instance under a tile that said none were closed. A
    /// row is closed by a moderator only when the audit log has a close entry for that instance, in
    /// its own lifetime: a later instance that got the same number does not lend it its close.
    /// </summary>
    [Fact]
    public async Task RecentRows_AreClosedByAModerator_OnlyWithACloseEntryForThatInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await ManagedGroupAsync(host, ct);

        var t = host.Clock.UtcNow.AddHours(-10);

        var ended = await PlacesFixtures.InstanceAsync(host, World, "7", t, t.AddMinutes(30), t.AddMinutes(30), ct);
        var closed = await PlacesFixtures.InstanceAsync(host, World, "8", t, t.AddMinutes(45), t.AddMinutes(45), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceClosed, $"{World}:8", t.AddMinutes(44), actor: "usr_mod", worldId: World, instanceId: "8"), ct);

        // The number 7 handed out again later, and that one closed by hand. The first 7 still ended.
        var again = await PlacesFixtures.InstanceAsync(host, World, "7", t.AddHours(3), t.AddHours(4), t.AddHours(4), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceClosed, $"{World}:7", t.AddHours(4).AddSeconds(-10), actor: "usr_mod", worldId: World, instanceId: "7"), ct);

        var open = await PlacesFixtures.InstanceAsync(host, World, "9", t, host.Clock.UtcNow, null, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<InstancesAnalytics>("/api/analytics/instances?days=7", cookie, ct);

        var byId = page.Recent.ToDictionary(r => r.Id);
        Assert.False(byId[ended.Id].ClosedByModerator);
        Assert.True(byId[closed.Id].ClosedByModerator);
        Assert.True(byId[again.Id].ClosedByModerator);
        Assert.False(byId[open.Id].ClosedByModerator);
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

    /// <summary>The group whose instances are the group's own.</summary>
    private static async Task ManagedGroupAsync(ReadSurfaceTestHost host, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await db.GetSettingsAsync(ct);
        settings.ManagedGroupId = "grp_1";
        await db.SaveChangesAsync(ct);
    }

    private static async Task SetClosedByAsync(ReadSurfaceTestHost host, Guid id, string closedBy, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var instance = await db.VRChatInstances.FindAsync([id], ct);
        instance!.ClosedBy = closedBy;
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
}
