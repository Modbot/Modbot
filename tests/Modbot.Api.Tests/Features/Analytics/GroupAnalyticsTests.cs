using System.Text.Json.Nodes;
using Modbot.Api.Features.Analytics.Group;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Analytics;

[Collection(nameof(PostgresCollection))]
public class GroupAnalyticsTests
{
    private readonly PostgresFixture _db;

    public GroupAnalyticsTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task DailySeries_ComeFromTheDailyTotals()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var day = host.Clock.UtcNow.AddDays(-2);

        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_a", day), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_b", day), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberLeft, "usr_c", day), ct);
        await host.WriteFactAsync(AuditFact(FactType.InviteCreated, "usr_d", day, actor: "usr_mod"), ct);
        await host.WriteFactAsync(AuditFact(FactType.JoinRequestCreated, "usr_e", day), ct);
        await host.RebuildDailyTotalsAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<GroupAnalytics>("/api/analytics/group?days=30", cookie, ct);

        Assert.Equal(2m, page.Joined.Sum(p => p.Value));
        Assert.Equal(1m, page.Left.Sum(p => p.Value));
        Assert.Equal(1m, page.InvitesSent.Sum(p => p.Value));
        Assert.Equal(1m, page.RequestsReceived.Sum(p => p.Value));
        Assert.Equal(1m, page.NetChange.Last().Value);
        Assert.NotNull(page.Coverage.DailyTotalsUpdatedAt);
    }

    [Fact]
    public async Task MemberCount_IsTheObservedHeadcount_FromGroupInfoFacts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var today = host.Clock.UtcNow;

        await host.WriteFactAsync(GroupInfoBaseline(14208, today.AddDays(-3)), ct);
        await host.WriteFactAsync(GroupInfoChange(14208, 14230, today.AddDays(-1)), ct);

        // Two observations on the same day: the later one is the day's value.
        await host.WriteFactAsync(GroupInfoChange(14230, 14244, today.AddDays(-1).AddHours(3)), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<GroupAnalytics>("/api/analytics/group?days=30", cookie, ct);

        Assert.Equal([14208m, 14244m], page.MemberCount.Select(p => p.Value));
    }

    /// <summary>
    /// Only members whose join is on record have a tenure, and somebody who has since left is
    /// not a current member however long they were one.
    /// </summary>
    [Fact]
    public async Task Tenure_BucketsCurrentMembersByRecordedJoin()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var now = host.Clock.UtcNow;

        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_new", now.AddDays(-3)), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_settled", now.AddDays(-40)), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_gone", now.AddDays(-10)), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberLeft, "usr_gone", now.AddDays(-2)), ct);

        // Left and came back: the current stretch is what counts.
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_back", now.AddDays(-400)), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_back", now.AddDays(-300), actor: "usr_mod"), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_back", now.AddDays(-20)), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<GroupAnalytics>("/api/analytics/group?days=7", cookie, ct);

        int Members(string label) => page.Tenure.Single(b => b.Label == label).Members;

        Assert.Equal(3, page.MembersWithKnownTenure);
        Assert.Equal(1, Members("Under a week"));
        Assert.Equal(1, Members("1 to 4 weeks"));
        Assert.Equal(1, Members("1 to 3 months"));
        Assert.Equal(0, Members("Over a year"));
    }

    [Fact]
    public async Task Invites_AreCountedAsFollowedOnlyWhenThatPersonJoinsWithinAWeek()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var now = host.Clock.UtcNow;

        await host.WriteFactAsync(AuditFact(FactType.InviteCreated, "usr_x", now.AddDays(-5), actor: "usr_mod"), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_x", now.AddDays(-4)), ct);

        await host.WriteFactAsync(AuditFact(FactType.InviteCreated, "usr_y", now.AddDays(-5), actor: "usr_mod"), ct);

        await host.WriteFactAsync(AuditFact(FactType.InviteCreated, "usr_z", now.AddDays(-20), actor: "usr_mod"), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_z", now.AddDays(-2)), ct);

        // Somebody else joining does not make usr_y's invite a success.
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_other", now.AddDays(-4)), ct);

        await host.RebuildDailyTotalsAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<GroupAnalytics>("/api/analytics/group?days=30", cookie, ct);

        Assert.Equal(3m, page.Invites.InvitesSent);
        Assert.Equal(1, page.Invites.JoinedAfterInvite);
        Assert.Equal(GroupAnalyticsQuery.InviteFollowUpDays, page.Invites.FollowUpDays);
    }

    [Fact]
    public async Task Roles_ComeFromTheGroupSnapshot_WithGrantsAndRevokesFromFacts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var now = host.Clock.UtcNow;

        await StoreGroupSnapshotAsync(host, "usr_owner",
        [
            Role("grol_mod", "Moderator", "group_instance_moderate", "group_members_remove"),
            Role("grol_member", "Member"),
        ], now.AddMinutes(-5), ct);

        JsonObject RoleData(string id, string name) => new() { ["roleId"] = id, ["roleName"] = name };

        await host.WriteFactAsync(AuditFact(FactType.RoleGranted, "usr_a", now.AddDays(-1), actor: "usr_owner", extra: RoleData("grol_mod", "Moderator")), ct);
        await host.WriteFactAsync(AuditFact(FactType.RoleGranted, "usr_b", now.AddDays(-1), actor: "usr_owner", extra: RoleData("grol_mod", "Moderator")), ct);
        await host.WriteFactAsync(AuditFact(FactType.RoleRevoked, "usr_a", now.AddHours(-1), actor: "usr_owner", extra: RoleData("grol_mod", "Moderator")), ct);

        // A role that has since been deleted from the group still shows the change it had.
        await host.WriteFactAsync(AuditFact(FactType.RoleGranted, "usr_c", now.AddDays(-1), actor: "usr_owner", extra: RoleData("grol_old", "Old role")), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<GroupAnalytics>("/api/analytics/group?days=30", cookie, ct);

        var moderator = page.Roles.Single(r => r.Id == "grol_mod");
        Assert.True(moderator.IsModerationRole);
        Assert.Equal(2m, moderator.Granted);
        Assert.Equal(1m, moderator.Revoked);

        Assert.False(page.Roles.Single(r => r.Id == "grol_member").IsModerationRole);

        var old = page.Roles.Single(r => r.Id == "grol_old");
        Assert.Equal("Old role", old.Name);
        Assert.Equal(1m, old.Granted);

        Assert.Equal(now.AddMinutes(-5), page.RolesKnownAt);
    }

    [Fact]
    public async Task AllTime_StartsAtTheFirstRecordedDay()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var old = host.Clock.UtcNow.AddDays(-400);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_a", old), ct);
        await host.RebuildDailyTotalsAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<GroupAnalytics>("/api/analytics/group?all=true", cookie, ct);

        Assert.Equal(DateOnly.FromDateTime(old.UtcDateTime), page.From);
        Assert.Equal(DateOnly.FromDateTime(host.Clock.UtcNow.UtcDateTime), page.To);
        Assert.Equal(1m, page.Joined.Sum(p => p.Value));
    }

    [Fact]
    public async Task TheWindow_IsDerivedFromTheDeploymentClock_NotTheMachines()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        host.Clock.UtcNow = new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<GroupAnalytics>("/api/analytics/group?days=7", cookie, ct);

        Assert.Equal(new DateOnly(2026, 6, 15), page.To);
        Assert.Equal(new DateOnly(2026, 6, 9), page.From);
        Assert.Equal(host.Clock.UtcNow, page.GeneratedAt);
    }
}
