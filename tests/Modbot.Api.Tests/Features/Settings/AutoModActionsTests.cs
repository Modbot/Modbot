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

    private static object List(string name, string word, string[] targets, bool groupBan = false, bool groupRemove = false) => new
    {
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
