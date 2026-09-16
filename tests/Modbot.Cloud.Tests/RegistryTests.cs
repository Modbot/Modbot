using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modbot.Cloud.Features.Accounts;
using Modbot.Cloud.Features.Registry;

namespace Modbot.Cloud.Tests;

/// <summary>
/// Servers registering, reporting, and being claimed by the account that owns them
/// (Cloud accounts and registry spec 3).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class RegistryTests(PostgresFixture db)
{
    private const string Password = "a-long-enough-password";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_server_registers_once_and_reports_with_its_secret()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, bearer) = await RegisterServerAsync(host);

        using var reported = await host.SendAsync(
            HttpMethod.Post, "/api/v1/servers/report", Report(), bearer: bearer);
        Assert.Equal(HttpStatusCode.Accepted, reported.StatusCode);

        using var read = await host.SendAsync(
            HttpMethod.Get, $"/api/admin/servers/{id}", bearer: CloudTestHost.RootKey);
        var body = await read.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var server = body.GetProperty("server");

        Assert.Equal("grp_1", server.GetProperty("groupId").GetString());
        Assert.Equal("VRChat Kings", server.GetProperty("groupName").GetString());
        Assert.Equal("https://api.vrchat.cloud/icon.png", server.GetProperty("groupIconUrl").GetString());
        Assert.Equal("https://modbot.example", server.GetProperty("publicAddress").GetString());
    }

    [Fact]
    public async Task A_report_without_the_secret_is_refused()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, _) = await RegisterServerAsync(host);

        using var wrong = await host.SendAsync(
            HttpMethod.Post, "/api/v1/servers/report", Report(), bearer: $"{id}.not-the-secret");

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public void A_report_carries_no_field_for_any_count_of_people()
    {
        // The promise is the shape of the type, not a rule somebody has to remember.
        var fields = typeof(ServerReportRequest).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(fields, name => name.Contains("Member", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, name => name.Contains("User", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, name => name.Contains("Count", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, name => name.Contains("Scale", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, name => name.Contains("Bucket", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Every_report_is_kept_so_the_figures_can_be_charted()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (id, bearer) = await RegisterServerAsync(host);

        await host.SendAsync(HttpMethod.Post, "/api/v1/servers/report", Report(coldStops: 1), bearer: bearer);
        host.Time.Advance(TimeSpan.FromHours(6));
        await host.SendAsync(HttpMethod.Post, "/api/v1/servers/report", Report(coldStops: 4), bearer: bearer);

        using var read = await host.SendAsync(
            HttpMethod.Get, $"/api/admin/servers/{id}/reports", bearer: CloudTestHost.RootKey);

        var body = await read.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var items = body.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(2, items.Count);
        Assert.Equal(4, items[0].GetProperty("rateLimitColdStops").GetInt32());
        Assert.Equal(1, items[1].GetProperty("rateLimitColdStops").GetInt32());
    }

    [Fact]
    public async Task An_account_claims_its_server_with_the_code_Modbot_showed()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var cookie = await SignUpAsync(host, "owner@example.com");
        var (id, bearer) = await RegisterServerAsync(host);

        var code = await ShowLinkCodeAsync(host, bearer);

        using var claimed = await host.SendAsync(
            HttpMethod.Post, "/api/v1/servers/claim", new { code }, cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);

        using var mine = await host.SendAsync(HttpMethod.Get, "/api/v1/servers/mine", cookie: cookie);
        var body = await mine.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var items = body.GetProperty("items").EnumerateArray().ToList();

        Assert.Single(items);
        Assert.Equal(id, items[0].GetProperty("serverId").GetGuid());
    }

    [Fact]
    public async Task A_link_code_works_once()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var cookie = await SignUpAsync(host, "owner@example.com");
        var (_, bearer) = await RegisterServerAsync(host);

        var code = await ShowLinkCodeAsync(host, bearer);

        using var first = await host.SendAsync(HttpMethod.Post, "/api/v1/servers/claim", new { code }, cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var again = await host.SendAsync(HttpMethod.Post, "/api/v1/servers/claim", new { code }, cookie: cookie);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task A_link_code_lapses_after_fifteen_minutes()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var cookie = await SignUpAsync(host, "owner@example.com");
        var (_, bearer) = await RegisterServerAsync(host);

        var code = await ShowLinkCodeAsync(host, bearer);
        host.Time.Advance(ServerSecrets.LinkCodeLifetime + TimeSpan.FromMinutes(1));

        using var late = await host.SendAsync(HttpMethod.Post, "/api/v1/servers/claim", new { code }, cookie: cookie);
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
    }

    [Fact]
    public async Task A_stranger_who_guesses_wrong_claims_nothing()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var stranger = await SignUpAsync(host, "stranger@example.com");
        var (_, bearer) = await RegisterServerAsync(host);

        await ShowLinkCodeAsync(host, bearer);

        using var guess = await host.SendAsync(
            HttpMethod.Post, "/api/v1/servers/claim", new { code = "ZZZZZZZZ" }, cookie: stranger);
        Assert.Equal(HttpStatusCode.BadRequest, guess.StatusCode);

        using var mine = await host.SendAsync(HttpMethod.Get, "/api/v1/servers/mine", cookie: stranger);
        var body = await mine.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Empty(body.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_second_account_cannot_take_a_server_somebody_already_holds()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var owner = await SignUpAsync(host, "owner@example.com");
        var (_, bearer) = await RegisterServerAsync(host);

        using var claimed = await host.SendAsync(
            HttpMethod.Post, "/api/v1/servers/claim", new { code = await ShowLinkCodeAsync(host, bearer) }, cookie: owner);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);

        var other = await SignUpAsync(host, "other@example.com");

        using var taken = await host.SendAsync(
            HttpMethod.Post, "/api/v1/servers/claim", new { code = await ShowLinkCodeAsync(host, bearer) }, cookie: other);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
    }

    [Fact]
    public async Task Claiming_needs_an_account()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, bearer) = await RegisterServerAsync(host);

        using var response = await host.SendAsync(
            HttpMethod.Post, "/api/v1/servers/claim", new { code = await ShowLinkCodeAsync(host, bearer) });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_owner_can_let_a_server_go()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var cookie = await SignUpAsync(host, "owner@example.com");
        var (id, bearer) = await RegisterServerAsync(host);

        await host.SendAsync(
            HttpMethod.Post, "/api/v1/servers/claim", new { code = await ShowLinkCodeAsync(host, bearer) }, cookie: cookie);

        using var released = await host.SendAsync(HttpMethod.Delete, $"/api/v1/servers/{id}/claim", cookie: cookie);
        Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);

        using var mine = await host.SendAsync(HttpMethod.Get, "/api/v1/servers/mine", cookie: cookie);
        var body = await mine.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Empty(body.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task The_registry_is_not_readable_without_the_root_key()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        await RegisterServerAsync(host);

        using var anonymous = await host.SendAsync(HttpMethod.Get, "/api/admin/servers");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // Not with the proxy key either: it opens /api/v1/site and nothing else.
        using var proxy = await host.SendAsync(HttpMethod.Get, "/api/admin/servers", bearer: CloudTestHost.ProxyKey);
        Assert.Equal(HttpStatusCode.Unauthorized, proxy.StatusCode);
    }

    [Fact]
    public async Task Too_many_registrations_from_one_address_are_refused()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        for (var i = 0; i < RegistryLimits.RegistrationsPerHour; i++)
            await RegisterServerAsync(host, ip: "203.0.113.60");

        using var refused = await host.SendAsync(
            HttpMethod.Post, "/api/v1/servers", new { publicAddress = "https://modbot.example", version = "2026.9.0" }, ip: "203.0.113.60");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Theory]
    [InlineData("abc-defg-h", null)]
    [InlineData("a1b2 c3d4", "A1B2C3D4")]
    [InlineData("a1b2-c3d4", "A1B2C3D4")]
    [InlineData("a1b2c3d", null)]
    public void A_typed_link_code_is_cleaned_or_refused(string typed, string? expected) =>
        Assert.Equal(expected, ServerSecrets.CleanLinkCode(typed));

    private static object Report(int coldStops = 0) => new
    {
        publicAddress = "https://modbot.example",
        version = "2026.9.0",
        hostPlatform = "linux-x64",
        groupId = "grp_1",
        groupName = "VRChat Kings",
        groupDescription = "A group.",
        groupIconUrl = "https://api.vrchat.cloud/icon.png",
        groupBannerUrl = "https://api.vrchat.cloud/banner.png",
        discordConnected = true,
        termListsImported = new[] { "modbot_racist_terms" },
        rateLimitColdStops = coldStops,
        wafBlocks = 0,
        aiModerationEnabled = true,
    };

    private static async Task<(Guid Id, string Bearer)> RegisterServerAsync(CloudTestHost host, string ip = "203.0.113.20")
    {
        using var response = await host.SendAsync(
            HttpMethod.Post,
            "/api/v1/servers",
            new { publicAddress = "https://modbot.example", version = "2026.9.0", hostPlatform = "linux-x64" },
            ip);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = body.GetProperty("serverId").GetGuid();
        return (id, $"{id}.{body.GetProperty("secret").GetString()}");
    }

    /// <summary>Makes a code the way Modbot does, tells Cloud its hash, and hands back the code.</summary>
    private static async Task<string> ShowLinkCodeAsync(CloudTestHost host, string bearer)
    {
        var code = ServerSecrets.NewLinkCode();

        using var response = await host.SendAsync(
            HttpMethod.Post, "/api/v1/servers/link-code", new { codeHash = ServerSecrets.Hash(code) }, bearer: bearer);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return code;
    }

    private static async Task<string> SignUpAsync(CloudTestHost host, string email)
    {
        await host.SendAsync(HttpMethod.Post, "/api/v1/accounts", new { email, password = Password });
        await host.SendAsync(HttpMethod.Post, "/api/v1/accounts/verify", new { token = host.Mail.LastToken() });

        using var signedIn = await host.SendAsync(HttpMethod.Post, "/api/v1/accounts/session", new { email, password = Password });
        Assert.Equal(HttpStatusCode.NoContent, signedIn.StatusCode);

        var setCookie = signedIn.Headers.GetValues("Set-Cookie")
            .First(v => v.StartsWith(AccountSessions.CookieName, StringComparison.Ordinal));

        return setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)];
    }
}
