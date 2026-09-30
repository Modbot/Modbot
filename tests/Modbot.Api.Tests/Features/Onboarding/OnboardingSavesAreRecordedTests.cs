using System.Net;
using System.Text.Json;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Onboarding;

/// <summary>
/// The setup steps are how Settings saves the Discord bot, the mail server, the VRChat account, the
/// proxy and the group once setup is done, so each one writes a "settings changed" entry like any
/// other save. A secret is only ever said to have changed. Before the first account exists there is
/// nobody to name, and the wizard writes nothing.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class OnboardingSavesAreRecordedTests
{
    private readonly PostgresFixture _db;

    public OnboardingSavesAreRecordedTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<JsonElement>> EntriesAsync(ApiTestHost host, string setting)
    {
        var facts = await host.FactsAsync(FactType.SettingsChanged, "settings", Ct);

        return [.. facts
            .Select(ApiTestHost.DataOf)
            .Where(data => data.TryGetProperty("setting", out var name) && name.GetString() == setting)];
    }

    private static JsonElement Field(JsonElement entry, string field) => entry.GetProperty("changed").GetProperty(field);

    private static bool Has(JsonElement entry, string field) => entry.GetProperty("changed").TryGetProperty(field, out _);

    // ── Discord bot, mail, public address ───────────────────────────────────────────────────

    [Fact]
    public async Task TheDiscordBotAndMailSettingsAreRecorded_WithTheirSecretsOnlyAsChanged()
    {
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, null, Ct);
        await using var running = host;

        object Body(string token, string password) => new
        {
            discord = new { botToken = token, guildId = "111", instanceChannelId = "222" },
            smtp = new { host = "mail.example.com", port = 465, username = "mailer", password, fromAddress = "modbot@example.com", useTls = true },
            publicAddress = "https://modbot.example.com",
        };

        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync("/api/onboarding/integrations", Body("bot-token-one", "smtp-secret-one"), cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync("/api/onboarding/integrations", Body("bot-token-two", "smtp-secret-two"), cookie, Ct)).StatusCode);

        var entries = await EntriesAsync(host, "integrations");
        Assert.Equal(2, entries.Count);

        // The first save says what was set; the second changed only the two secrets. What the
        // setup status hides from anybody without Manage settings is only ever said to have changed.
        Assert.Equal(465, Field(entries[1], "smtpPort").GetProperty("new").GetInt32());
        foreach (var hidden in new[] { "discordGuildId", "instanceChannelId", "smtpHost", "smtpUsername", "smtpFromAddress", "publicAddress" })
            Assert.True(Field(entries[1], hidden).GetProperty("secret").GetBoolean(), hidden);

        Assert.True(Field(entries[0], "discordBotToken").GetProperty("secret").GetBoolean());
        Assert.True(Field(entries[0], "smtpPassword").GetProperty("secret").GetBoolean());
        Assert.False(Has(entries[0], "discordGuildId"));
        Assert.False(Has(entries[0], "smtpHost"));

        Assert.All(entries, entry =>
        {
            var text = entry.GetRawText();
            Assert.DoesNotContain("bot-token", text, StringComparison.Ordinal);
            Assert.DoesNotContain("smtp-secret", text, StringComparison.Ordinal);
            Assert.DoesNotContain("mail.example.com", text, StringComparison.Ordinal);
            Assert.DoesNotContain("mailer", text, StringComparison.Ordinal);
            Assert.DoesNotContain("modbot.example.com", text, StringComparison.Ordinal);
            Assert.DoesNotContain("111", text, StringComparison.Ordinal);
        });

        // The same values again change nothing, so they write nothing.
        await host.PostAsync("/api/onboarding/integrations", new { discord = new { guildId = "111" } }, cookie, Ct);
        Assert.Equal(2, (await EntriesAsync(host, "integrations")).Count);
    }

    [Fact]
    public async Task BeforeTheFirstAccountTheWizardWritesNoEntry()
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);

        var response = await host.PostAsync(
            "/api/onboarding/integrations", new { discord = new { guildId = "111" } }, cookie: null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await EntriesAsync(host, "integrations"));
    }

    // ── VRChat account ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheVRChatAccountIsRecorded_WithItsPasswordAndAuthenticatorSecretOnlyAsChanged()
    {
        var gate = new FakeVRChatGate().SignedInAs("ModbotBot");
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var running = host;

        object Body(string password, string totp) => new { username = "modbot@example.com", password, totpSecret = totp };

        await host.PostAsync("/api/onboarding/vrchat", Body("vrchat-password-one", "AAAA BBBB"), cookie, Ct);
        await host.PostAsync("/api/onboarding/vrchat", Body("vrchat-password-two", "AAAABBBB"), cookie, Ct);

        var entries = await EntriesAsync(host, "vrchatAccount");
        Assert.Equal(2, entries.Count);

        Assert.True(Field(entries[1], "username").GetProperty("secret").GetBoolean());
        Assert.True(Field(entries[1], "password").GetProperty("secret").GetBoolean());
        Assert.True(Field(entries[1], "authenticatorSecret").GetProperty("secret").GetBoolean());

        // The second save kept the username and the authenticator secret (spaces do not count).
        Assert.False(Has(entries[0], "username"));
        Assert.False(Has(entries[0], "authenticatorSecret"));
        Assert.True(Field(entries[0], "password").GetProperty("secret").GetBoolean());

        Assert.All(entries, entry =>
        {
            var text = entry.GetRawText();
            Assert.DoesNotContain("vrchat-password", text, StringComparison.Ordinal);
            Assert.DoesNotContain("AAAA", text, StringComparison.Ordinal);
            Assert.DoesNotContain("modbot@example.com", text, StringComparison.Ordinal);
        });

        // Saving exactly the same again is not a change.
        await host.PostAsync("/api/onboarding/vrchat", Body("vrchat-password-two", "AAAABBBB"), cookie, Ct);
        Assert.Equal(2, (await EntriesAsync(host, "vrchatAccount")).Count);
    }

    // ── Proxy ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheProxyIsRecorded_WithItsAddressLoginAndPasswordOnlyAsChanged_AndSwitchingItOffIsRecorded()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var running = host;

        var on = await host.PostAsync(
            "/api/onboarding/connection-test",
            new { useProxy = true, proxyUrl = "http://proxyuser:proxypass@proxy.example.com:8080", proxyUsername = "second-user", proxyPassword = "proxy-secret" },
            cookie,
            Ct);
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);

        var entry = (await EntriesAsync(host, "proxy"))[0];
        foreach (var hidden in new[] { "address", "username", "password" })
            Assert.True(Field(entry, hidden).GetProperty("secret").GetBoolean(), hidden);

        var text = entry.GetRawText();
        Assert.DoesNotContain("proxy.example.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain("proxyuser", text, StringComparison.Ordinal);
        Assert.DoesNotContain("proxypass", text, StringComparison.Ordinal);
        Assert.DoesNotContain("second-user", text, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy-secret", text, StringComparison.Ordinal);

        var off = await host.PostAsync("/api/onboarding/connection-test", new { useProxy = false }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);

        var cleared = (await EntriesAsync(host, "proxy"))[0];
        Assert.True(Field(cleared, "address").GetProperty("secret").GetBoolean());
        Assert.True(Field(cleared, "password").GetProperty("secret").GetBoolean());
    }

    // ── Group ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangingTheManagedGroupIsRecorded_AndChoosingTheSameOneAgainIsNot()
    {
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, null, Ct);
        await using var running = host;

        await host.PostAsync("/api/onboarding/group", new { groupId = "grp_kings", name = "VRC Kings" }, cookie, Ct);
        await host.PostAsync("/api/onboarding/group", new { groupId = "grp_queens", name = "VRC Queens" }, cookie, Ct);
        await host.PostAsync("/api/onboarding/group", new { groupId = "grp_queens", name = "VRC Queens" }, cookie, Ct);

        var entries = await EntriesAsync(host, "managedGroup");
        Assert.Equal(2, entries.Count);

        Assert.Equal("grp_kings", Field(entries[0], "groupId").GetProperty("old").GetString());
        Assert.Equal("grp_queens", Field(entries[0], "groupId").GetProperty("new").GetString());
        Assert.Equal("VRC Queens", Field(entries[0], "groupName").GetProperty("new").GetString());
    }
}
