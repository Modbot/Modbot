using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Core.Moderation;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// What the AutoMod tab added (AutoMod design §3, §5, §6): its own route with the old one still
/// answering, the AI tool switches, the VRChat actions a rule may take on a profile match, and
/// the AI opinion on a flag refused while its tool is off.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AutoModActionsTests
{
    private const string Path = "/api/settings/automod";
    private const string OldPath = "/api/settings/ai/moderation";

    private readonly PostgresFixture _db;

    public AutoModActionsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── The route ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheOldPathStillAnswers_WithTheSameBodyAsTheNewOne()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var old = await host.SendJsonAsync(HttpMethod.Get, OldPath, null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, old.StatusCode);

        var fromOld = await JsonAsync(old);
        var fromNew = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct));

        Assert.Equal(fromNew.GetProperty("enabled").GetBoolean(), fromOld.GetProperty("enabled").GetBoolean());
        Assert.Equal(fromNew.GetProperty("aiTools").GetArrayLength(), fromOld.GetProperty("aiTools").GetArrayLength());

        // A nested route too, not only the root.
        var nested = await host.SendJsonAsync(HttpMethod.Get, $"{OldPath}/hub", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, nested.StatusCode);
    }

    // ── The tools ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheToolsStartWhereTheirKindShould_AndAiEnabledFollowsTheBaseSwitch()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var body = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct));

        Assert.False(body.GetProperty("aiEnabled").GetBoolean());

        var tools = body.GetProperty("aiTools").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("on").GetBoolean());

        Assert.True(tools["classify_topics"]);
        Assert.True(tools["check_pictures"]);
        Assert.True(tools["review_flag"]);
        Assert.False(tools["propose_action"]);
    }

    [Fact]
    public async Task ASwitchIsSaved_AndAnUnknownToolIsRefused()
    {
        await using var host = await StartAsync();
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var saved = await JsonAsync(await host.SendJsonAsync(HttpMethod.Put, Path,
            new { enabled = true, aiTools = new Dictionary<string, bool> { ["classify_topics"] = false, ["propose_action"] = true } },
            cookie, Ct));

        var tools = saved.GetProperty("aiTools").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("on").GetBoolean());

        Assert.False(tools["classify_topics"]);
        Assert.True(tools["propose_action"]);
        Assert.True(tools["check_pictures"]);

        // The limit was not sent, so it stayed where it was.
        Assert.Equal(200, saved.GetProperty("dailyAiCallLimit").GetInt32());

        await using (var db = _db.NewContext())
        {
            var settings = await db.GetSettingsAsync(Ct);
            Assert.True(settings.AutoModEnabled);

            // Parsed, not matched as a raw string: Postgres rewrites jsonb on the way in -- spaces
            // after colons, keys reordered -- so a literal substring asserts on Postgres's formatter.
            var savedTools = JsonDocument.Parse(settings.AutoModAiTools).RootElement;
            Assert.False(savedTools.GetProperty("classify_topics").GetBoolean());
            Assert.False(savedTools.TryGetProperty("check_pictures", out _));
        }

        var refused = await host.SendJsonAsync(HttpMethod.Put, Path,
            new { enabled = true, aiTools = new Dictionary<string, bool> { ["summarise_everything"] = true } }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var facts = await host.FactsAsync(FactType.AutoModRuleChanged, user.Id.ToString(), Ct);
        Assert.Single(facts);
    }

    // ── The VRChat actions ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AGroupBanNeedsAProfileTarget()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var refused = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists",
            List("Impersonation", "official modbot", targets: ["discordMessage"], groupBan: true), cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("VRChat profile text", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARuleSetToBanFromTheGroup_BansOnAProfileMatch_OncePastItsTrial()
    {
        var vrchat = new FakeVRChatActions();
        await using var host = await StartAsync(vrchat);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        await SwitchOnAsync(host, cookie);

        var created = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists",
            List("Impersonation", "official modbot", targets: ["displayName"], groupBan: true), cookie, Ct));
        var list = created.GetProperty("list");
        var id = list.GetProperty("id").GetGuid();

        Assert.True(list.GetProperty("groupBan").GetBoolean());
        Assert.Equal(user.Username, list.GetProperty("setToActBy").GetString());
        Assert.Equal(JsonValueKind.Object, list.GetProperty("trial").ValueKind);

        // In the trial: recorded, not done.
        var trial = await CheckAsync(host, new ProfileToCheck("usr_1", "Official Modbot", null, null, null));
        Assert.False(trial.GroupBanned);
        Assert.Empty(vrchat.Banned);

        var ended = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/rules/termList/{id}/end-trial", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);

        var real = await CheckAsync(host, new ProfileToCheck("usr_2", "Official Modbot", null, null, null));
        Assert.True(real.GroupBanned);
        Assert.Equal(["usr_2"], vrchat.Banned);

        var facts = await host.FactsAsync(FactType.AutoModGroupBan, "usr_2", Ct);
        var data = ApiTestHost.DataOf(Assert.Single(facts));
        Assert.True(data.GetProperty("done").GetBoolean());
        Assert.Equal(user.Username, data.GetProperty("rules")[0].GetProperty("setToActByUsername").GetString());

        // Try it says what would happen, and does nothing.
        var tried = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/try",
            new { text = "the official modbot", target = "displayName", includeAi = false }, cookie, Ct));
        Assert.True(tried.GetProperty("wouldGroupBan").GetBoolean());
        Assert.Single(vrchat.Banned);
    }

    // ── Who a VRChat rule never acts on ─────────────────────────────────────────────────────

    private const string Group = "grp_test";
    private const string StaffRole = "grol_staff";

    [Fact]
    public async Task ATeamMembersProvenVRChatAccountIsFlaggedButNeverActedOn_AndAPendingLinkDoesNotCount()
    {
        var vrchat = new FakeVRChatActions();
        await using var host = await StartAsync(vrchat);
        var (team, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var (pendingUser, _) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct, linked: false);
        await SwitchOnAsync(host, cookie);

        await using (var db = _db.NewContext())
        {
            // Somebody who has pasted an id and not yet put the code in their bio: a claim, not a link.
            var row = await db.Users.SingleAsync(u => u.Id == pendingUser.Id, Ct);
            row.VRChatLinkPendingUserId = "usr_pending";
            await db.SaveChangesAsync(Ct);
        }

        await ActingBanListAsync(host, cookie, "mod");

        // One batch of three, as the profile check hands them over.
        var outcomes = await CheckAsync(host,
            new ProfileToCheck(team.VRChatUserId!, null, "I am a mod here", null, null),
            new ProfileToCheck("usr_pending", null, "I am a mod here too", null, null),
            new ProfileToCheck("usr_plain", null, "mod of my own server", null, null));

        // The team member: flagged, marked exempt, nothing done.
        Assert.Equal(1, outcomes[0].FlagsWritten);
        Assert.True(Assert.Single(outcomes[0].Matches).Exempt);
        Assert.False(outcomes[0].GroupBanned);

        // The other two are acted on: a pending link is nobody's proof.
        Assert.True(outcomes[1].GroupBanned);
        Assert.True(outcomes[2].GroupBanned);
        Assert.Equal(["usr_pending", "usr_plain"], vrchat.Banned.Order().ToList());

        await using var check = _db.NewContext();
        var flag = await check.ModerationFlags.SingleAsync(f => f.SubjectId == team.VRChatUserId, Ct);
        Assert.True(flag.Exempt);
        Assert.False(flag.GroupBanned);
        Assert.False(flag.WouldGroupBan);
    }

    [Fact]
    public async Task AnExemptGroupRoleIsFlaggedOnly_AndNothingAtAllWithDoNotFlagThemEither_AndSomeoneWhoLeftHoldsNothing()
    {
        var vrchat = new FakeVRChatActions();
        await using var host = await StartAsync(vrchat);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        await SwitchOnAsync(host, cookie);
        await SetManagedGroupAsync();

        await GroupMemberAsync("usr_helper", [StaffRole]);
        await GroupMemberAsync("usr_helper_2", [StaffRole, "grol_other"]);
        await GroupMemberAsync("usr_member", ["grol_other"]);
        await GroupMemberAsync("usr_left", [StaffRole], left: true);

        var id = await ActingBanListAsync(host, cookie, "mod", GroupScope(skipFlag: false, StaffRole));

        var first = await CheckAsync(host,
            new ProfileToCheck("usr_helper", null, "I am a mod here", null, null),
            new ProfileToCheck("usr_member", null, "I am a mod here", null, null),
            new ProfileToCheck("usr_left", null, "I am a mod here", null, null));

        Assert.True(Assert.Single(first[0].Matches).Exempt);
        Assert.False(first[0].GroupBanned);
        Assert.True(first[1].GroupBanned);
        Assert.True(first[2].GroupBanned);

        // The saved rule reads back with the role.
        var read = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, $"{Path}/lists/{id}", null, cookie, Ct));
        Assert.Equal([StaffRole], read.GetProperty("list").GetProperty("scope").GetProperty("exemptGroupRoles").EnumerateArray().Select(r => r.GetString()).ToList());

        // With "do not flag them either", the exempt person is not flagged at all.
        var saved = await host.SendJsonAsync(HttpMethod.Put, $"{Path}/lists/{id}",
            List("Impersonation", "mod", targets: ["bio"], groupBan: true, scope: GroupScope(skipFlag: true, StaffRole)), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var quiet = await CheckAsync(host, new ProfileToCheck("usr_helper_2", null, "I am a mod here", null, null));
        Assert.Empty(quiet.Matches);
        Assert.Equal(0, quiet.FlagsWritten);
        Assert.Equal(["usr_left", "usr_member"], vrchat.Banned.Order().ToList());
    }

    [Fact]
    public async Task AGroupRoleThatIsNotSavedIsKeptWhenARequestLeavesItOut_AndTheGroupsRolesAreOffered()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await using (var db = _db.NewContext())
        {
            var settings = await db.GetSettingsAsync(Ct);
            settings.GroupInfoSnapshot = new Modbot.VRChat.Sync.GroupInfoSnapshot(
                "Group", null, null, null, null, null, null, null, false, 0, 0,
                [
                    new Modbot.VRChat.Sync.GroupRoleSnapshot(StaffRole, "Staff", null, 1, true, false, false, false, []),
                    new Modbot.VRChat.Sync.GroupRoleSnapshot("grol_other", "Members", null, 2, false, false, false, true, []),
                ]).ToJson();
            await db.SaveChangesAsync(Ct);
        }

        var id = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists",
            List("Impersonation", "mod", targets: ["bio"], scope: GroupScope(skipFlag: false, StaffRole)), cookie, Ct));

        // A request from a client that knows nothing of group roles sends the scope without them.
        var listId = id.GetProperty("list").GetProperty("id").GetGuid();
        var kept = await JsonAsync(await host.SendJsonAsync(HttpMethod.Put, $"{Path}/lists/{listId}",
            List("Impersonation", "mod", targets: ["bio"],
                scope: new { channelMode = "all", channels = Array.Empty<string>(), exemptRoles = Array.Empty<string>(), exemptRolesSkipFlag = false }),
            cookie, Ct));
        Assert.Equal([StaffRole], kept.GetProperty("list").GetProperty("scope").GetProperty("exemptGroupRoles").EnumerateArray().Select(r => r.GetString()).ToList());

        var body = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct));
        var roles = body.GetProperty("groupRoles").EnumerateArray().Select(r => (r.GetProperty("id").GetString(), r.GetProperty("name").GetString())).ToList();
        Assert.Equal([(StaffRole, "Staff"), ("grol_other", "Members")], roles);

        // An id that is empty or absurdly long is refused; the ids themselves are opaque text.
        var refused = await host.SendJsonAsync(HttpMethod.Put, $"{Path}/lists/{listId}",
            List("Impersonation", "mod", targets: ["bio"], scope: GroupScope(skipFlag: false, new string('x', 101))), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    // ── The AI opinion ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AskingTheAiAboutAFlagIsRefusedWhileAiIsOff_AndWhileTheToolIsOff()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(
            ModbotPermissions.ManageSettings | ModbotPermissions.ReviewTickets | ModbotPermissions.ViewProfile, Ct);
        await SwitchOnAsync(host, cookie);

        await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists", List("Scams", "free nitro", targets: ["bio"]), cookie, Ct);
        await CheckAsync(host, new ProfileToCheck("usr_1", null, "free nitro here", null, null));

        Guid flagId;
        await using (var db = _db.NewContext())
            flagId = (await db.ModerationFlags.SingleAsync(Ct)).Id;

        // AI off: the list says the button does nothing, and the endpoint says so too.
        var list = await JsonAsync(await host.SendJsonAsync(HttpMethod.Get, "/api/moderation-flags", null, cookie, Ct));
        Assert.False(list.GetProperty("aiOpinionAvailable").GetBoolean());

        var aiOff = await host.SendJsonAsync(HttpMethod.Post, $"/api/moderation-flags/{flagId}/ai-opinion", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.Conflict, aiOff.StatusCode);

        // AI on but the tool off: still refused, before anything is asked.
        await using (var db = _db.NewContext())
        {
            var settings = await db.GetSettingsAsync(Ct);
            settings.AiEnabled = true;
            await db.SaveChangesAsync(Ct);
        }

        await host.SendJsonAsync(HttpMethod.Put, Path,
            new { enabled = true, aiTools = new Dictionary<string, bool> { ["review_flag"] = false } }, cookie, Ct);

        var toolOff = await host.SendJsonAsync(HttpMethod.Post, $"/api/moderation-flags/{flagId}/ai-opinion", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.Conflict, toolOff.StatusCode);
        Assert.Contains("switched off", await toolOff.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        await using (var db = _db.NewContext())
            Assert.Null((await db.ModerationFlags.SingleAsync(Ct)).AiOpinion);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private async Task<ApiTestHost> StartAsync(FakeVRChatActions? vrchat = null)
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        await using (var db = _db.NewContext())
        {
            await db.ModerationFlags.ExecuteDeleteAsync(Ct);
            await db.ModerationTestSamples.ExecuteDeleteAsync(Ct);
            await db.ModerationTestRuns.ExecuteDeleteAsync(Ct);
            await db.ModerationRuleVersions.ExecuteDeleteAsync(Ct);
            await db.ModerationTermLists.ExecuteDeleteAsync(Ct);
            await db.ModerationTopics.ExecuteDeleteAsync(Ct);
            await db.GroupMembers.ExecuteDeleteAsync(Ct);
        }

        return await ApiTestHost.StartAsync(_db, configure: services =>
        {
            if (vrchat is not null)
                services.AddScoped<IVRChatModerationActions>(_ => vrchat);
        });
    }

    private async Task SwitchOnAsync(ApiTestHost host, string cookie)
    {
        var response = await host.SendJsonAsync(HttpMethod.Put, Path, new { enabled = true }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<ModerationOutcome> CheckAsync(ApiTestHost host, ProfileToCheck profile)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IModerationChecker>().CheckProfileAsync(profile, Ct);
    }

    /// <summary>Several profiles in one pass, the way the profile check hands them over.</summary>
    private static async Task<IReadOnlyList<ModerationOutcome>> CheckAsync(ApiTestHost host, params ProfileToCheck[] profiles)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IModerationChecker>().CheckProfilesAsync(profiles, Ct);
    }

    /// <summary>A bio rule set to ban, past its trial, so a match really acts.</summary>
    private async Task<Guid> ActingBanListAsync(ApiTestHost host, string cookie, string word, object? scope = null)
    {
        var created = await JsonAsync(await host.SendJsonAsync(HttpMethod.Post, $"{Path}/lists",
            List("Impersonation", word, targets: ["bio"], groupBan: true, scope: scope), cookie, Ct));
        var id = created.GetProperty("list").GetProperty("id").GetGuid();

        var ended = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/rules/termList/{id}/end-trial", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);

        return id;
    }

    private static object GroupScope(bool skipFlag, params string[] groupRoles) => new
    {
        channelMode = "all",
        channels = Array.Empty<string>(),
        exemptRoles = Array.Empty<string>(),
        exemptRolesSkipFlag = skipFlag,
        exemptGroupRoles = groupRoles,
    };

    private async Task SetManagedGroupAsync()
    {
        await using var db = _db.NewContext();
        var settings = await db.GetSettingsAsync(Ct);
        settings.ManagedGroupId = Group;
        await db.SaveChangesAsync(Ct);
    }

    private async Task GroupMemberAsync(string userId, string[] roles, bool left = false)
    {
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await using var db = _db.NewContext();
        db.GroupMembers.Add(new GroupMember
        {
            GroupId = Group,
            UserId = userId,
            Roles = JsonSerializer.Serialize(roles),
            FirstSeenAt = at,
            LastSeenAt = at,
            LeftAt = left ? at : null,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static object List(
        string name, string word, string[] targets, bool groupBan = false, bool groupRemove = false, object? scope = null) => new
    {
        scope,
        name,
        enabled = true,
        targets,
        deleteMessage = false,
        timeoutMinutes = (int?)null,
        groupBan,
        groupRemove,
        terms = new[] { new { id = (string?)null, kind = "word", text = word } },
        // The acting gate (design §12.4) has its own tests; these skip it and say so.
        actWithoutTest = true,
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public sealed class FakeVRChatActions : IVRChatModerationActions
    {
        public List<string> Banned { get; } = [];

        public List<string> Removed { get; } = [];

        public Task<VRChatActionOutcome> BanFromGroupAsync(string userId, string reason, CancellationToken ct = default)
        {
            Banned.Add(userId);
            return Task.FromResult(VRChatActionOutcome.Ok);
        }

        public Task<VRChatActionOutcome> RemoveFromGroupAsync(string userId, string reason, CancellationToken ct = default)
        {
            Removed.Add(userId);
            return Task.FromResult(VRChatActionOutcome.Ok);
        }

        // Role and ban sync's half of the interface. No AutoMod rule reaches these.
        public Task<VRChatActionOutcome> UnbanFromGroupAsync(string userId, string reason, CancellationToken ct = default)
            => Task.FromResult(VRChatActionOutcome.Ok);

        public Task<VRChatActionOutcome> GiveGroupRoleAsync(string userId, string roleId, CancellationToken ct = default)
            => Task.FromResult(VRChatActionOutcome.Ok);

        public Task<VRChatActionOutcome> TakeGroupRoleAsync(string userId, string roleId, CancellationToken ct = default)
            => Task.FromResult(VRChatActionOutcome.Ok);
    }
}
