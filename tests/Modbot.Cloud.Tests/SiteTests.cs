using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modbot.Cloud.Features.Site;

namespace Modbot.Cloud.Tests;

/// <summary>
/// What my.modbot.co and the landing page read and save through the proxy key
/// (Cloud accounts and registry spec 3.5).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class SiteTests(PostgresFixture db)
{
    private const string Visitor = "198.51.100.4";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_the_proxy_key_nothing_under_site_answers()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var anonymous = await host.SendAsync(HttpMethod.Get, "/api/v1/site/counts");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // An account session is not a proxy key either.
        using var wrong = await host.SendAsync(HttpMethod.Get, "/api/v1/site/counts", bearer: "not-the-key");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task A_visit_is_saved_against_the_address_my_modbot_co_passed_on()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var saved = await host.SendAsync(
            HttpMethod.Post,
            "/api/v1/site/visits",
            new { address = Visitor, url = "https://modbot.example/register?x=1" },
            bearer: CloudTestHost.ProxyKey);
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        var items = await VisitsAsync(host, Visitor);

        Assert.Single(items);
        Assert.Equal("https://modbot.example", items[0].GetProperty("serverUrl").GetString());

        // Still sent under the old name until the deployed my.modbot.co reads the new one.
        Assert.Equal("https://modbot.example", items[0].GetProperty("instanceUrl").GetString());
    }

    [Fact]
    public async Task One_page_view_saved_twice_counts_once()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        await SaveAsync(host, "https://modbot.example");
        await SaveAsync(host, "https://modbot.example");

        var items = await VisitsAsync(host, Visitor);
        Assert.Equal(1, items[0].GetProperty("visits").GetInt32());

        // Past the five minutes, it is a new visit.
        host.Time.Advance(ServerVisits.RepeatWindow + TimeSpan.FromMinutes(1));
        await SaveAsync(host, "https://modbot.example");

        items = await VisitsAsync(host, Visitor);
        Assert.Equal(2, items[0].GetProperty("visits").GetInt32());
    }

    [Fact]
    public async Task One_address_never_sees_another_address_list()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        await SaveAsync(host, "https://modbot.example");

        Assert.Empty(await VisitsAsync(host, "203.0.113.99"));
    }

    [Fact]
    public async Task A_visit_older_than_ninety_days_drops_off()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        await SaveAsync(host, "https://modbot.example");
        host.Time.Advance(ServerVisits.HistoryReach + TimeSpan.FromDays(1));

        Assert.Empty(await VisitsAsync(host, Visitor));
    }

    [Fact]
    public async Task A_visited_address_carries_the_group_when_a_server_reports_it()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var registered = await host.SendAsync(
            HttpMethod.Post, "/api/v1/servers", new { publicAddress = "https://modbot.example", version = "2026.9.0" });
        var body = await registered.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var bearer = $"{body.GetProperty("serverId").GetGuid()}.{body.GetProperty("secret").GetString()}";

        await host.SendAsync(
            HttpMethod.Post,
            "/api/v1/servers/report",
            new
            {
                publicAddress = "https://modbot.example",
                groupName = "VRChat Kings",
                groupIconUrl = "https://api.vrchat.cloud/icon.png",
            },
            bearer: bearer);

        await SaveAsync(host, "https://modbot.example");

        var items = await VisitsAsync(host, Visitor);
        Assert.Equal("VRChat Kings", items[0].GetProperty("groupName").GetString());
        Assert.Equal("https://api.vrchat.cloud/icon.png", items[0].GetProperty("groupIconUrl").GetString());
    }

    [Fact]
    public async Task The_counts_the_landing_page_reads_are_two_numbers()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        await host.SendAsync(HttpMethod.Post, "/api/v1/servers", new { publicAddress = "https://modbot.example" });

        using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/site/counts", bearer: CloudTestHost.ProxyKey);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(1, body.GetProperty("registered").GetInt32());
        Assert.Equal(1, body.GetProperty("activeLast30Days").GetInt32());

        // Two fields, so nothing about any one deployment can leak through this endpoint.
        Assert.Equal(2, body.EnumerateObject().Count());
    }

    [Fact]
    public async Task An_address_that_is_not_an_address_is_ignored_rather_than_stored()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var saved = await host.SendAsync(
            HttpMethod.Post,
            "/api/v1/site/visits",
            new { address = "not-an-address", url = "https://modbot.example" },
            bearer: CloudTestHost.ProxyKey);

        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        Assert.Empty(await VisitsAsync(host, Visitor));
    }

    [Fact]
    public async Task An_http_address_is_refused()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var saved = await host.SendAsync(
            HttpMethod.Post,
            "/api/v1/site/visits",
            new { address = Visitor, url = "http://modbot.example" },
            bearer: CloudTestHost.ProxyKey);

        Assert.Equal(HttpStatusCode.BadRequest, saved.StatusCode);
    }

    private static async Task SaveAsync(CloudTestHost host, string url)
    {
        using var response = await host.SendAsync(
            HttpMethod.Post, "/api/v1/site/visits", new { address = Visitor, url }, bearer: CloudTestHost.ProxyKey);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<List<JsonElement>> VisitsAsync(CloudTestHost host, string address)
    {
        using var response = await host.SendAsync(
            HttpMethod.Get, $"/api/v1/site/visits?address={Uri.EscapeDataString(address)}", bearer: CloudTestHost.ProxyKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return [.. body.GetProperty("items").EnumerateArray()];
    }
}
