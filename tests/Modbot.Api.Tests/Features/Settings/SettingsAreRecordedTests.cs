using System.Net;
using System.Text.Json;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Every settings save writes one "settings changed" entry saying who changed what and from what:
/// the screens that used to save without a trace, one test each.
/// </summary>
/// <remarks>
/// Each test saves twice, with a different value the second time, and reads the newest entry. The
/// first save may or may not change anything, depending on what an earlier test left in the
/// shared tables; the second always does. A third save of the same values writes nothing, and a
/// secret never appears in an entry, before or after.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class SettingsAreRecordedTests
{
    private readonly PostgresFixture _db;

    public SettingsAreRecordedTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(ApiTestHost Host, ModbotUser User, string Cookie)> StartAsync(ModbotPermissions permissions)
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var host = await ApiTestHost.StartAsync(_db);
        var (user, cookie) = await host.SignedInAsync(permissions, Ct);
        return (host, user, cookie);
    }

    private static async Task<List<JsonElement>> EntriesAsync(ApiTestHost host, string setting)
    {
        var facts = await host.FactsAsync(FactType.SettingsChanged, "settings", Ct);

        return [.. facts
            .Select(ApiTestHost.DataOf)
            .Where(data => data.TryGetProperty("setting", out var name) && name.GetString() == setting)];
    }

    private static JsonElement Field(JsonElement entry, string field) => entry.GetProperty("changed").GetProperty(field);

    private static bool Has(JsonElement entry, string field) => entry.GetProperty("changed").TryGetProperty(field, out _);

    private static async Task PutAsync(ApiTestHost host, string path, object body, string cookie)
    {
        var response = await host.SendJsonAsync(HttpMethod.Put, path, body, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Retention ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ShorteningRetentionIsRecordedWithWhoAndFromWhat()
    {
        var (host, user, cookie) = await StartAsync(ModbotPermissions.ManageSettings);
        await using var running = host;

        object Body(int moderationDays) => new
        {
            moderationFactRetentionDays = moderationDays,
            presenceFactRetentionDays = 0,
            discordMessageRetentionDays = 0,
        };

        await PutAsync(host, "/api/settings/retention", Body(30), cookie);
        await PutAsync(host, "/api/settings/retention", Body(1), cookie);

        var newest = (await EntriesAsync(host, "retention"))[0];
        Assert.Equal(30, Field(newest, "moderationFactRetentionDays").GetProperty("old").GetInt32());
        Assert.Equal(1, Field(newest, "moderationFactRetentionDays").GetProperty("new").GetInt32());
        Assert.False(Has(newest, "presenceFactRetentionDays"));

        var facts = await host.FactsAsync(FactType.SettingsChanged, "settings", Ct);
        Assert.Equal(user.Id.ToString(), facts[0].ActorId);

        var before = (await EntriesAsync(host, "retention")).Count;
        await PutAsync(host, "/api/settings/retention", Body(1), cookie);
        Assert.Equal(before, (await EntriesAsync(host, "retention")).Count);
    }

    // ── AI ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheAiKeyIsRecordedAsChangedAndNeverAsAValue()
    {
        var (host, _, cookie) = await StartAsync(ModbotPermissions.ManageSettings);
        await using var running = host;

        object Body(string model, string? apiKey) => new
        {
            enabled = false,
            provider = "openrouter",
            endpoint = "https://openrouter.ai/api/v1",
            model,
            apiKey,
        };

        await PutAsync(host, "/api/settings/ai", Body("model-one", "sk-secret-one"), cookie);
        await PutAsync(host, "/api/settings/ai", Body("model-two", "sk-secret-two"), cookie);

        var entries = await EntriesAsync(host, "ai");
        var newest = entries[0];

        Assert.Equal("model-one", Field(newest, "model").GetProperty("old").GetString());
        Assert.Equal("model-two", Field(newest, "model").GetProperty("new").GetString());
        Assert.True(Field(newest, "apiKey").GetProperty("secret").GetBoolean());

        // Nothing of either key, and nothing of the stored form of them, in any entry.
        Assert.All(entries, entry =>
        {
            var text = entry.GetRawText();
            Assert.DoesNotContain("sk-secret", text, StringComparison.Ordinal);
            Assert.False(Field(entry, "apiKey").TryGetProperty("old", out _));
            Assert.False(Field(entry, "apiKey").TryGetProperty("new", out _));
        });

        // Saving again with the key left empty keeps it, and changes nothing.
        await PutAsync(host, "/api/settings/ai", Body("model-two", null), cookie);
        Assert.Equal(entries.Count, (await EntriesAsync(host, "ai")).Count);
    }

    [Fact]
    public async Task AChatSaveIsRecordedWithTheToolsSwitched()
    {
        var (host, _, cookie) = await StartAsync(ModbotPermissions.ManageSettings);
        await using var running = host;

        object Body(int maxToolCalls, bool searchOn) => new
        {
            enabled = false,
            model = (string?)null,
            instructions = (string?)null,
            maxToolCalls,
            maxReplyTokens = 2000,
            timeLimitSeconds = 120,
            tools = new Dictionary<string, bool> { ["search_audit_log"] = searchOn },
        };

        await PutAsync(host, "/api/settings/ai/chat", Body(3, true), cookie);
        await PutAsync(host, "/api/settings/ai/chat", Body(4, false), cookie);

        var newest = (await EntriesAsync(host, "aiChat"))[0];

        Assert.Equal(3, Field(newest, "maxToolCalls").GetProperty("old").GetInt32());
        Assert.Equal(4, Field(newest, "maxToolCalls").GetProperty("new").GetInt32());

        var was = Field(newest, "toolsOn").GetProperty("old").EnumerateArray().Select(t => t.GetString()).ToList();
        var now = Field(newest, "toolsOn").GetProperty("new").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Contains("search_audit_log", was);
        Assert.DoesNotContain("search_audit_log", now);
    }

    [Fact]
    public async Task ALimitRaisedIsRecordedAsTheOldOneTakenAwayAndTheNewOnePutIn()
    {
        var (host, _, cookie) = await StartAsync(ModbotPermissions.ManageSettings);
        await using var running = host;

        object Body(decimal perDay) => new { limits = new[] { new { appliesTo = "everyone", perDay, perMonth = (decimal?)null } } };

        await PutAsync(host, "/api/settings/ai/limits", Body(5), cookie);
        await PutAsync(host, "/api/settings/ai/limits", Body(8), cookie);

        var newest = (await EntriesAsync(host, "aiLimits"))[0];

        Assert.Equal(["Everyone: 5 a day"], Field(newest, "spendLimits").GetProperty("old").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal(["Everyone: 8 a day"], Field(newest, "spendLimits").GetProperty("new").EnumerateArray().Select(l => l.GetString()));

        var before = (await EntriesAsync(host, "aiLimits")).Count;
        await PutAsync(host, "/api/settings/ai/limits", Body(8), cookie);
        Assert.Equal(before, (await EntriesAsync(host, "aiLimits")).Count);
    }

    [Fact]
    public async Task AChangedPriceIsRecorded()
    {
        var (host, _, cookie) = await StartAsync(ModbotPermissions.ManageSettings);
        await using var running = host;

        object Body(decimal input) => new
        {
            prices = new[] { new { model = "test-model", inputPerMillion = input, cachedInputPerMillion = (decimal?)null, outputPerMillion = 2m } },
        };

        await PutAsync(host, "/api/settings/ai/prices", Body(1), cookie);
        await PutAsync(host, "/api/settings/ai/prices", Body(3), cookie);

        var newest = (await EntriesAsync(host, "aiPrices"))[0];

        Assert.Contains("input 1", Field(newest, "prices").GetProperty("old")[0].GetString(), StringComparison.Ordinal);
        Assert.Contains("input 3", Field(newest, "prices").GetProperty("new")[0].GetString(), StringComparison.Ordinal);
    }

    // ── Discord ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheDiscordClientSecretIsRecordedAsChangedAndNeverAsAValue()
    {
        var (host, _, cookie) = await StartAsync(ModbotPermissions.ManageSettings);
        await using var running = host;

        object Body(string clientId, string? secret) => new
        {
            clientId,
            clientSecret = secret,
            removeClientSecret = false,
            promptNewMembers = false,
            backupChannelId = (string?)null,
            linkedRoleId = (string?)null,
            eighteenPlusRoleId = (string?)null,
        };

        await PutAsync(host, "/api/settings/discord-linking", Body("1111", "client-secret-one"), cookie);
        await PutAsync(host, "/api/settings/discord-linking", Body("2222", "client-secret-two"), cookie);

        var entries = await EntriesAsync(host, "discordLinking");
        var newest = entries[0];

        Assert.Equal("1111", Field(newest, "clientId").GetProperty("old").GetString());
        Assert.Equal("2222", Field(newest, "clientId").GetProperty("new").GetString());
        Assert.True(Field(newest, "clientSecret").GetProperty("secret").GetBoolean());
        Assert.All(entries, entry => Assert.DoesNotContain("client-secret", entry.GetRawText(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheDiscordSyncSwitchesAndPairsAreRecorded()
    {
        var (host, _, cookie) = await StartAsync(ModbotPermissions.ManageDiscordSync);
        await using var running = host;

        object Switches(string banCopyAction) => new
        {
            roleSyncOn = false,
            banSyncToDiscord = false,
            banSyncToVRChat = false,
            banCopyAction,
        };

        await PutAsync(host, "/api/discord-sync", Switches("ban"), cookie);
        await PutAsync(host, "/api/discord-sync", Switches("remove"), cookie);

        var newest = (await EntriesAsync(host, "discordSync"))[0];
        Assert.Equal("ban", Field(newest, "banCopyAction").GetProperty("old").GetString());
        Assert.Equal("remove", Field(newest, "banCopyAction").GetProperty("new").GetString());

        var added = await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/discord-sync/pairs",
            new { vrchatRoleId = "grol_staff", discordRoleId = "801", decides = "vrchat", enabled = true },
            cookie,
            Ct);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        var pairAdded = (await EntriesAsync(host, "discordSyncPairs"))[0];
        Assert.Empty(Field(pairAdded, "pairs").GetProperty("old").EnumerateArray());
        Assert.Contains("grol_staff", Field(pairAdded, "pairs").GetProperty("new")[0].GetString(), StringComparison.Ordinal);

        var id = (await ApiTestHost.BodyOf(added, Ct)).GetProperty("pairs")[0].GetProperty("id").GetGuid();

        var removed = await host.SendJsonAsync(HttpMethod.Delete, $"/api/discord-sync/pairs/{id}", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        var pairRemoved = (await EntriesAsync(host, "discordSyncPairs"))[0];
        Assert.Empty(Field(pairRemoved, "pairs").GetProperty("new").EnumerateArray());
        Assert.Contains("grol_staff", Field(pairRemoved, "pairs").GetProperty("old")[0].GetString(), StringComparison.Ordinal);
    }

    // ── Logs, health alerts, insights, alerts ───────────────────────────────────────────────

    [Fact]
    public async Task TheLogSettingsAreRecorded()
    {
        var (host, _, cookie) = await StartAsync(ModbotPermissions.ManageSettings);
        await using var running = host;

        await PutAsync(host, "/api/logs/settings", new { keepDays = 7, sendToCloud = true }, cookie);
        await PutAsync(host, "/api/logs/settings", new { keepDays = 14, sendToCloud = false }, cookie);

        var newest = (await EntriesAsync(host, "logs"))[0];

        Assert.Equal(7, Field(newest, "keepDays").GetProperty("old").GetInt32());
        Assert.Equal(14, Field(newest, "keepDays").GetProperty("new").GetInt32());
        Assert.True(Field(newest, "sendToCloud").GetProperty("old").GetBoolean());
        Assert.False(Field(newest, "sendToCloud").GetProperty("new").GetBoolean());
    }

    [Fact]
    public async Task TheHealthAlertSettingsAreRecorded()
    {
        var (host, _, cookie) = await StartAsync(ModbotPermissions.ManageSettings);
        await using var running = host;

        object Body(int quietHours) => new
        {
            quietHours,
            storageWarnGb = 5.0,
            checksOn = Array.Empty<string>(),
            recipientUserIds = Array.Empty<Guid>(),
        };

        await PutAsync(host, "/api/health/alerts", Body(6), cookie);
        await PutAsync(host, "/api/health/alerts", Body(12), cookie);

        var newest = (await EntriesAsync(host, "healthAlerts"))[0];

        Assert.Equal(6, Field(newest, "quietHours").GetProperty("old").GetInt32());
        Assert.Equal(12, Field(newest, "quietHours").GetProperty("new").GetInt32());
        Assert.False(Has(newest, "storageWarnGb"));
    }

    [Fact]
    public async Task TheInsightSettingsAreRecorded()
    {
        var (host, _, cookie) = await StartAsync(ModbotPermissions.ManageSettings);
        await using var running = host;

        object Body(int hour) => new
        {
            timeZone = (string?)null,
            model = (string?)null,
            kinds = new[] { new { kind = "group", enabled = false, every = "week", hour, weekday = 1, discordChannelId = (string?)null } },
        };

        await PutAsync(host, "/api/settings/ai/insights", Body(9), cookie);
        await PutAsync(host, "/api/settings/ai/insights", Body(10), cookie);

        var newest = (await EntriesAsync(host, "aiInsights"))[0];

        Assert.Equal(9, Field(newest, "groupInsightHour").GetProperty("old").GetInt32());
        Assert.Equal(10, Field(newest, "groupInsightHour").GetProperty("new").GetInt32());
    }

    [Fact]
    public async Task TheAlertSettingsAreRecorded()
    {
        var (host, _, cookie) = await StartAsync(ModbotPermissions.ManageSettings);
        await using var running = host;

        object Body(int quietHours) => new
        {
            discordChannelId = (string?)null,
            quietHours,
            writeSentence = true,
            watchers = new[] { new { watcher = "flags", sensitivity = "normal" } },
        };

        await PutAsync(host, "/api/settings/ai/alerts", Body(6), cookie);
        await PutAsync(host, "/api/settings/ai/alerts", Body(12), cookie);

        var newest = (await EntriesAsync(host, "aiAlerts"))[0];

        Assert.Equal(6, Field(newest, "quietHours").GetProperty("old").GetInt32());
        Assert.Equal(12, Field(newest, "quietHours").GetProperty("new").GetInt32());
    }

    // ── Public address ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The public address is hidden from anybody without Manage settings, so the entry says it
    /// changed and never what it was or became.
    /// </summary>
    [Fact]
    public async Task ThePublicAddressIsRecordedAsChangedAndNeverAsAValue()
    {
        var (host, _, cookie) = await StartAsync(ModbotPermissions.ManageSettings);
        await using var running = host;

        await PutAsync(host, "/api/settings/public-address", new { publicAddress = "https://first-address.example.com" }, cookie);
        await PutAsync(host, "/api/settings/public-address", new { publicAddress = "https://second-address.example.com" }, cookie);

        var entries = await EntriesAsync(host, "publicAddress");
        Assert.Equal(2, entries.Count);

        Assert.All(entries, entry =>
        {
            Assert.True(Field(entry, "address").GetProperty("secret").GetBoolean());
            Assert.DoesNotContain("address.example.com", entry.GetRawText(), StringComparison.Ordinal);
            Assert.False(entry.TryGetProperty("before", out _));
            Assert.False(entry.TryGetProperty("after", out _));
        });

        // The same address again changes nothing, so it writes nothing.
        await PutAsync(host, "/api/settings/public-address", new { publicAddress = "https://second-address.example.com" }, cookie);
        Assert.Equal(2, (await EntriesAsync(host, "publicAddress")).Count);
    }

    // ── Who may see the entries ─────────────────────────────────────────────────────────────

    /// <summary>
    /// How long a closed report keeps its words (Discord commands design §3.4, decision 10): saved
    /// when sent and recorded, left as it was when an older client leaves it out, and never negative.
    /// </summary>
    [Fact]
    public async Task TheClosedReportRetention_IsSavedWhenSent_LeftAloneWhenLeftOut_AndRefusedWhenNegative()
    {
        var (host, _, cookie) = await StartAsync(ModbotPermissions.ManageSettings);
        await using var running = host;

        object Body(int? reports) => reports is { } days
            ? new { moderationFactRetentionDays = 0, presenceFactRetentionDays = 0, discordMessageRetentionDays = 0, memberReportRetentionDays = days }
            : new { moderationFactRetentionDays = 0, presenceFactRetentionDays = 0, discordMessageRetentionDays = 0, memberReportRetentionDays = (int?)null };

        await PutAsync(host, "/api/settings/retention", Body(60), cookie);
        await PutAsync(host, "/api/settings/retention", Body(30), cookie);

        var newest = (await EntriesAsync(host, "retention"))[0];
        Assert.Equal(60, Field(newest, "memberReportRetentionDays").GetProperty("old").GetInt32());
        Assert.Equal(30, Field(newest, "memberReportRetentionDays").GetProperty("new").GetInt32());

        // Left out: the answer says what it still is, and nothing about it is recorded.
        var before = (await EntriesAsync(host, "retention")).Count;
        var older = await host.SendJsonAsync(
            HttpMethod.Put,
            "/api/settings/retention",
            new { moderationFactRetentionDays = 0, presenceFactRetentionDays = 0, discordMessageRetentionDays = 0 },
            cookie,
            Ct);
        Assert.Equal(HttpStatusCode.OK, older.StatusCode);
        Assert.Equal(30, JsonDocument.Parse(await older.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("memberReportRetentionDays").GetInt32());
        Assert.Equal(before, (await EntriesAsync(host, "retention")).Count);

        var negative = await host.SendJsonAsync(
            HttpMethod.Put,
            "/api/settings/retention",
            Body(-1),
            cookie,
            Ct);
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
    }

    /// <summary>A save without Change settings is refused, and leaves no entry behind.</summary>
    [Fact]
    public async Task ARefusedSaveLeavesNoEntry()
    {
        var (host, _, cookie) = await StartAsync(ModbotPermissions.ViewMembers);
        await using var running = host;

        var response = await host.SendJsonAsync(
            HttpMethod.Put,
            "/api/settings/retention",
            new { moderationFactRetentionDays = 1, presenceFactRetentionDays = 0, discordMessageRetentionDays = 0 },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await EntriesAsync(host, "retention"));
    }
}
