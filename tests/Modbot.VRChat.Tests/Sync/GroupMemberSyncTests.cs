using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// The member sweep: pages until empty, rows upserted, leavers marked, and facts inferred only
/// for what the audit log did not record.
/// </summary>
/// <remarks>
/// The interesting cases are the ones offset paging over a live list produces on its own -- a
/// member skipped between two pages, a restart mid-sweep, a list that suddenly answers nothing --
/// and the one the design is built around: a join the audit log already holds must not be
/// recorded twice.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class GroupMemberSyncTests(PostgresFixture fixture) : SyncTestBase(fixture)
{
    private static void Seed(FakeGroups groups, int count, params string[] roles)
    {
        for (var i = 0; i < count; i++)
            groups.Members.Add(Member($"usr_{i:0000}", Now.AddDays(-100 + (i % 90)), roles));
    }

    [Fact]
    public async Task TheFirstSweepWalksEveryPageAndRecordsOneSnapshotNotAJoinPerMember()
    {
        Seed(VRChat.Groups, 230);

        var finished = await SweepMembersAsync();

        Assert.True(finished.SweepComplete);
        Assert.True(finished.FirstSweep);

        // 100, then 100 from offset 95 (the overlap), then 40 from 190, then the empty page that
        // ends it. Four requests; not five, not three.
        Assert.Equal([0, 95, 190, 230], VRChat.Groups.MemberQueries.Select(q => q.Offset));

        await using var context = Database.NewContext();
        Assert.Equal(230, await context.GroupMembers.CountAsync(m => m.GroupId == GroupId && m.LeftAt == null, Ct));

        // One fact about the group. 230 "joined" facts dated today would say 230 people joined
        // on the day Modbot was installed, which is false and would spike every chart forever.
        var fact = Assert.Single(await FactsAsync());
        Assert.Equal(FactType.MembersSnapshot, fact.Type);
        Assert.Equal(GroupId, fact.SubjectId);
        Assert.Equal(230, Payload(fact)["memberCount"]!.GetValue<int>());
        Assert.Equal("list-diff", Payload(fact)["source"]!.GetValue<string>());

        Assert.Equal(230, (await SettingsAsync()).MemberSweepCount);
    }

    /// <summary>
    /// Everyone on the list becomes a vrchat_user row, so the profile sync fetches them -- with
    /// their join date as the sighting, not "now", so 5,000 people do not all land at the front
    /// of the profile queue on every sweep.
    /// </summary>
    [Fact]
    public async Task EveryMemberIsRecordedAsASightingDatedAtTheirJoin()
    {
        VRChat.Groups.Members.Add(Member("usr_a", Now.AddDays(-40)));

        await SweepMembersAsync();

        var user = await UserRowAsync("usr_a");

        Assert.NotNull(user);
        Assert.Equal(Now.AddDays(-40), user.LastSeenAt);
        Assert.Null(user.LastRefreshedAt);
    }

    [Fact]
    public async Task ARowKeepsWhatVRChatSaid()
    {
        var member = Member("usr_a", Now.AddDays(-3), "grol_b", "grol_a");
        member.IsRepresenting = true;
        member.ManagerNotes = "helped at the last event";
        VRChat.Groups.Members.Add(member);

        await SweepMembersAsync();

        var row = await MemberRowAsync("usr_a");

        Assert.NotNull(row);
        Assert.Equal("gmem_usr_a", row.MembershipId);
        Assert.Equal(Now.AddDays(-3), row.JoinedAt);
        Assert.Equal("member", row.MembershipStatus);
        Assert.Equal("visible", row.Visibility);
        Assert.True(row.IsRepresenting);
        Assert.Equal("helped at the last event", row.ManagerNotes);

        // Sorted, so the same roles in a different order are not a change.
        Assert.Equal("""["grol_a", "grol_b"]""", row.Roles);

        // The entry as VRChat sent it, parsed rather than string-matched: jsonb reformats.
        var raw = JsonNode.Parse(row.Raw!)!.AsObject();
        Assert.Equal("usr_a", raw["userId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ANewMemberOnALaterSweepIsRecordedAsAJoinDatedWhenVRChatSaysTheyJoined()
    {
        Seed(VRChat.Groups, 3);
        await SweepMembersAsync();

        RestMembers();
        var joinedAt = Clock.UtcNow.AddMinutes(-5);
        VRChat.Groups.Members.Add(Member("usr_new", joinedAt));

        var finished = await SweepMembersAsync();

        Assert.True(finished.SweepComplete);
        Assert.Equal(1, finished.FactsWritten);

        var join = Assert.Single(await FactsOfTypeAsync(FactType.MemberJoined));

        Assert.Equal("usr_new", join.SubjectId);
        Assert.Equal(FactSource.SyncDiff, join.Source);

        // VRChat stated the time, so the fact is exact rather than a window.
        Assert.Equal(joinedAt, join.OccurredAt);
        Assert.Null(join.OccurredBefore);
        Assert.Null(join.ActorId);
        Assert.Equal("list-diff", Payload(join)["source"]!.GetValue<string>());
    }

    /// <summary>
    /// The rule the whole design turns on. The audit log is authoritative; when it already holds
    /// the join, the sweep's inference is a duplicate that would count the join twice in the
    /// daily totals.
    /// </summary>
    [Fact]
    public async Task AJoinTheAuditLogAlreadyRecordedIsNotRecordedAgain()
    {
        Seed(VRChat.Groups, 3);
        await SweepMembersAsync();

        RestMembers();
        var joinedAt = Clock.UtcNow.AddMinutes(-5);

        // The audit log gets there first, the way it does in production.
        VRChat.Groups.Add(Entry("gaud_join", joinedAt, GroupAuditLogEvents.MemberJoin, target: "usr_new"));
        await RunAuditLogAsync();

        VRChat.Groups.Members.Add(Member("usr_new", joinedAt));
        var noticed = await SweepMembersAsync();

        // Noticed in the same instant the audit log last polled, so it waits for the next poll.
        Assert.Equal(1, noticed.FactsWaiting);

        RestMembers();
        await RunAuditLogAsync();
        var settled = await SweepMembersAsync();

        Assert.Equal(0, settled.FactsWritten);
        Assert.Equal(1, settled.FactsDeduplicated);

        var join = Assert.Single(await FactsOfTypeAsync(FactType.MemberJoined));
        Assert.Equal(FactSource.AuditLog, join.Source);
        Assert.Null((await MemberRowAsync("usr_new"))!.WaitingFacts);
    }

    /// <summary>
    /// When the audit log is running, the sweep does not race it: the join waits until the audit
    /// log has polled past the moment it was noticed, and is recorded on the next pass only if
    /// the audit log still has nothing.
    /// </summary>
    [Fact]
    public async Task AJoinWaitsForTheAuditLogToPollBeforeItIsRecorded()
    {
        Seed(VRChat.Groups, 3);
        await SweepMembersAsync();

        // The audit log polled a moment ago -- alive, but not yet past the join.
        await using (var context = Database.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.AuditLogPolledAt = Clock.UtcNow;
            await context.SaveChangesAsync(Ct);
        }

        RestMembers();
        VRChat.Groups.Members.Add(Member("usr_new", Clock.UtcNow.AddMinutes(-1)));

        var second = await SweepMembersAsync();

        Assert.Equal(0, second.FactsWritten);
        Assert.Equal(1, second.FactsWaiting);
        Assert.Empty(await FactsOfTypeAsync(FactType.MemberJoined));
        Assert.NotNull((await MemberRowAsync("usr_new"))!.WaitingFacts);

        // The audit log polls again, after the change was noticed, and has nothing to say.
        RestMembers();
        await using (var context = Database.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.AuditLogPolledAt = Clock.UtcNow;
            await context.SaveChangesAsync(Ct);
        }

        var third = await SweepMembersAsync();

        Assert.Equal(1, third.FactsWritten);
        Assert.Single(await FactsOfTypeAsync(FactType.MemberJoined));
        Assert.Null((await MemberRowAsync("usr_new"))!.WaitingFacts);
    }

    [Fact]
    public async Task AMemberMissingFromAFullSweepIsMarkedLeftAndTheLeaveIsRecordedWithAWindow()
    {
        Seed(VRChat.Groups, 3);
        await SweepMembersAsync();
        var lastSeen = (await MemberRowAsync("usr_0001"))!.LastSeenAt;

        RestMembers();
        VRChat.Groups.Members.RemoveAll(m => m.UserId == "usr_0001");

        var second = await SweepMembersAsync();

        Assert.Equal(1, second.MarkedGone);
        Assert.Equal(Clock.UtcNow, (await MemberRowAsync("usr_0001"))!.LeftAt);

        // Not yet a fact: a leave noticed this instant has not had the audit log's turn, and a
        // page-boundary miss looks identical until the next sweep says otherwise.
        Assert.Empty(await FactsOfTypeAsync(FactType.MemberLeft));
        var noticedAt = Clock.UtcNow;

        RestMembers();
        var third = await SweepMembersAsync();

        Assert.Equal(1, third.FactsWritten);

        var leave = Assert.Single(await FactsOfTypeAsync(FactType.MemberLeft));

        // A window, not an instant: all the sweep knows is that they were listed at one sweep
        // and not at the next (spec 5.3).
        Assert.Equal(lastSeen, leave.OccurredAt);
        Assert.Equal(noticedAt, leave.OccurredBefore);
        Assert.Equal(FactSource.SyncDiff, leave.Source);
    }

    /// <summary>
    /// Offset paging over a live list skips somebody now and then. Skipped is not left: when
    /// they are back next sweep with the same join date, the leave is dropped before it was
    /// ever written, and no join is invented either.
    /// </summary>
    [Fact]
    public async Task AMemberSkippedForOneSweepIsNeitherALeaveNorAJoin()
    {
        Seed(VRChat.Groups, 3);
        await SweepMembersAsync();

        RestMembers();
        var skipped = VRChat.Groups.Members.Single(m => m.UserId == "usr_0001");
        VRChat.Groups.Members.Remove(skipped);
        await SweepMembersAsync();

        Assert.NotNull((await MemberRowAsync("usr_0001"))!.LeftAt);

        RestMembers();
        VRChat.Groups.Members.Add(skipped);
        await SweepMembersAsync();

        var row = await MemberRowAsync("usr_0001");

        Assert.Null(row!.LeftAt);
        Assert.Null(row.WaitingFacts);
        Assert.Empty(await FactsOfTypeAsync(FactType.MemberLeft));
        Assert.Empty(await FactsOfTypeAsync(FactType.MemberJoined));
    }

    [Fact]
    public async Task AMemberWhoLeftAndCameBackGetsALeaveAndAJoin()
    {
        Seed(VRChat.Groups, 3);
        await SweepMembersAsync();

        RestMembers();
        VRChat.Groups.Members.RemoveAll(m => m.UserId == "usr_0001");
        await SweepMembersAsync();

        RestMembers();
        var rejoinedAt = Clock.UtcNow.AddMinutes(-2);
        VRChat.Groups.Members.Add(Member("usr_0001", rejoinedAt));
        var third = await SweepMembersAsync();

        Assert.Equal(2, third.FactsWritten);

        var leave = Assert.Single(await FactsOfTypeAsync(FactType.MemberLeft));
        var join = Assert.Single(await FactsOfTypeAsync(FactType.MemberJoined));

        Assert.Equal("usr_0001", leave.SubjectId);
        Assert.Equal(rejoinedAt, join.OccurredAt);
        Assert.Null((await MemberRowAsync("usr_0001"))!.LeftAt);
    }

    /// <summary>
    /// A member first listed with a join date from before the previous sweep started was there
    /// all along and got missed. That is a miss on Modbot's side, not a join, and recording it
    /// as one would date a join wrongly by however long they had been a member.
    /// </summary>
    [Fact]
    public async Task ANewRowWhoJoinedBeforeTheLastSweepIsTreatedAsMissedNotNew()
    {
        Seed(VRChat.Groups, 3);
        await SweepMembersAsync();

        RestMembers();
        VRChat.Groups.Members.Add(Member("usr_old", Now.AddDays(-200)));
        var second = await SweepMembersAsync();

        Assert.Equal(0, second.FactsWritten);
        Assert.Equal(0, second.FactsWaiting);
        Assert.NotNull(await MemberRowAsync("usr_old"));
        Assert.Empty(await FactsOfTypeAsync(FactType.MemberJoined));
    }

    [Fact]
    public async Task ARoleChangeRecordsTheRoleGrantedAndTheRoleTakenAway()
    {
        VRChat.Groups.Members.Add(Member("usr_a", Now.AddDays(-30), "grol_member"));
        await SweepMembersAsync();

        RestMembers();
        VRChat.Groups.Members[0].RoleIds = ["grol_mod"];
        var second = await SweepMembersAsync();

        Assert.Equal(2, second.FactsWritten);

        var granted = Assert.Single(await FactsOfTypeAsync(FactType.RoleGranted));
        var revoked = Assert.Single(await FactsOfTypeAsync(FactType.RoleRevoked));

        Assert.Equal("grol_mod", Payload(granted)["roleId"]!.GetValue<string>());
        Assert.Equal("grol_member", Payload(revoked)["roleId"]!.GetValue<string>());
        Assert.NotNull(granted.OccurredBefore);
        Assert.Equal("""["grol_mod"]""", (await MemberRowAsync("usr_a"))!.Roles);
    }

    [Fact]
    public async Task ARoleGrantTheAuditLogRecordedIsNotRecordedAgainButADifferentRoleIs()
    {
        VRChat.Groups.Members.Add(Member("usr_a", Now.AddDays(-30)));
        await SweepMembersAsync();

        RestMembers();

        var entry = Entry("gaud_role", Clock.UtcNow.AddMinutes(-1), GroupAuditLogEvents.RoleAssign, target: "usr_a");
        entry.Data = """{"roleId":"grol_mod","roleName":"Moderator"}""";
        VRChat.Groups.Add(entry);
        await RunAuditLogAsync();

        VRChat.Groups.Members[0].RoleIds = ["grol_mod", "grol_helper"];
        var noticed = await SweepMembersAsync();

        Assert.Equal(2, noticed.FactsWaiting);

        RestMembers();
        await RunAuditLogAsync();
        var second = await SweepMembersAsync();

        Assert.Equal(1, second.FactsWritten);
        Assert.Equal(1, second.FactsDeduplicated);

        var inferred = (await FactsOfTypeAsync(FactType.RoleGranted)).Where(f => f.Source == FactSource.SyncDiff).ToList();
        Assert.Equal("grol_helper", Payload(Assert.Single(inferred))["roleId"]!.GetValue<string>());
    }

    /// <summary>
    /// Spec 4.2.4: a restart resumes from the cursor. Each pass is one page and one save, so
    /// there is no in-memory position to lose.
    /// </summary>
    [Fact]
    public async Task ARestartMidSweepResumesFromTheStoredOffset()
    {
        Seed(VRChat.Groups, 230);

        await RunMemberSweepAsync();
        await RunMemberSweepAsync();

        Assert.Equal(190, (await SettingsAsync()).MemberSweepOffset);
        Assert.NotNull((await SettingsAsync()).MemberSweepStartedAt);

        // "Restart": every pass already runs in a fresh scope; the next one reads the cursor.
        var third = await RunMemberSweepAsync();

        Assert.Equal(190, VRChat.Groups.MemberQueries[^1].Offset);
        Assert.Equal(40, third.RowsRead);
        Assert.False(third.SweepStarted);
    }

    [Fact]
    public async Task BetweenSweepsThePassRestsWithoutARequest()
    {
        Seed(VRChat.Groups, 3);
        await SweepMembersAsync();
        var requests = VRChat.Groups.MemberRequests;

        Clock.Advance(TimeSpan.FromMinutes(1));
        var pass = await RunMemberSweepAsync();

        Assert.NotNull(pass.RestUntil);
        Assert.Equal(requests, VRChat.Groups.MemberRequests);

        RestMembers();
        var next = await RunMemberSweepAsync();

        Assert.True(next.SweepStarted);
        Assert.Equal(requests + 1, VRChat.Groups.MemberRequests);
    }

    /// <summary>
    /// Spec 4.3.1: a 429 is a cold stop, never a retry. The cursor stays, nothing is sent until
    /// the stop lifts, and the ban sweep -- a different bucket -- is untouched.
    /// </summary>
    [Fact]
    public async Task ARateLimitedPageColdStopsTheMemberSweepAndNothingElse()
    {
        Seed(VRChat.Groups, 230);
        VRChat.Groups.Bans.Add(Ban("usr_banned"));

        await RunMemberSweepAsync();
        VRChat.Groups.MembersStatus = HttpStatusCode.TooManyRequests;

        var limited = await RunMemberSweepAsync();

        Assert.Equal(SyncOutcome.RateLimited, limited.Outcome);
        Assert.Equal(95, (await SettingsAsync()).MemberSweepOffset);

        var requests = VRChat.Groups.MemberRequests;
        VRChat.Groups.MembersStatus = HttpStatusCode.OK;

        var refused = await RunMemberSweepAsync();

        Assert.Equal(SyncOutcome.RateLimited, refused.Outcome);
        Assert.Equal(requests, VRChat.Groups.MemberRequests);

        var bans = await SweepBansAsync();
        Assert.True(bans.SweepComplete);

        Clock.Advance(Limiter.Options.ColdStopBase + TimeSpan.FromMinutes(1));
        var resumed = await RunMemberSweepAsync();

        Assert.Equal(95, VRChat.Groups.MemberQueries[^1].Offset);
        Assert.Equal(SyncOutcome.Produced, resumed.Outcome);
    }

    /// <summary>
    /// A list that answers nobody where the last sweep listed hundreds is VRChat misbehaving, not
    /// a group emptying out. Marking everyone as left on that evidence would write hundreds of
    /// false leaves.
    /// </summary>
    [Fact]
    public async Task ASweepThatListsNobodyAfterAFullOneMarksNobodyAsLeft()
    {
        Seed(VRChat.Groups, 5);
        await SweepMembersAsync();

        RestMembers();
        VRChat.Groups.Members.Clear();

        var empty = await SweepMembersAsync();

        Assert.Equal(SyncOutcome.Failed, empty.Outcome);
        Assert.Null((await MemberRowAsync("usr_0002"))!.LeftAt);
        Assert.Null((await SettingsAsync()).MemberSweepStartedAt);
    }

    [Fact]
    public async Task AnUnconfiguredDeploymentIssuesNothing()
    {
        await using (var context = Database.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.ManagedGroupId = null;
            await context.SaveChangesAsync(Ct);
        }

        var pass = await RunMemberSweepAsync();

        Assert.Equal(SyncOutcome.NotConfigured, pass.Outcome);
        Assert.Equal(0, VRChat.Groups.MemberRequests);
    }

    /// <summary>Spec 4.2.3: the UI shows real last-sync times, so every pass records when it ran.</summary>
    [Fact]
    public async Task EveryPassRecordsWhenItRan()
    {
        Seed(VRChat.Groups, 3);
        await SweepMembersAsync();

        Assert.Equal(Clock.UtcNow, (await SettingsAsync()).MemberSweepPolledAt);
        Assert.Equal(Clock.UtcNow, (await SettingsAsync()).MemberSweepCompletedAt);
    }

    private static JsonObject Payload(ModbotEvent fact) => JsonNode.Parse(fact.Data)!.AsObject();
}
