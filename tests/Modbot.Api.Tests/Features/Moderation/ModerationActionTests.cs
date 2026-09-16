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
    private static async Task SeedAsync(ReadSurfaceTestHost host, CancellationToken ct, bool banned = false)
    {
        host.Clock.UtcNow = Day;

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var settings = await db.GetSettingsAsync(ct);
        settings.ManagedGroupId = Group;
        settings.VRChatSessionUserId = ModbotAccount;

        db.GroupMembers.Add(new GroupMember
        {
            GroupId = Group,
            UserId = Person,
            Roles = "[]",
            JoinedAt = Day.AddDays(-20),
            FirstSeenAt = Day.AddDays(-20),
            LastSeenAt = Day.AddHours(-1),
        });

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
}
