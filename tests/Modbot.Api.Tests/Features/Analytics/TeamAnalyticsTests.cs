using Modbot.Analytics.DailyTotals;
using Modbot.Api.Features.Analytics.Team;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Api.Tests.Features.Places;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Analytics;

[Collection(nameof(PostgresCollection))]
public class TeamAnalyticsTests
{
    private const string World = "wrld_a";
    private const string Instance = "12345";

    private readonly PostgresFixture _db;

    public TeamAnalyticsTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task Moderators_AreTotalledAndBrokenDownByKind_FromTheDailyTotals()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var day = host.Clock.UtcNow.AddDays(-1);

        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_1", day, actor: "usr_alice", actorName: "Alice"), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_2", day.AddMinutes(1), actor: "usr_alice", actorName: "Alice"), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceKick, "usr_3", day.AddMinutes(2), actor: "usr_alice", actorName: "Alice", worldId: World, instanceId: Instance), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceWarn, "usr_4", day.AddMinutes(3), actor: "usr_bob", actorName: "Bob", worldId: World, instanceId: Instance), ct);
        await host.RebuildDailyTotalsAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=30", cookie, ct);

        Assert.Equal(2, page.Moderators.Count);

        var alice = page.Moderators[0];
        Assert.Equal("usr_alice", alice.Who.Id);
        Assert.Equal("Alice", alice.Who.Name);
        Assert.Equal(3m, alice.Total);
        Assert.Equal(2m, alice.ByKind[DailyTotalMetrics.ModeratorBans]);
        Assert.Equal(1m, alice.ByKind[DailyTotalMetrics.ModeratorInstanceKicks]);
        Assert.Equal(DateOnly.FromDateTime(day.UtcDateTime), alice.LastActiveDay);

        Assert.Equal(4m, page.ActionsPerDay.Sum(p => p.Value));
        Assert.Equal(1m, page.ActionsPerDayByKind.Single(k => k.Metric == DailyTotalMetrics.ModeratorWarns).Total);
        Assert.Equal(TeamAnalyticsQuery.Kinds.Count, page.Kinds.Count);
    }

    /// <summary>
    /// The hand-built scenario: a busy instance, a moderator present, the moderator leaves, and
    /// the gap begins with the people who were still there. It ends when the moderator is back.
    /// </summary>
    [Fact]
    public async Task CoverageGap_BeginsWhenTheLastModeratorLeavesPeopleBehind_AndEndsWhenOneReturns()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-6);

        // The moderator is recognised by having taken a moderation action.
        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_someone", t.AddDays(-10), actor: "usr_mod", actorName: "Mod"), ct);

        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_mod", t, World, Instance, name: "Mod"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_p1", t.AddMinutes(1), World, Instance), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstancePresenceObserved, "usr_p2", t.AddMinutes(2), World, Instance), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_p3", t.AddMinutes(3), World, Instance), ct);

        // A second report of somebody already here must not count as a second person.
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_p1", t.AddMinutes(4), World, Instance), ct);

        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_mod", t.AddMinutes(30), World, Instance), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_mod", t.AddMinutes(60), World, Instance), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=30", cookie, ct);

        var gap = Assert.Single(page.CoverageGaps);
        Assert.Equal(World, gap.WorldId);
        Assert.Equal(Instance, gap.InstanceId);
        Assert.Equal(t.AddMinutes(30), gap.StartedAt);
        Assert.Equal(t.AddMinutes(60), gap.EndedAt);
        Assert.Equal("moderator-arrived", gap.EndedBy);
        Assert.Equal(3, gap.PeopleWhenLastModeratorLeft);
        Assert.Equal("usr_mod", gap.LastModerator?.Id);
        Assert.Equal("Mod", gap.LastModerator?.Name);

        Assert.Equal(1, page.InstancesWatched);
        Assert.Equal(1, page.ModeratorsRecognised);
    }

    /// <summary>
    /// A gap names its world and the instance it happened in, so the page can show "The Black Cat
    /// #12345" and open the instance rather than print a world id. The number was used before by
    /// an instance that had closed; the gap belongs to the one that was open at the time.
    /// </summary>
    [Fact]
    public async Task CoverageGap_CarriesTheWorldName_AndTheInstanceItHappenedIn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-6);

        await PlacesFixtures.WorldAsync(host, World, "The Black Cat", t.AddDays(-3), ct);
        await PlacesFixtures.InstanceAsync(host, World, Instance, t.AddDays(-2), t.AddDays(-2).AddHours(1), t.AddDays(-2).AddHours(1), ct, name: "Last week");
        var tonight = await PlacesFixtures.InstanceAsync(host, World, Instance, t.AddMinutes(-5), host.Clock.UtcNow, null, ct, name: "Movie night");

        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_someone", t.AddDays(-10), actor: "usr_mod"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_mod", t, World, Instance), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_p1", t.AddMinutes(1), World, Instance), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_mod", t.AddMinutes(30), World, Instance), ct);

        // A second world Modbot has only seen as an id, with no instance row: nothing to name.
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_mod", t.AddHours(2), "wrld_unread", "7"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_p2", t.AddHours(2).AddMinutes(1), "wrld_unread", "7"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_mod", t.AddHours(2).AddMinutes(10), "wrld_unread", "7"), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=30", cookie, ct);

        Assert.Equal(2, page.CoverageGaps.Count);

        var named = page.CoverageGaps.Single(g => g.WorldId == World);
        Assert.Equal("The Black Cat", named.WorldName);
        Assert.Equal(tonight.Id, named.ModbotInstanceId);
        Assert.Equal("Movie night", named.InstanceName);

        var unnamed = page.CoverageGaps.Single(g => g.WorldId == "wrld_unread");
        Assert.Null(unnamed.WorldName);
        Assert.Null(unnamed.ModbotInstanceId);
        Assert.Null(unnamed.InstanceName);
    }

    [Fact]
    public async Task CoverageGap_EndsWhenTheInstanceCloses_AndIsUnknownWhenNothingMoreIsSeen()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-6);

        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_someone", t.AddDays(-10), actor: "usr_mod"), ct);

        // Instance one: the moderator leaves, then the instance is closed from outside.
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceCreated, $"{World}:1", t.AddMinutes(-5), actor: "usr_mod", worldId: World, instanceId: "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_mod", t, World, "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_p1", t.AddMinutes(1), World, "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_mod", t.AddMinutes(30), World, "1"), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceClosed, $"{World}:1", t.AddMinutes(45), actor: "usr_mod", worldId: World, instanceId: "1"), ct);

        // Instance two: the moderator leaves and nothing is ever heard again.
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_mod", t.AddHours(2), World, "2"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_p2", t.AddHours(2).AddMinutes(1), World, "2"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_mod", t.AddHours(2).AddMinutes(10), World, "2"), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=30", cookie, ct);

        Assert.Equal(2, page.CoverageGaps.Count);

        var closed = page.CoverageGaps.Single(g => g.InstanceId == "1");
        Assert.Equal(t.AddMinutes(45), closed.EndedAt);
        Assert.Equal("instance-closed", closed.EndedBy);

        var open = page.CoverageGaps.Single(g => g.InstanceId == "2");
        Assert.Null(open.EndedAt);
        Assert.Equal("unknown", open.EndedBy);
        Assert.Equal(1, open.PeopleWhenLastModeratorLeft);
    }

    [Fact]
    public async Task NoGap_WhenTheModeratorIsTheLastToLeave()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-6);

        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_someone", t.AddDays(-10), actor: "usr_mod"), ct);

        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_mod", t, World, Instance), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_p1", t.AddMinutes(1), World, Instance), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_p1", t.AddMinutes(20), World, Instance), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_mod", t.AddMinutes(30), World, Instance), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=30", cookie, ct);

        Assert.Empty(page.CoverageGaps);
    }

    /// <summary>
    /// Every presence report comes from a moderator's own client, so a client reporting is cover
    /// even when the roster does not know the person running it. When that client's owner
    /// leaves, the gap still begins -- and is attributed to them.
    /// </summary>
    [Fact]
    public async Task AReportingClient_CountsAsCover_EvenWhenItsOwnerIsNotOnTheRoster()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-6);

        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_staff", t, World, Instance, device: "device-9", name: "Staff"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_p1", t.AddMinutes(1), World, Instance, device: "device-9"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_p2", t.AddMinutes(2), World, Instance, device: "device-9"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_staff", t.AddMinutes(30), World, Instance, device: "device-9"), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=30", cookie, ct);

        var gap = Assert.Single(page.CoverageGaps);
        Assert.Equal(t.AddMinutes(30), gap.StartedAt);
        Assert.Equal(2, gap.PeopleWhenLastModeratorLeft);
        Assert.Equal("usr_staff", gap.LastModerator?.Id);
        Assert.Equal("Staff", gap.LastModerator?.Name);
    }

    [Fact]
    public async Task InstancesNoClientEverReportedFrom_AreCountedSeparately()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-6);

        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceCreated, $"{World}:1", t, actor: "usr_mod", worldId: World, instanceId: "1"), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceCreated, $"{World}:2", t, actor: "usr_mod", worldId: World, instanceId: "2"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_mod", t.AddMinutes(1), World, "1"), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=30", cookie, ct);

        Assert.Equal(1, page.InstancesWatched);
        Assert.Equal(1, page.InstancesOpenedWithoutAnyWatch);
    }

    [Fact]
    public async Task Roster_IncludesModerationRoleHolders_AndTheOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var now = host.Clock.UtcNow;

        await StoreGroupSnapshotAsync(host, "usr_owner",
        [
            Role("grol_mod", "Moderator", "group-instance-moderate"),
            Role("grol_member", "Member"),
        ], now, ct);

        System.Text.Json.Nodes.JsonObject RoleData(string id) => new() { ["roleId"] = id, ["roleName"] = id };

        await host.WriteFactAsync(AuditFact(FactType.RoleGranted, "usr_guard", now.AddDays(-5), actor: "usr_owner", extra: RoleData("grol_mod")), ct);
        await host.WriteFactAsync(AuditFact(FactType.RoleGranted, "usr_former", now.AddDays(-5), actor: "usr_owner", extra: RoleData("grol_mod")), ct);
        await host.WriteFactAsync(AuditFact(FactType.RoleRevoked, "usr_former", now.AddDays(-4), actor: "usr_owner", extra: RoleData("grol_mod")), ct);
        await host.WriteFactAsync(AuditFact(FactType.RoleGranted, "usr_plain", now.AddDays(-5), actor: "usr_owner", extra: RoleData("grol_member")), ct);

        // usr_guard holds a moderation role but has never acted; their presence still covers.
        var t = now.AddHours(-3);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_guard", t, World, Instance, device: "device-2"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_p1", t.AddMinutes(1), World, Instance, device: "device-2"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_guard", t.AddMinutes(10), World, Instance, device: "device-2"), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=30", cookie, ct);

        // owner + usr_guard + usr_owner-as-actor (same person) = 2 distinct; usr_former and usr_plain are not moderators.
        Assert.Equal(2, page.ModeratorsRecognised);

        var gap = Assert.Single(page.CoverageGaps);
        Assert.Equal("usr_guard", gap.LastModerator?.Id);
    }
}
