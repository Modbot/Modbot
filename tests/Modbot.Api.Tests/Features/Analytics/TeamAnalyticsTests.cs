using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.DailyTotals;
using Modbot.Api.Features.Analytics.Team;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Api.Tests.Features.Places;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Analytics;

[Collection(nameof(PostgresCollection))]
public class TeamAnalyticsTests
{
    private const string World = "wrld_a";
    private const string Instance = "12345";

    /// <summary>Each moderator's own numbers need the audit log as well as analytics.</summary>
    private const ModbotPermissions SeesEachModerator = ModbotPermissions.ViewAnalytics | ModbotPermissions.ViewAuditLog;

    private readonly PostgresFixture _db;

    public TeamAnalyticsTests(PostgresFixture db) => _db = db;

    /// <summary>
    /// Actions on people and door work are counted apart and never added up: a moderator who lets
    /// people in and hands out roles is not one who throws people out.
    /// </summary>
    [Fact]
    public async Task Moderators_KeepActionsOnPeopleApartFromDoorWork_FromTheDailyTotals()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var day = host.Clock.UtcNow.AddDays(-1);

        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_1", day, actor: "usr_alice", actorName: "Alice"), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_2", day.AddMinutes(1), actor: "usr_alice", actorName: "Alice"), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceKick, "usr_3", day.AddMinutes(2), actor: "usr_alice", actorName: "Alice", worldId: World, instanceId: Instance), ct);
        await host.WriteFactAsync(AuditFact(FactType.RoleGranted, "usr_5", day.AddMinutes(3), actor: "usr_alice", actorName: "Alice"), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_6", day.AddMinutes(4), actor: "usr_alice", actorName: "Alice"), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberUnbanned, "usr_7", day.AddMinutes(5), actor: "usr_alice", actorName: "Alice"), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceWarn, "usr_4", day.AddMinutes(6), actor: "usr_bob", actorName: "Bob", worldId: World, instanceId: Instance), ct);
        await host.RebuildDailyTotalsAsync(ct);

        var cookie = await host.SignedInAsync(SeesEachModerator, ct);
        var page = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=30", cookie, ct);

        Assert.True(page.CanSeeEachModerator);
        Assert.Equal(2, page.Moderators.Count);
        Assert.Equal(2, page.ModeratorsActive);

        var alice = page.Moderators.Single(m => m.Who.Id == "usr_alice");
        Assert.Equal("Alice", alice.Who.Name);
        Assert.Equal(3m, alice.OnPeople);
        Assert.Equal(3m, alice.DoorAndAdmin);
        Assert.Equal(2m, alice.ByKind[DailyTotalMetrics.ModeratorBans]);
        Assert.Equal(1m, alice.ByKind[DailyTotalMetrics.ModeratorInstanceKicks]);
        Assert.Equal(1m, alice.ByKind[DailyTotalMetrics.ModeratorUnbans]);
        Assert.Equal(1, alice.DaysActive);
        Assert.Equal(3m, alice.OnPeoplePerDay);
        Assert.Equal(DateOnly.FromDateTime(day.UtcDateTime), alice.LastActiveDay);

        Assert.Equal(4m, page.OnPeoplePerDay.Sum(p => p.Value));
        Assert.Equal(3m, page.DoorAndAdminPerDay.Sum(p => p.Value));
        Assert.Equal(1m, page.ActionsPerDayByKind.Single(k => k.Metric == DailyTotalMetrics.ModeratorWarns).Total);

        Assert.Equal(TeamAnalyticsQuery.Kinds.Count, page.Kinds.Count);
        Assert.Equal(ActionGroups.Door, page.Kinds.Single(k => k.Metric == DailyTotalMetrics.ModeratorUnbans).Group);
        Assert.Equal(ActionGroups.People, page.Kinds.Single(k => k.Metric == DailyTotalMetrics.ModeratorRejections).Group);

        // Two moderators are too few for a middle: with your own number it would give the other's away.
        Assert.Null(page.Middle);
    }

    /// <summary>
    /// Without the audit log a moderator sees the team's numbers, their own row and the team's
    /// middle, and nobody else by name: not in the table and not as the last one out of an instance.
    /// </summary>
    [Fact]
    public async Task WithoutTheAuditLog_OnlyTheTeamAndYourOwnRowAreShown()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var me = await LinkedVRChatIdAsync(host, ct);

        var day = host.Clock.UtcNow.AddDays(-2);

        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_1", day, actor: me, actorName: "Me"), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_2", day.AddDays(1), actor: me, actorName: "Me"), ct);
        await host.WriteFactAsync(AuditFact(FactType.InviteCreated, "usr_3", day, actor: me, actorName: "Me"), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceWarn, "usr_4", day, actor: "usr_bob", actorName: "Bob"), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceWarn, "usr_5", day, actor: "usr_cat", actorName: "Cat"), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceWarn, "usr_6", day.AddMinutes(1), actor: "usr_cat", actorName: "Cat"), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceWarn, "usr_7", day.AddMinutes(2), actor: "usr_cat", actorName: "Cat"), ct);

        for (var i = 0; i < 5; i++)
            await host.WriteFactAsync(AuditFact(FactType.GroupInstanceWarn, $"usr_d{i}", day.AddMinutes(10 + i), actor: "usr_dan", actorName: "Dan"), ct);

        // A gap, so there is a last moderator out to leave unnamed.
        var t = host.Clock.UtcNow.AddHours(-6);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_bob", t, World, Instance, name: "Bob"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_p1", t.AddMinutes(1), World, Instance), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_bob", t.AddMinutes(30), World, Instance), ct);

        await host.RebuildDailyTotalsAsync(ct);

        var page = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=30", cookie, ct);

        Assert.False(page.CanSeeEachModerator);
        Assert.Empty(page.Moderators);
        Assert.Equal(4, page.ModeratorsActive);

        Assert.NotNull(page.You);
        Assert.Equal(me, page.You.Who.Id);
        Assert.Equal(2m, page.You.OnPeople);
        Assert.Equal(1m, page.You.DoorAndAdmin);
        Assert.Equal(2, page.You.DaysActive);
        Assert.Equal(1m, page.You.OnPeoplePerDay);

        // Me 2, Bob 1, Cat 3, Dan 5 actions on people: the middle is halfway between 2 and 3.
        // Per day: 1, 1, 3, 5 -> halfway between 1 and 3.
        Assert.NotNull(page.Middle);
        Assert.Equal(4, page.Middle.Moderators);
        Assert.Equal(2.5m, page.Middle.OnPeople);
        Assert.Equal(2m, page.Middle.OnPeoplePerDay);

        var gap = Assert.Single(page.CoverageGaps);
        Assert.Null(gap.LastModerator);
    }

    /// <summary>A moderator with no actions in the range still gets their row, at nought.</summary>
    [Fact]
    public async Task You_IsANoughtRow_WhenTheCallerTookNoAction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var me = await LinkedVRChatIdAsync(host, ct);

        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_1", host.Clock.UtcNow.AddDays(-1), actor: "usr_bob"), ct);
        await host.RebuildDailyTotalsAsync(ct);

        var page = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=30", cookie, ct);

        Assert.NotNull(page.You);
        Assert.Equal(me, page.You.Who.Id);
        Assert.Equal(0m, page.You.OnPeople);
        Assert.Equal(0, page.You.DaysActive);
        Assert.Null(page.You.OnPeoplePerDay);
        Assert.Null(page.You.LastActiveDay);
    }

    /// <summary>
    /// The week grid: an hour is busy by VRChat's head count, and of the busy hours one a gap
    /// overlapped had nobody on, and one no companion reported from is not seen -- not uncovered.
    /// </summary>
    [Fact]
    public async Task CoverWeek_SortsBusyHoursIntoCoveredNobodyOnAndNotSeen()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await SetManagedGroupAsync(host, ct);

        var now = host.Clock.UtcNow;
        var h0 = new DateTimeOffset(now.UtcDateTime.Date.AddHours(now.UtcDateTime.Hour), TimeSpan.Zero).AddHours(-6);

        var instance = await PlacesFixtures.InstanceAsync(host, World, "1", h0, h0.AddHours(3), h0.AddHours(3), ct);
        await HeadCountsAsync(host, instance.Id, ct, (h0.AddMinutes(5), 4), (h0.AddHours(2).AddMinutes(10), 1));

        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_someone", h0.AddDays(-10), actor: "usr_mod"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_mod", h0.AddMinutes(6), World, "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_p1", h0.AddMinutes(7), World, "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_mod", h0.AddMinutes(70), World, "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_mod", h0.AddMinutes(110), World, "1"), ct);

        var cookie = await host.SignedInAsync(SeesEachModerator, ct);
        var page = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=30&people=3", cookie, ct);

        int Bucket(DateTimeOffset at) => (((int)at.UtcDateTime.DayOfWeek + 6) % 7) * 24 + at.UtcDateTime.Hour;

        Assert.Equal(3, page.Cover.People);
        Assert.Equal(3, page.Cover.SavedPeople);
        Assert.Equal(168, page.Cover.Busy.Count);
        Assert.Equal(3, page.Cover.Busy.Sum());

        Assert.Equal(1, page.Cover.Busy[Bucket(h0)]);
        Assert.Equal(0, page.Cover.NobodyOn[Bucket(h0)]);
        Assert.Equal(0, page.Cover.NotSeen[Bucket(h0)]);

        Assert.Equal(1, page.Cover.NobodyOn[Bucket(h0.AddHours(1))]);
        Assert.Equal(1, page.Cover.NotSeen[Bucket(h0.AddHours(2))]);
        Assert.Equal(1, page.Cover.NobodyOn.Sum());
        Assert.Equal(1, page.Cover.NotSeen.Sum());

        // Raise the bar above the head count and no hour is busy.
        var quiet = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=30&people=5", cookie, ct);
        Assert.Equal(0, quiet.Cover.Busy.Sum());
    }

    /// <summary>The bar is saved by somebody who may change settings, and nobody else.</summary>
    [Fact]
    public async Task CoverPeople_IsSavedWithChangeSettings_AndRefusedWithout()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var reader = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var refused = await host.PutJsonAsync("/api/analytics/team/people", new { people = 5 }, reader, ct);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, refused.StatusCode);

        var admin = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, ct);
        var tooMany = await host.PutJsonAsync("/api/analytics/team/people", new { people = 0 }, admin, ct);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, tooMany.StatusCode);

        var saved = await host.PutJsonAsync("/api/analytics/team/people", new { people = 5 }, admin, ct);
        saved.EnsureSuccessStatusCode();

        var page = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=30", reader, ct);
        Assert.Equal(5, page.Cover.People);
        Assert.Equal(5, page.Cover.SavedPeople);

        var asked = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=30&people=10", reader, ct);
        Assert.Equal(10, asked.Cover.People);
        Assert.Equal(5, asked.Cover.SavedPeople);
    }

    /// <summary>
    /// A join request waits from the request to the approval or rejection; a join by invite, with
    /// no request before it, is not a decision. The middle of two waits is halfway between them.
    /// </summary>
    [Fact]
    public async Task Waits_AreTheMiddleOfEachQueuesDecisions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddDays(-2);

        await host.WriteFactAsync(AuditFact(FactType.JoinRequestCreated, "usr_r1", t, actor: "usr_r1"), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_r1", t.AddMinutes(30), actor: "usr_mod"), ct);
        await host.WriteFactAsync(AuditFact(FactType.JoinRequestCreated, "usr_r2", t.AddHours(1), actor: "usr_r2"), ct);
        await host.WriteFactAsync(AuditFact(FactType.JoinRequestRejected, "usr_r2", t.AddHours(3), actor: "usr_mod"), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_invited", t.AddHours(4), actor: "usr_mod"), ct);

        // Rejected above, then let in by invite days later: the request was already answered, so
        // the invite is not a second, days-long answer to it.
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_r2", t.AddDays(1), actor: "usr_mod"), ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.Reviews.Add(new Review
            {
                ModeratorPlatform = FactPlatform.VRChat,
                ModeratorId = "usr_mod",
                Signal = "same-person",
                About = "usr_r2",
                Summary = "",
                WindowStart = t,
                WindowEnd = t,
                OpenedAt = t,
                UpdatedAt = t.AddHours(1),
                State = ReviewState.Closed,
                ClosedAt = t.AddHours(1),
            });
            await db.SaveChangesAsync(ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=30", cookie, ct);

        var requests = page.Waits.Single(w => w.Queue == Queues.JoinRequests);
        Assert.Equal(2, requests.Decided);
        Assert.Equal(75m, requests.MiddleMinutes);

        var reviews = page.Waits.Single(w => w.Queue == Queues.Reviews);
        Assert.Equal(1, reviews.Decided);
        Assert.Equal(60m, reviews.MiddleMinutes);
        Assert.Equal(60m, Assert.Single(reviews.MiddleMinutesPerDay).Value);
    }

    /// <summary>
    /// People acted on again within thirty days, bans lifted within thirty days, and the reasons the
    /// case files give for the lifts -- counted for the team, never per moderator.
    /// </summary>
    [Fact]
    public async Task Outcomes_CountPeopleActedOnAgain_AndBansLifted_WithTheirReasons()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var now = host.Clock.UtcNow;

        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceKick, "usr_a", now.AddDays(-10), actor: "usr_mod"), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceKick, "usr_a", now.AddDays(-5), actor: "usr_mod"), ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceWarn, "usr_b", now.AddDays(-3), actor: "usr_mod"), ct);
        // Two bans old enough to have had their thirty days, one lifted within them; and one from
        // two days ago, which has not had them yet and so is in neither side of the share.
        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_c", now.AddDays(-50), actor: "usr_mod"), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberUnbanned, "usr_c", now.AddDays(-45), actor: "usr_mod"), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_f", now.AddDays(-40), actor: "usr_mod"), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_d", now.AddDays(-2), actor: "usr_mod"), ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

            var mistake = new BanReason
            {
                Label = "Mistake",
                UsedFor = ReasonUse.Unban,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.BanReasons.Add(mistake);

            db.CaseFiles.Add(LiftedCaseFile("usr_c", now.AddDays(-45), $"[\"{mistake.Id}\"]", now));
            db.CaseFiles.Add(LiftedCaseFile("usr_e", now.AddDays(-4), "[]", now));
            await db.SaveChangesAsync(ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=90", cookie, ct);

        // a (kicked twice), b, c, f, d; only a was acted on again within thirty days.
        Assert.Equal(5, page.ActedOnAgain.People);
        Assert.Equal(1, page.ActedOnAgain.Again);
        Assert.Equal(30, page.ActedOnAgain.Days);

        Assert.Equal(3, page.BansLifted.Bans);
        Assert.Equal(2, page.BansLifted.OldEnough);
        Assert.Equal(1, page.BansLifted.LiftedWithin);
        var reason = Assert.Single(page.BansLifted.Reasons);
        Assert.Equal("Mistake", reason.Label);
        Assert.Equal(1, reason.Count);
        Assert.Equal(1, page.BansLifted.LiftedWithoutReason);
    }

    private static CaseFile LiftedCaseFile(string person, DateTimeOffset liftedAt, string reasons, DateTimeOffset now) => new()
    {
        UserId = person,
        BannedAt = liftedAt.AddDays(-5),
        AuthorUserId = Guid.NewGuid(),
        AuthorUsername = "ada",
        CreatedAt = now,
        UpdatedAt = now,
        SnapshotTakenAt = now,
        LiftedAt = liftedAt,
        LiftedByUsername = "ada",
        LiftReasonIds = reasons,
    };

    /// <summary>The VRChat id the test account was linked to: the only account after a reset.</summary>
    private static async Task<string> LinkedVRChatIdAsync(ReadSurfaceTestHost host, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var ids = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.Users.Where(u => u.VRChatUserId != null).Select(u => u.VRChatUserId!), ct);
        return Assert.Single(ids);
    }

    /// <summary>Head counts, one row per change, the way <c>HeadCounts.Record</c> writes them.</summary>
    private static async Task HeadCountsAsync(
        ReadSurfaceTestHost host, Guid instanceId, CancellationToken ct, params (DateTimeOffset At, int Count)[] counts)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        foreach (var (at, count) in counts)
        {
            db.InstanceHeadCounts.Add(new InstanceHeadCount
            {
                InstanceId = instanceId,
                CountedAt = at,
                HeadCount = count,
                UserCount = count,
                Source = HeadCounts.FromPage,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The table is not a leaderboard: whoever acted most recently comes first, whatever their
    /// count, and moderators last active on the same day are in name order.
    /// </summary>
    [Fact]
    public async Task Moderators_AreSortedByLastActive_ThenByName_NotByCount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var yesterday = host.Clock.UtcNow.AddDays(-1);
        var earlier = host.Clock.UtcNow.AddDays(-5);

        // Zed has the most actions, but none since five days ago.
        for (var i = 0; i < 5; i++)
            await host.WriteFactAsync(AuditFact(FactType.MemberBanned, $"usr_z{i}", earlier.AddMinutes(i), actor: "usr_zed", actorName: "Zed"), ct);

        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_b1", yesterday, actor: "usr_bob", actorName: "bob"), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_a1", yesterday.AddMinutes(1), actor: "usr_amy", actorName: "Amy"), ct);
        await host.RebuildDailyTotalsAsync(ct);

        var cookie = await host.SignedInAsync(SeesEachModerator, ct);
        var page = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=30", cookie, ct);

        Assert.Equal(["usr_amy", "usr_bob", "usr_zed"], page.Moderators.Select(m => m.Who.Id));
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

        var cookie = await host.SignedInAsync(SeesEachModerator, ct);
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

        var cookie = await host.SignedInAsync(SeesEachModerator, ct);
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

        var cookie = await host.SignedInAsync(SeesEachModerator, ct);
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

        var cookie = await host.SignedInAsync(SeesEachModerator, ct);
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

        var cookie = await host.SignedInAsync(SeesEachModerator, ct);
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

        await SetManagedGroupAsync(host, ct);

        var t = host.Clock.UtcNow.AddHours(-6);

        // Counted from Modbot's instance list, not from the audit log's create entries: number 3 has
        // no entry of VRChat's at all, and 2 and 3 are never reported from.
        await PlacesFixtures.InstanceAsync(host, World, "1", t, t.AddMinutes(40), t.AddMinutes(40), ct);
        await PlacesFixtures.InstanceAsync(host, World, "2", t, t.AddMinutes(40), t.AddMinutes(40), ct);
        await PlacesFixtures.InstanceAsync(host, World, "3", t, t.AddMinutes(40), t.AddMinutes(40), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_mod", t.AddMinutes(1), World, "1"), ct);

        var cookie = await host.SignedInAsync(SeesEachModerator, ct);
        var page = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=30", cookie, ct);

        Assert.Equal(1, page.InstancesWatched);
        Assert.Equal(2, page.InstancesOpenedWithoutAnyWatch);
    }

    /// <summary>A number VRChat handed out twice is two instances, each judged by the reports made while it ran.</summary>
    [Fact]
    public async Task AReusedInstanceNumber_IsTwoInstances_EachWatchedOrNotOnItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await SetManagedGroupAsync(host, ct);

        var t = host.Clock.UtcNow.AddHours(-10);

        await PlacesFixtures.InstanceAsync(host, World, "5", t, t.AddMinutes(30), t.AddMinutes(30), ct);
        await PlacesFixtures.InstanceAsync(host, World, "5", t.AddHours(4), t.AddHours(5), t.AddHours(5), ct);

        // A moderator was in the second one only.
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_mod", t.AddHours(4).AddMinutes(1), World, "5"), ct);

        var cookie = await host.SignedInAsync(SeesEachModerator, ct);
        var page = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=30", cookie, ct);

        Assert.Equal(1, page.InstancesOpenedWithoutAnyWatch);
    }

    private static async Task SetManagedGroupAsync(ReadSurfaceTestHost host, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await db.GetSettingsAsync(ct);
        settings.ManagedGroupId = "grp_1";
        await db.SaveChangesAsync(ct);
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

        var cookie = await host.SignedInAsync(SeesEachModerator, ct);
        var page = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=30", cookie, ct);

        // owner + usr_guard + usr_owner-as-actor (same person) = 2 distinct; usr_former and usr_plain are not moderators.
        Assert.Equal(2, page.ModeratorsRecognised);

        var gap = Assert.Single(page.CoverageGaps);
        Assert.Equal("usr_guard", gap.LastModerator?.Id);
    }
}
