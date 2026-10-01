using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Cases;
using Modbot.Api.Features.Moderation;
using Modbot.Api.Tests.Fakes;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.TestSupport;
using Modbot.VRChat;

// Aliased rather than imported wholesale: VRChat's GroupMember and Modbot's group_member row share
// a name, and this file uses both -- the one the SDK returns, and the one the sweep stores.
using VRChatGroupMember = VRChat.API.Model.GroupMember;
using VRChatSuccess = VRChat.API.Model.Success;

namespace Modbot.Api.Tests.Features.Moderation;

/// <summary>
/// Kicking, banning and unbanning from Modbot (M4 §4): each behind its own permission, nothing
/// recorded unless VRChat accepted, a refusal recorded as a refusal, the service account off
/// limits, and one confirmation acting exactly once.
/// </summary>
/// <remarks>
/// The gate is the scripted fake, so nothing here reaches VRChat. What is being proved is the
/// order of operations around it, which is the part that decides whether a moderator can be shown
/// a ban that never happened.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class ModerationActionTests
{
    private const string Group = "grp_1";
    private const string Person = "usr_troublemaker";
    private const string ModbotAccount = "usr_modbot";

    private static readonly DateTimeOffset Day = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _db;

    public ModerationActionTests(PostgresFixture db) => _db = db;

    // ── Setting the scene ──────────────────────────────────────────────────────────────────

    /// <summary>A group Modbot manages, signed in as its own account, with the person a member.</summary>
    /// <param name="member">
    /// False leaves the member list without them, which is the state a stranger is in: somebody a
    /// moderator heard about from another group or a Discord message and has never seen join.
    /// </param>
    private static async Task SeedAsync(
        ReadSurfaceTestHost host, CancellationToken ct, bool banned = false, bool member = true)
    {
        host.Clock.UtcNow = Day;

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var settings = await db.GetSettingsAsync(ct);
        settings.ManagedGroupId = Group;
        settings.VRChatSessionUserId = ModbotAccount;

        if (member)
        {
            db.GroupMembers.Add(new GroupMember
            {
                GroupId = Group,
                UserId = Person,
                Roles = "[]",
                JoinedAt = Day.AddDays(-20),
                FirstSeenAt = Day.AddDays(-20),
                LastSeenAt = Day.AddHours(-1),
            });
        }

        if (banned)
        {
            db.GroupBans.Add(new GroupBan
            {
                GroupId = Group,
                UserId = Person,
                BannedAt = Day.AddDays(-1),
                FirstSeenAt = Day.AddDays(-1),
                LastSeenAt = Day.AddHours(-1),
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>A gate that accepts all three actions.</summary>
    private static FakeVRChatGate Accepting()
        => new FakeVRChatGate()
            .SignedInAs()
            .Returns("KickGroupMember", new VRChatSuccess())
            .Returns("BanGroupMember", new VRChatGroupMember())
            .Returns("UnbanGroupMember", new VRChatGroupMember());

    private static async Task<Guid> ReasonAsync(ReadSurfaceTestHost host, string cookie, CancellationToken ct)
    {
        var list = await host.GetJsonAsync<BanReasonListResponse>("/api/settings/ban-reasons", cookie, ct);
        return list.Reasons.First(r => r.Label == "Harassment").Id;
    }

    private static object Body(string userId, string key, Guid? reason = null, string note = "")
        => new
        {
            userId,
            key,
            reasonIds = reason is { } id ? new[] { id } : [],
            note,
        };

    private static async Task<ModerationActionResult> ResultOf(HttpResponseMessage response, CancellationToken ct)
        => JsonSerializer.Deserialize<ModerationActionResult>(await response.Content.ReadAsStringAsync(ct), Web)!;

    private static async Task<List<ModbotEvent>> FactsAboutAsync(
        ReadSurfaceTestHost host, string userId, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        return await db.Events.AsNoTracking()
            .Where(e => e.SubjectId == userId)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);
    }

    // ── Permissions ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EachAction_NeedsItsOwnPermission()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, Accepting());
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        // Somebody who may see members and nothing else may do none of the three.
        var onlooker = await host.SignedInAsync(ModbotPermissions.ViewMembers, ct);

        foreach (var action in new[] { "kick", "ban", "unban" })
        {
            var refused = await host.PostJsonAsync($"/api/moderation/{action}", Body(Person, Guid.NewGuid().ToString()), onlooker, ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        // And each flag opens exactly one of them.
        await AllowsOnlyAsync(host, ModbotPermissions.Kick, "kick", ct);
        await AllowsOnlyAsync(host, ModbotPermissions.Unban, "unban", ct);
    }

    private static async Task AllowsOnlyAsync(
        ReadSurfaceTestHost host, ModbotPermissions held, string allowed, CancellationToken ct)
    {
        var cookie = await host.SignedInAsync(held, ct);

        foreach (var action in new[] { "kick", "ban", "unban" })
        {
            var response = await host.PostJsonAsync(
                $"/api/moderation/{action}", Body(Person, Guid.NewGuid().ToString()), cookie, ct);

            if (action == allowed)
                Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
            else
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task Banning_NeedsTheBanPermission_AndTheKickPermissionIsNotEnough()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, Accepting());
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var kicker = await host.SignedInAsync(ModbotPermissions.Kick, ct);
        var refused = await host.PostJsonAsync("/api/moderation/ban", Body(Person, Guid.NewGuid().ToString()), kicker, ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Empty(await FactsAboutAsync(host, Person, ct));
    }

    // ── Nothing is recorded unless VRChat accepted ─────────────────────────────────────────

    [Fact]
    public async Task AnAcceptedBan_WritesTheFactAndTheCaseFile_AndMarksThePersonBanned()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, Accepting());
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban, ct);
        var reason = await ReasonAsync(host, cookie, ct);

        var response = await host.PostJsonAsync(
            "/api/moderation/ban", Body(Person, "key-ban-1", reason, "Followed people shouting slurs."), cookie, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await ResultOf(response, ct);
        Assert.True(result.Done);
        Assert.Null(result.Error);
        Assert.NotNull(result.CaseId);

        var fact = Assert.Single(await FactsAboutAsync(host, Person, ct), e => e.Type == FactType.ActionBan);
        var data = JsonDocument.Parse(fact.Data ?? "{}").RootElement;

        Assert.Equal("ban", data.GetProperty("action").GetString());
        Assert.Equal("Followed people shouting slurs.", data.GetProperty("note").GetString());
        Assert.Equal("Harassment", data.GetProperty("reasonLabels")[0].GetString());
        Assert.Equal(FactPlatform.VRChat, fact.SubjectPlatform);
        Assert.Equal(FactPlatform.Modbot, fact.ActorPlatform);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        // The case file cites the ban Modbot just performed, not a ban the audit log has not
        // published yet.
        var caseFile = await db.CaseFiles.AsNoTracking().SingleAsync(c => c.Id == result.CaseId, ct);
        Assert.Equal(fact.Id, caseFile.BanFactId);
        Assert.Equal("Followed people shouting slurs.", caseFile.WrittenReason);

        // What Modbot stores is right straight away: on the ban list, and off the member list.
        var ban = await db.GroupBans.AsNoTracking().SingleAsync(b => b.UserId == Person, ct);
        Assert.Null(ban.LiftedAt);
        Assert.Equal(Day, ban.BannedAt);

        var member = await db.GroupMembers.AsNoTracking().SingleAsync(m => m.UserId == Person, ct);
        Assert.Equal(Day, member.LeftAt);
    }

    [Fact]
    public async Task AnAcceptedKick_MarksTheMemberGone_AndWritesNoCaseFile()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, Accepting());
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Kick, ct);
        var result = await ResultOf(
            await host.PostJsonAsync("/api/moderation/kick", Body(Person, "key-kick-1"), cookie, ct), ct);

        Assert.True(result.Done);
        Assert.Null(result.CaseId);

        Assert.Single(await FactsAboutAsync(host, Person, ct), e => e.Type == FactType.ActionKick);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        Assert.Equal(Day, (await db.GroupMembers.AsNoTracking().SingleAsync(m => m.UserId == Person, ct)).LeftAt);
        Assert.Empty(await db.CaseFiles.AsNoTracking().ToListAsync(ct));
    }

    [Fact]
    public async Task AnAcceptedUnban_LiftsTheBan()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, Accepting());
        await host.ResetAsync(ct);
        await SeedAsync(host, ct, banned: true);

        var cookie = await host.SignedInAsync(ModbotPermissions.Unban, ct);
        var result = await ResultOf(
            await host.PostJsonAsync("/api/moderation/unban", Body(Person, "key-unban-1"), cookie, ct), ct);

        Assert.True(result.Done);
        Assert.Single(await FactsAboutAsync(host, Person, ct), e => e.Type == FactType.ActionUnban);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        Assert.Equal(Day, (await db.GroupBans.AsNoTracking().SingleAsync(b => b.UserId == Person, ct)).LiftedAt);
    }

    // ── Banning somebody who is not in the group ───────────────────────────────────────────

    /// <summary>
    /// The case the whole of this section exists for: a moderator hears about somebody from
    /// another group, from Discord or from a flag, and keeps them out before they ever arrive.
    /// VRChat's group ban takes a user id, not a membership, so nothing about this is unusual —
    /// and nothing in Modbot may quietly assume the member row is there.
    /// </summary>
    [Fact]
    public async Task ABanOnSomebodyWhoIsNotAMember_GoesThrough_AndWritesTheBanListRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = Accepting();

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct, member: false);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban, ct);
        var reason = await ReasonAsync(host, cookie, ct);

        var result = await ResultOf(
            await host.PostJsonAsync(
                "/api/moderation/ban",
                Body(Person, "key-ban-stranger", reason, "Named in another group's warning."),
                cookie,
                ct),
            ct);

        Assert.True(result.Done);
        Assert.Null(result.Error);
        Assert.NotNull(result.CaseId);

        Assert.Single(gate.Calls, c => c.Endpoint.Operation == "BanGroupMember");

        var fact = Assert.Single(await FactsAboutAsync(host, Person, ct), e => e.Type == FactType.ActionBan);
        Assert.Equal(Person, fact.SubjectId);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        // The ban list holds them straight away, so the Bans page is right on its next load and
        // the sweep only has to confirm it.
        var ban = await db.GroupBans.AsNoTracking().SingleAsync(b => b.UserId == Person, ct);
        Assert.Null(ban.LiftedAt);
        Assert.Equal(Day, ban.BannedAt);

        // And no member row was invented to hang the ban off.
        Assert.Empty(await db.GroupMembers.AsNoTracking().ToListAsync(ct));

        // The write-up is there, with nothing about a membership there never was.
        var caseFile = await db.CaseFiles.AsNoTracking().SingleAsync(c => c.Id == result.CaseId, ct);
        Assert.Equal(fact.Id, caseFile.BanFactId);
        Assert.Null(caseFile.MembershipAtBan);
    }

    /// <summary>
    /// Somebody Modbot has no profile row for at all — the id arrived from outside. The ban
    /// records them as a person so the Bans page has somebody to name rather than a bare id.
    /// </summary>
    [Fact]
    public async Task ABanOnSomebodyModbotHasNeverSeen_RecordsThemAsAPerson()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = Accepting();

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct, member: false);

        using (var before = host.Services.CreateScope())
        {
            var db = before.ServiceProvider.GetRequiredService<ModbotContext>();
            Assert.Empty(await db.VRChatUsers.AsNoTracking().Where(u => u.UserId == Person).ToListAsync(ct));
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban, ct);
        var reason = await ReasonAsync(host, cookie, ct);

        var result = await ResultOf(
            await host.PostJsonAsync("/api/moderation/ban", Body(Person, "key-ban-stranger-2", reason), cookie, ct), ct);

        Assert.True(result.Done);

        using var scope = host.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        // The id and that Modbot has now seen it. The name and the picture arrive when the
        // profile sync reaches the request the ban queued -- nothing is fetched in the request.
        var person = await db2.VRChatUsers.AsNoTracking().SingleAsync(u => u.UserId == Person, ct);
        Assert.Equal(Day, person.LastSeenAt);
        Assert.Null(person.DisplayName);
        Assert.Null(person.LastRefreshedAt);
    }

    /// <summary>
    /// A ban still needs somebody real behind the id. VRChat's 404 reaches the moderator as a
    /// sentence, not as "VRChat returned 404 for groups.moderate/...".
    /// </summary>
    [Fact]
    public async Task ABanOnAnIdVRChatDoesNotKnow_SaysSo_AndRecordsNothingAsDone()
    {
        var ct = TestContext.Current.CancellationToken;

        var gate = new FakeVRChatGate()
            .SignedInAs()
            .Returns("BanGroupMember", VRChatResult<VRChatGroupMember>.Failure(404, "404 Not Found"));

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct, member: false);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban, ct);
        var reason = await ReasonAsync(host, cookie, ct);

        var result = await ResultOf(
            await host.PostJsonAsync("/api/moderation/ban", Body(Person, "key-ban-nobody", reason), cookie, ct), ct);

        Assert.False(result.Done);
        Assert.Equal("VRChat has no account with that id.", result.Error);
        Assert.Null(result.CaseId);

        var facts = await FactsAboutAsync(host, Person, ct);
        Assert.Single(facts, e => e.Type == FactType.ActionFailed);
        Assert.DoesNotContain(facts, e => e.Type == FactType.ActionBan);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        Assert.Empty(await db.GroupBans.AsNoTracking().ToListAsync(ct));
        Assert.Empty(await db.CaseFiles.AsNoTracking().ToListAsync(ct));
    }

    // ── Kick and unban are different questions ─────────────────────────────────────────────

    /// <summary>
    /// Kicking somebody who is not in the group is meaningless, and Modbot lets VRChat say so
    /// rather than refusing from its own member list: that list rests fifteen minutes between
    /// sweeps, and a check against it would block the kick of somebody who joined a minute ago.
    /// </summary>
    [Fact]
    public async Task AKickOnSomebodyWhoIsNotInTheGroup_IsSentAndRefused_AndReadsAsWhatItIs()
    {
        var ct = TestContext.Current.CancellationToken;

        var gate = new FakeVRChatGate()
            .SignedInAs()
            .Returns("KickGroupMember", VRChatResult<VRChatSuccess>.Failure(404, "404 Not Found"));

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct, member: false);

        var cookie = await host.SignedInAsync(ModbotPermissions.Kick, ct);

        var result = await ResultOf(
            await host.PostJsonAsync("/api/moderation/kick", Body(Person, "key-kick-stranger"), cookie, ct), ct);

        Assert.False(result.Done);
        Assert.Equal("VRChat says they are not in the group.", result.Error);
        Assert.False(result.RateLimited);

        // Sent, not pre-refused: Modbot's member list does not get to decide this.
        Assert.Single(gate.Calls, c => c.Endpoint.Operation == "KickGroupMember");
        Assert.Single(await FactsAboutAsync(host, Person, ct), e => e.Type == FactType.ActionFailed);
    }

    /// <summary>
    /// Unbanning somebody who was never banned is meaningless in the other direction: there is no
    /// ban to lift. VRChat says so, and nothing is written to the ban list to say otherwise.
    /// </summary>
    [Fact]
    public async Task AnUnbanOnSomebodyWhoIsNotBanned_IsSentAndRefused_AndWritesNoBanRow()
    {
        var ct = TestContext.Current.CancellationToken;

        var gate = new FakeVRChatGate()
            .SignedInAs()
            .Returns("UnbanGroupMember", VRChatResult<VRChatGroupMember>.Failure(404, "404 Not Found"));

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Unban, ct);

        var result = await ResultOf(
            await host.PostJsonAsync("/api/moderation/unban", Body(Person, "key-unban-nothing"), cookie, ct), ct);

        Assert.False(result.Done);
        Assert.Equal("VRChat says they are not banned.", result.Error);

        Assert.Single(gate.Calls, c => c.Endpoint.Operation == "UnbanGroupMember");
        Assert.Single(await FactsAboutAsync(host, Person, ct), e => e.Type == FactType.ActionFailed);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        Assert.Empty(await db.GroupBans.AsNoTracking().ToListAsync(ct));
    }

    // ── A refusal is a refusal ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenVRChatRefuses_NothingIsRecordedAsDone_AndTheModeratorIsToldWhatVRChatSaid()
    {
        var ct = TestContext.Current.CancellationToken;

        var gate = new FakeVRChatGate()
            .SignedInAs()
            .Returns("BanGroupMember", VRChatResult<VRChatGroupMember>.Failure(
                403, "You do not have permission to ban members of this group."));

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban, ct);
        var reason = await ReasonAsync(host, cookie, ct);

        var result = await ResultOf(
            await host.PostJsonAsync("/api/moderation/ban", Body(Person, "key-refused", reason), cookie, ct), ct);

        Assert.False(result.Done);
        Assert.Equal("You do not have permission to ban members of this group.", result.Error);
        Assert.Null(result.CaseId);

        var facts = await FactsAboutAsync(host, Person, ct);

        // A failure, and nothing that a query for "who was banned" could ever count.
        Assert.Single(facts, e => e.Type == FactType.ActionFailed);
        Assert.DoesNotContain(facts, e => e.Type == FactType.ActionBan);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        Assert.Empty(await db.CaseFiles.AsNoTracking().ToListAsync(ct));
        Assert.Empty(await db.GroupBans.AsNoTracking().ToListAsync(ct));
        Assert.Null((await db.GroupMembers.AsNoTracking().SingleAsync(m => m.UserId == Person, ct)).LeftAt);
    }

    [Fact]
    public async Task A429_IsAColdStop_NotARetry_AndTheActionSimplyFailed()
    {
        var ct = TestContext.Current.CancellationToken;

        var gate = new FakeVRChatGate()
            .SignedInAs()
            .Returns("KickGroupMember", VRChatResult<VRChatSuccess>.Failure(
                429, "VRChat rate limited this request.", kind: VRChatFailureKind.RateLimited));

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Kick, ct);
        var result = await ResultOf(
            await host.PostJsonAsync("/api/moderation/kick", Body(Person, "key-429"), cookie, ct), ct);

        Assert.False(result.Done);
        Assert.True(result.RateLimited);

        // Exactly one request was issued. A 429 is never retried (spec 4.3.1): retrying it would
        // lengthen the penalty VRChat is already applying.
        Assert.Single(gate.Calls, c => c.Endpoint.Class == VRChatEndpointClass.GroupsModerate);

        Assert.Single(await FactsAboutAsync(host, Person, ct), e => e.Type == FactType.ActionFailed);
    }

    [Fact]
    public async Task WhenABucketIsColdStopped_NothingIsSent_AndItStillReadsAsARateLimit()
    {
        var ct = TestContext.Current.CancellationToken;

        // A cold stop sends nothing at all, so the gate reports status 0 rather than a 429 it
        // never received.
        var gate = new FakeVRChatGate()
            .SignedInAs()
            .Returns("BanGroupMember", VRChatResult<VRChatGroupMember>.Failure(
                0, "Waiting out a VRChat rate limit.", kind: VRChatFailureKind.RateLimited));

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban, ct);
        var reason = await ReasonAsync(host, cookie, ct);

        var result = await ResultOf(
            await host.PostJsonAsync("/api/moderation/ban", Body(Person, "key-cold", reason), cookie, ct), ct);

        Assert.False(result.Done);
        Assert.True(result.RateLimited);
        Assert.Equal("Waiting out a VRChat rate limit.", result.Error);
    }

    // ── Safety ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ModbotWillNotActOnTheAccountItSignsInAs()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = Accepting();

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Administrator, ct);
        var reason = await ReasonAsync(host, cookie, ct);

        foreach (var action in new[] { "kick", "ban", "unban" })
        {
            var response = await host.PostJsonAsync(
                $"/api/moderation/{action}", Body(ModbotAccount, Guid.NewGuid().ToString(), reason), cookie, ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // Nothing was sent, and nothing was recorded.
        Assert.DoesNotContain(gate.Calls, c => c.Endpoint.Class == VRChatEndpointClass.GroupsModerate);
        Assert.Empty(await FactsAboutAsync(host, ModbotAccount, ct));
    }

    [Fact]
    public async Task APersonWithNoVRChatId_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = Accepting();

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Administrator, ct);
        var reason = await ReasonAsync(host, cookie, ct);

        foreach (var missing in new[] { "", "   " })
        {
            var response = await host.PostJsonAsync(
                "/api/moderation/ban", Body(missing, Guid.NewGuid().ToString(), reason), cookie, ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.DoesNotContain(gate.Calls, c => c.Endpoint.Class == VRChatEndpointClass.GroupsModerate);
    }

    [Fact]
    public async Task ABanWithNoReason_IsRefusedBeforeAnythingIsSent()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = Accepting();

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban, ct);

        var response = await host.PostJsonAsync("/api/moderation/ban", Body(Person, "key-no-reason"), cookie, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(gate.Calls, c => c.Endpoint.Class == VRChatEndpointClass.GroupsModerate);
    }

    [Fact]
    public async Task AReasonThatNeedsANote_IsRefusedWithoutOne_BeforeAnythingIsSent()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = Accepting();

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban, ct);

        // "Other" is the one reason the default list marks as needing the written reason: a case
        // file that says only "other" says nothing.
        var list = await host.GetJsonAsync<BanReasonListResponse>("/api/settings/ban-reasons", cookie, ct);
        var other = list.Reasons.First(r => r.NeedsWrittenReason).Id;

        var refused = await host.PostJsonAsync("/api/moderation/ban", Body(Person, "key-other-blank", other), cookie, ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.DoesNotContain(gate.Calls, c => c.Endpoint.Class == VRChatEndpointClass.GroupsModerate);

        // With a note it goes through, and the case file carries the moderator's words.
        var done = await ResultOf(
            await host.PostJsonAsync(
                "/api/moderation/ban", Body(Person, "key-other-noted", other, "Repeatedly crashed the instance."), cookie, ct),
            ct);

        Assert.True(done.Done);
        Assert.NotNull(done.CaseId);
    }

    // ── Each action its own reasons (M4 §9) ────────────────────────────────────────────────

    private static async Task<Guid> ReasonAsync(ReadSurfaceTestHost host, string cookie, string label, string usedFor, CancellationToken ct)
    {
        var list = await host.GetJsonAsync<BanReasonListResponse>("/api/settings/ban-reasons", cookie, ct);
        return list.Reasons.Single(r => r.Label == label && r.UsedFor.Contains(usedFor)).Id;
    }

    private static async Task RequireReasonsAsync(ReadSurfaceTestHost host, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        (await db.GetSettingsAsync(ct)).RequireModerationClassification = true;
        await db.SaveChangesAsync(ct);
    }

    [Fact]
    public async Task AnUnban_TakesOnlyTheReasonsForLiftingABan_AndABanOnlyThoseForBanning()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = Accepting();

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct, banned: true);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban | ModbotPermissions.Unban, ct);
        var harassment = await ReasonAsync(host, cookie, "Harassment", "ban", ct);
        var mistake = await ReasonAsync(host, cookie, "Mistake", "unban", ct);

        // "Harassment" is no answer to why a ban was lifted, and "Mistake" no reason to ban.
        var unbanWrong = await host.PostJsonAsync("/api/moderation/unban", Body(Person, "key-unban-wrong", harassment), cookie, ct);
        Assert.Equal(HttpStatusCode.BadRequest, unbanWrong.StatusCode);
        Assert.Contains("Harassment", await unbanWrong.Content.ReadAsStringAsync(ct));

        var banWrong = await host.PostJsonAsync("/api/moderation/ban", Body("usr_other", "key-ban-wrong", mistake), cookie, ct);
        Assert.Equal(HttpStatusCode.BadRequest, banWrong.StatusCode);

        Assert.DoesNotContain(gate.Calls, c => c.Endpoint.Class == VRChatEndpointClass.GroupsModerate);

        var done = await ResultOf(
            await host.PostJsonAsync("/api/moderation/unban", Body(Person, "key-unban-right", mistake), cookie, ct), ct);
        Assert.True(done.Done);

        var fact = Assert.Single(await FactsAboutAsync(host, Person, ct), e => e.Type == FactType.ActionUnban);
        Assert.Equal("Mistake", JsonDocument.Parse(fact.Data ?? "{}").RootElement.GetProperty("reasonLabels")[0].GetString());
    }

    [Fact]
    public async Task WithReasonsRequired_AKickAndAnUnbanNeedOne_AndWithoutIt_TheyDoNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = Accepting();

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct, banned: true);
        await RequireReasonsAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Kick | ModbotPermissions.Unban, ct);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostJsonAsync("/api/moderation/unban", Body(Person, "key-unban-bare"), cookie, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostJsonAsync("/api/moderation/kick", Body(Person, "key-kick-bare"), cookie, ct)).StatusCode);
        Assert.DoesNotContain(gate.Calls, c => c.Endpoint.Class == VRChatEndpointClass.GroupsModerate);

        var timeServed = await ReasonAsync(host, cookie, "Time served", "unban", ct);
        var unbanned = await ResultOf(
            await host.PostJsonAsync("/api/moderation/unban", Body(Person, "key-unban-reason", timeServed), cookie, ct), ct);
        Assert.True(unbanned.Done);

        var spam = await ReasonAsync(host, cookie, "Spam", "kick", ct);
        var kicked = await ResultOf(
            await host.PostJsonAsync("/api/moderation/kick", Body(Person, "key-kick-reason", spam), cookie, ct), ct);
        Assert.True(kicked.Done);
    }

    [Fact]
    public async Task AnUnbanReasonThatNeedsANote_IsRefusedWithoutOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = Accepting();

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct, banned: true);

        var cookie = await host.SignedInAsync(ModbotPermissions.Unban, ct);
        var other = await ReasonAsync(host, cookie, "Other", "unban", ct);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostJsonAsync("/api/moderation/unban", Body(Person, "key-unban-other", other), cookie, ct)).StatusCode);
        Assert.DoesNotContain(gate.Calls, c => c.Endpoint.Class == VRChatEndpointClass.GroupsModerate);

        var done = await ResultOf(
            await host.PostJsonAsync("/api/moderation/unban", Body(Person, "key-unban-other-noted", other, "Banned the wrong twin."), cookie, ct), ct);
        Assert.True(done.Done);
    }

    // ── The case file learns its ban was lifted ────────────────────────────────────────────

    [Fact]
    public async Task AnUnban_MarksTheBansCaseFileLifted_WithWhyAndWho_AndTheFactNamesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, Accepting());
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban | ModbotPermissions.Unban | ModbotPermissions.ViewProfile, ct);
        var harassment = await ReasonAsync(host, cookie, "Harassment", "ban", ct);
        var appeal = await ReasonAsync(host, cookie, "Appeal upheld", "unban", ct);

        var banned = await ResultOf(
            await host.PostJsonAsync("/api/moderation/ban", Body(Person, "key-lift-ban", harassment, "Shouted slurs."), cookie, ct), ct);
        var caseId = Assert.IsType<Guid>(banned.CaseId);

        host.Clock.UtcNow = Day.AddDays(3);

        var lifted = await ResultOf(
            await host.PostJsonAsync("/api/moderation/unban", Body(Person, "key-lift-unban", appeal, "Apologised to both of them."), cookie, ct), ct);

        Assert.True(lifted.Done);
        Assert.Equal(caseId, lifted.CaseId);
        Assert.Null(lifted.CaseFileError);

        var unban = Assert.Single(await FactsAboutAsync(host, Person, ct), e => e.Type == FactType.ActionUnban);
        Assert.Equal(caseId.ToString(), JsonDocument.Parse(unban.Data ?? "{}").RootElement.GetProperty("caseId").GetString());

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var row = await db.CaseFiles.AsNoTracking().SingleAsync(c => c.Id == caseId, ct);

            Assert.Equal(Day.AddDays(3), row.LiftedAt);
            Assert.Equal(unban.Id, row.UnbanFactId);
            Assert.Equal("Apologised to both of them.", row.LiftNote);
            Assert.Contains(appeal.ToString(), row.LiftReasonIds);

            // What it said about the ban itself is untouched.
            Assert.Equal("Shouted slurs.", row.WrittenReason);
            Assert.Contains(harassment.ToString(), row.ReasonIds);
        }

        var view = await host.GetJsonAsync<CaseFileView>($"/api/cases/{caseId}", cookie, ct);
        Assert.NotNull(view.Lifted);
        Assert.Equal(["Appeal upheld"], view.Lifted!.Reasons.Select(r => r.Label));
        Assert.Equal(unban.Id, view.Lifted.UnbanFactId);
        Assert.Equal(["Harassment"], view.Reasons.Select(r => r.Label));

        // A second unban later does not lift it again, or move the day it was lifted.
        host.Clock.UtcNow = Day.AddDays(5);
        await host.PostJsonAsync("/api/moderation/unban", Body(Person, "key-lift-unban-again"), cookie, ct);

        var again = await host.GetJsonAsync<CaseFileView>($"/api/cases/{caseId}", cookie, ct);
        Assert.Equal(Day.AddDays(3), again.Lifted!.At);
    }

    [Fact]
    public async Task AnUnbanWithNoCaseFile_MarksNothing_AndStillSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, Accepting());
        await host.ResetAsync(ct);
        await SeedAsync(host, ct, banned: true);

        var cookie = await host.SignedInAsync(ModbotPermissions.Unban, ct);
        var result = await ResultOf(
            await host.PostJsonAsync("/api/moderation/unban", Body(Person, "key-unban-no-case"), cookie, ct), ct);

        Assert.True(result.Done);
        Assert.Null(result.CaseId);
        Assert.Null(result.CaseFileError);

        var fact = Assert.Single(await FactsAboutAsync(host, Person, ct), e => e.Type == FactType.ActionUnban);
        Assert.False(JsonDocument.Parse(fact.Data ?? "{}").RootElement.TryGetProperty("caseId", out _));
    }

    /// <summary>
    /// A ban lifted in VRChat, where Modbot could not tie it to the case file, and then a fresh ban
    /// with no write-up: an unban from Modbot now is about the new ban, and must not put its
    /// reasons on the old case file's lift, which happened earlier and for reasons nobody gave.
    /// </summary>
    [Fact]
    public async Task ACaseFileWhoseBanWasAlreadyLiftedInVRChat_IsLeftAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, Accepting());
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban | ModbotPermissions.Unban, ct);
        var harassment = await ReasonAsync(host, cookie, "Harassment", "ban", ct);

        var banned = await ResultOf(
            await host.PostJsonAsync("/api/moderation/ban", Body(Person, "key-old-ban", harassment), cookie, ct), ct);
        var caseId = Assert.IsType<Guid>(banned.CaseId);

        await host.WriteFactAsync(
            Modbot.Api.Tests.Features.Analytics.AnalyticsFacts.AuditFact(FactType.MemberUnbanned, Person, Day.AddDays(1)), ct);

        host.Clock.UtcNow = Day.AddDays(4);

        var result = await ResultOf(
            await host.PostJsonAsync("/api/moderation/unban", Body(Person, "key-new-unban"), cookie, ct), ct);

        Assert.True(result.Done);
        Assert.Null(result.CaseId);

        using var check = host.Services.CreateScope();
        var after = await check.ServiceProvider.GetRequiredService<ModbotContext>().CaseFiles.AsNoTracking().SingleAsync(c => c.Id == caseId, ct);
        Assert.Null(after.LiftedAt);
    }

    // ── One confirmation, one action ───────────────────────────────────────────────────────

    [Fact]
    public async Task TheSameKeyTwice_SendsOneRequest_AndRecordsOneFact()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = Accepting();

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban, ct);
        var reason = await ReasonAsync(host, cookie, ct);
        var body = Body(Person, "key-pressed-twice", reason);

        var first = await ResultOf(await host.PostJsonAsync("/api/moderation/ban", body, cookie, ct), ct);
        var second = await ResultOf(await host.PostJsonAsync("/api/moderation/ban", body, cookie, ct), ct);

        Assert.True(first.Done);
        Assert.False(first.Repeat);

        // The second press gets the first one's answer rather than banning again.
        Assert.True(second.Done);
        Assert.True(second.Repeat);
        Assert.Equal(first.CaseId, second.CaseId);

        Assert.Single(gate.Calls, c => c.Endpoint.Operation == "BanGroupMember");
        Assert.Single(await FactsAboutAsync(host, Person, ct), e => e.Type == FactType.ActionBan);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        Assert.Single(await db.ModerationActions.AsNoTracking().ToListAsync(ct));
        Assert.Single(await db.CaseFiles.AsNoTracking().ToListAsync(ct));
    }

    [Fact]
    public async Task AnActionWithNoKey_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = Accepting();

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Kick, ct);

        var response = await host.PostJsonAsync("/api/moderation/kick", Body(Person, ""), cookie, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(gate.Calls, c => c.Endpoint.Class == VRChatEndpointClass.GroupsModerate);
    }

    // ── Through the gate, on its own budget ────────────────────────────────────────────────

    [Fact]
    public async Task EveryActionGoesThroughTheGate_OnItsOwnEndpointClass_AtInteractivePriority()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = Accepting();

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct, banned: true);

        var cookie = await host.SignedInAsync(ModbotPermissions.Administrator, ct);
        var reason = await ReasonAsync(host, cookie, ct);

        await host.PostJsonAsync("/api/moderation/unban", Body(Person, "g-1", reason), cookie, ct);
        await host.PostJsonAsync("/api/moderation/kick", Body(Person, "g-2", reason), cookie, ct);

        var calls = gate.Calls.Where(c => c.Endpoint.Class == VRChatEndpointClass.GroupsModerate).ToList();

        Assert.Equal(2, calls.Count);

        foreach (var call in calls)
        {
            // Resource-scoped on the managed group, and ahead of any queued sync: a moderator is
            // watching a spinner (spec 4.3.3).
            Assert.Equal(Group, call.Endpoint.ResourceId);
            Assert.Equal(VRChatCallPriority.Interactive, call.Priority);
        }
    }

    [Fact]
    public async Task WithNoGroupSetUpYet_TheActionIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = Accepting();

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Kick, ct);

        var response = await host.PostJsonAsync("/api/moderation/kick", Body(Person, "key-nogroup"), cookie, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain(gate.Calls, c => c.Endpoint.Class == VRChatEndpointClass.GroupsModerate);
    }

    // ── A ban made through Modbot lands on the linked Discord account too ──────────────────

    private static Task<ReadSurfaceTestHost> StartWithDiscordAsync(
        PostgresFixture db, FakeVRChatGate gate, RecordingLinkedDiscord discord)
        => ReadSurfaceTestHost.StartAsync(
            db, gate, configure: services => services.AddScoped<ILinkedDiscordBans>(_ => discord));

    [Fact]
    public async Task ABan_AlsoBansTheLinkedDiscordAccount_AndSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var discord = new RecordingLinkedDiscord();

        await using var host = await StartWithDiscordAsync(_db, Accepting(), discord);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban, ct);
        var reason = await ReasonAsync(host, cookie, ct);

        var result = await ResultOf(
            await host.PostJsonAsync("/api/moderation/ban", Body(Person, "key-discord-1", reason), cookie, ct), ct);

        Assert.True(result.Done);
        Assert.True(result.DiscordDone);
        Assert.Null(result.DiscordError);

        var (banned, why, causedBy) = Assert.Single(discord.Banned);
        Assert.Equal(Person, banned);
        Assert.Equal("Harassment", why);

        // It cites the ban Modbot recorded, so the audit log ties the two together.
        var fact = Assert.Single(await FactsAboutAsync(host, Person, ct), e => e.Type == FactType.ActionBan);
        Assert.Equal(fact.Id, causedBy);
    }

    /// <summary>
    /// Discord saying no is shown, and the VRChat ban stands: the ban is on the ban list and its
    /// case file is written whatever the bot was allowed to do.
    /// </summary>
    [Fact]
    public async Task ADiscordRefusal_IsShown_AndTheVRChatBanStands()
    {
        var ct = TestContext.Current.CancellationToken;
        var discord = new RecordingLinkedDiscord { Answer = LinkedDiscordOutcome.Failed("The bot may not ban in this server.") };

        await using var host = await StartWithDiscordAsync(_db, Accepting(), discord);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban, ct);
        var reason = await ReasonAsync(host, cookie, ct);

        var result = await ResultOf(
            await host.PostJsonAsync("/api/moderation/ban", Body(Person, "key-discord-2", reason), cookie, ct), ct);

        Assert.True(result.Done);
        Assert.False(result.DiscordDone);
        Assert.Equal("The bot may not ban in this server.", result.DiscordError);
        Assert.NotNull(result.CaseId);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        Assert.Null((await db.GroupBans.AsNoTracking().SingleAsync(b => b.UserId == Person, ct)).LiftedAt);
    }

    [Fact]
    public async Task ADiscordSideThatFallsOver_NeverTakesTheBanDownWithIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var discord = new RecordingLinkedDiscord { Throws = true };

        await using var host = await StartWithDiscordAsync(_db, Accepting(), discord);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban, ct);
        var reason = await ReasonAsync(host, cookie, ct);

        var response = await host.PostJsonAsync("/api/moderation/ban", Body(Person, "key-discord-3", reason), cookie, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await ResultOf(response, ct);
        Assert.True(result.Done);
        Assert.False(result.DiscordDone);
        Assert.Equal("Modbot could not reach Discord.", result.DiscordError);
    }

    [Fact]
    public async Task ABanVRChatRefused_IsNeverAskedOfDiscord()
    {
        var ct = TestContext.Current.CancellationToken;
        var discord = new RecordingLinkedDiscord();

        var gate = new FakeVRChatGate()
            .SignedInAs()
            .Returns("BanGroupMember", VRChatResult<VRChatGroupMember>.Failure(
                403, "You do not have permission to ban members of this group."));

        await using var host = await StartWithDiscordAsync(_db, gate, discord);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban, ct);
        var reason = await ReasonAsync(host, cookie, ct);

        var result = await ResultOf(
            await host.PostJsonAsync("/api/moderation/ban", Body(Person, "key-discord-4", reason), cookie, ct), ct);

        Assert.False(result.Done);
        Assert.False(result.DiscordDone);
        Assert.Null(result.DiscordError);
        Assert.Empty(discord.Banned);
    }

    [Fact]
    public async Task AnUnban_AlsoLiftsTheLinkedDiscordBan_ButAKickTouchesNothingInDiscord()
    {
        var ct = TestContext.Current.CancellationToken;
        var discord = new RecordingLinkedDiscord();

        await using var host = await StartWithDiscordAsync(_db, Accepting(), discord);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct, banned: true);

        var unbanner = await host.SignedInAsync(ModbotPermissions.Unban, ct);
        var unbanned = await ResultOf(
            await host.PostJsonAsync("/api/moderation/unban", Body(Person, "key-discord-5"), unbanner, ct), ct);

        Assert.True(unbanned.Done);
        Assert.True(unbanned.DiscordDone);
        Assert.Equal(Person, Assert.Single(discord.Unbanned));

        var kicker = await host.SignedInAsync(ModbotPermissions.Kick, ct);
        var kicked = await ResultOf(
            await host.PostJsonAsync("/api/moderation/kick", Body(Person, "key-discord-6"), kicker, ct), ct);

        Assert.True(kicked.Done);
        Assert.False(kicked.DiscordDone);
        Assert.Empty(discord.Banned);
        Assert.Single(discord.Unbanned);
    }

    /// <summary>
    /// "They are not banned" from VRChat still lifts the Discord ban: a ban made through Modbot is
    /// lifted everywhere, and the moderator is told VRChat had nothing to lift and Discord did.
    /// </summary>
    [Fact]
    public async Task AnUnbanVRChatSaysIsNotBanned_StillLiftsTheDiscordBan()
    {
        var ct = TestContext.Current.CancellationToken;
        var discord = new RecordingLinkedDiscord();

        var gate = new FakeVRChatGate()
            .SignedInAs()
            .Returns("UnbanGroupMember", VRChatResult<VRChatGroupMember>.Failure(404, "404 Not Found"));

        await using var host = await StartWithDiscordAsync(_db, gate, discord);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Unban, ct);
        var result = await ResultOf(
            await host.PostJsonAsync("/api/moderation/unban", Body(Person, "key-discord-8"), cookie, ct), ct);

        Assert.False(result.Done);
        Assert.Equal("VRChat says they are not banned.", result.Error);
        Assert.True(result.DiscordDone);
        Assert.Equal(Person, Assert.Single(discord.Unbanned));
    }

    /// <summary>Any other VRChat refusal changed nothing, so Discord is not asked.</summary>
    [Fact]
    public async Task AnUnbanVRChatRefusedForAnotherReason_IsNotAskedOfDiscord()
    {
        var ct = TestContext.Current.CancellationToken;
        var discord = new RecordingLinkedDiscord();

        var gate = new FakeVRChatGate()
            .SignedInAs()
            .Returns("UnbanGroupMember", VRChatResult<VRChatGroupMember>.Failure(403, "No permission."));

        await using var host = await StartWithDiscordAsync(_db, gate, discord);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Unban, ct);
        var result = await ResultOf(
            await host.PostJsonAsync("/api/moderation/unban", Body(Person, "key-discord-9"), cookie, ct), ct);

        Assert.False(result.Done);
        Assert.False(result.DiscordDone);
        Assert.Empty(discord.Unbanned);
    }

    /// <summary>A second press of the same key is told what the first press was, Discord line included.</summary>
    [Fact]
    public async Task ARepeatPress_GetsTheSameDiscordAnswerAsTheFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var discord = new RecordingLinkedDiscord { Answer = LinkedDiscordOutcome.Failed("The bot may not ban in this server.") };

        await using var host = await StartWithDiscordAsync(_db, Accepting(), discord);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban, ct);
        var reason = await ReasonAsync(host, cookie, ct);

        var first = await ResultOf(
            await host.PostJsonAsync("/api/moderation/ban", Body(Person, "key-discord-10", reason), cookie, ct), ct);
        var again = await ResultOf(
            await host.PostJsonAsync("/api/moderation/ban", Body(Person, "key-discord-10", reason), cookie, ct), ct);

        Assert.False(first.Repeat);
        Assert.True(again.Repeat);
        Assert.Equal(first.DiscordDone, again.DiscordDone);
        Assert.Equal("The bot may not ban in this server.", again.DiscordError);
        Assert.Single(discord.Banned);
    }

    /// <summary>Somebody with no linked Discord account, or a group with no Discord: nothing said, nothing shown.</summary>
    [Fact]
    public async Task WithNothingToDoInDiscord_TheAnswerSaysNothingAboutIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var discord = new RecordingLinkedDiscord { Answer = LinkedDiscordOutcome.Skipped };

        await using var host = await StartWithDiscordAsync(_db, Accepting(), discord);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban, ct);
        var reason = await ReasonAsync(host, cookie, ct);

        var result = await ResultOf(
            await host.PostJsonAsync("/api/moderation/ban", Body(Person, "key-discord-7", reason), cookie, ct), ct);

        Assert.True(result.Done);
        Assert.False(result.DiscordDone);
        Assert.Null(result.DiscordError);
    }

    private sealed class RecordingLinkedDiscord : ILinkedDiscordBans
    {
        public List<(string VRChatUserId, string? Why, long? CausedBy)> Banned { get; } = [];

        public List<string> Unbanned { get; } = [];

        public bool Throws { get; set; }

        public LinkedDiscordOutcome Answer { get; set; } = new(LinkedDiscordStatus.Done);

        public Task<LinkedDiscordOutcome> BanAsync(
            string vrchatUserId, string by, string? why, long? causedByFactId, CancellationToken ct = default)
        {
            if (Throws)
                throw new InvalidOperationException("Discord fell over.");

            Banned.Add((vrchatUserId, why, causedByFactId));
            return Task.FromResult(Answer);
        }

        public Task<LinkedDiscordOutcome> UnbanAsync(
            string vrchatUserId, string by, string? why, long? causedByFactId, CancellationToken ct = default)
        {
            if (Throws)
                throw new InvalidOperationException("Discord fell over.");

            Unbanned.Add(vrchatUserId);
            return Task.FromResult(Answer);
        }
    }
}
