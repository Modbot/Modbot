using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Modbot.Cloud.Tests;

/// <summary>
/// What a my.modbot.co register visit learned about a Modbot address, and how it sits beside what a
/// server reported about itself (register details spec 3).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class VisitedServerTests(PostgresFixture db)
{
    private const string Visitor = "198.51.100.4";

    private const string Address = "https://modbot.example";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_the_proxy_key_nothing_can_be_learned_about_an_address()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var anonymous = await host.SendAsync(
            HttpMethod.Post, "/api/v1/site/servers", new { url = Address, groupName = "VRChat Kings" });

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task What_an_address_said_about_itself_is_kept_and_shown_on_the_visitor_list()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        await LearnAsync(host, new
        {
            url = Address,
            groupId = "grp_1",
            groupName = "VRChat Kings",
            groupIconUrl = "https://api.vrchat.cloud/icon.png",
            groupBannerUrl = "https://api.vrchat.cloud/banner.png",
            ownerEmail = "Owner@Example.com",
        });

        await SaveVisitAsync(host, Address);

        var items = await VisitsAsync(host, Visitor);
        Assert.Equal("VRChat Kings", items[0].GetProperty("groupName").GetString());
        Assert.Equal("https://api.vrchat.cloud/icon.png", items[0].GetProperty("groupIconUrl").GetString());
    }

    /// <summary>
    /// The list a visitor reads is the one place the owner's address must never appear
    /// (register details spec 3.4).
    /// </summary>
    [Fact]
    public async Task The_owner_address_is_on_nothing_a_visitor_can_read()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        await LearnAsync(host, new { url = Address, groupName = "VRChat Kings", ownerEmail = "owner@example.com" });
        await SaveVisitAsync(host, Address);

        using var response = await host.SendAsync(
            HttpMethod.Get,
            $"/api/v1/site/visits?address={Uri.EscapeDataString(Visitor)}",
            bearer: CloudTestHost.ProxyKey);

        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain("owner@example.com", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ownerEmail", body, StringComparison.OrdinalIgnoreCase);

        // Nor on the counts the landing page reads.
        using var counts = await host.SendAsync(HttpMethod.Get, "/api/v1/site/counts", bearer: CloudTestHost.ProxyKey);
        Assert.DoesNotContain(
            "owner@example.com", await counts.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A report arrives with a server's own secret; a visit arrives because somebody opened a link.
    /// Where both know something, the report wins.
    /// </summary>
    [Fact]
    public async Task What_a_server_reported_beats_what_a_visit_learned()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        await LearnAsync(host, new
        {
            url = Address,
            groupName = "Learned from a visit",
            groupIconUrl = "https://api.vrchat.cloud/visit-icon.png",
        });

        var bearer = await RegisterAndReportAsync(host, new
        {
            publicAddress = Address,
            groupName = "Reported by the server",
            groupIconUrl = "https://api.vrchat.cloud/report-icon.png",
        });

        Assert.NotNull(bearer);

        await SaveVisitAsync(host, Address);

        var items = await VisitsAsync(host, Visitor);
        Assert.Equal("Reported by the server", items[0].GetProperty("groupName").GetString());
        Assert.Equal("https://api.vrchat.cloud/report-icon.png", items[0].GetProperty("groupIconUrl").GetString());
    }

    /// <summary>
    /// Field by field, so a server that has registered but not finished setting up still shows the
    /// name a visit learned.
    /// </summary>
    [Fact]
    public async Task A_registry_row_with_no_group_falls_back_to_what_the_visit_learned()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        await RegisterAndReportAsync(host, new { publicAddress = Address, version = "2026.9.0" });

        await LearnAsync(host, new
        {
            url = Address,
            groupName = "Learned from a visit",
            groupIconUrl = "https://api.vrchat.cloud/visit-icon.png",
        });

        await SaveVisitAsync(host, Address);

        var items = await VisitsAsync(host, Visitor);
        Assert.Equal("Learned from a visit", items[0].GetProperty("groupName").GetString());
    }

    [Fact]
    public async Task A_visit_never_changes_what_a_server_reported()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        await RegisterAndReportAsync(host, new { publicAddress = Address, groupName = "Reported by the server" });
        await LearnAsync(host, new { url = Address, groupName = "Learned from a visit" });

        var servers = await AdminAsync(host, "/api/admin/servers");
        var rows = servers.GetProperty("items").EnumerateArray().ToList();

        Assert.Single(rows);
        Assert.Equal("Reported by the server", rows[0].GetProperty("server").GetProperty("groupName").GetString());
    }

    [Fact]
    public async Task A_later_answer_that_says_nothing_leaves_what_is_stored_alone()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        await SaveVisitAsync(host, Address);
        await LearnAsync(host, new { url = Address, groupName = "VRChat Kings" });

        host.Time.Advance(TimeSpan.FromDays(1));

        // A later answer with nothing in it leaves what is stored alone.
        await LearnAsync(host, new { url = Address, groupBannerUrl = "https://api.vrchat.cloud/banner.png" });

        var addresses = await AdminAsync(host, "/api/admin/page-servers");
        var row = addresses.GetProperty("items").EnumerateArray().Single();

        Assert.Equal("VRChat Kings", row.GetProperty("groupName").GetString());
        Assert.Equal("https://api.vrchat.cloud/banner.png", row.GetProperty("groupBannerUrl").GetString());
    }

    [Fact]
    public async Task Admin_reads_the_owner_address_and_the_group_for_a_noted_address()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        await SaveVisitAsync(host, Address);
        await LearnAsync(host, new
        {
            url = Address,
            groupId = "grp_1",
            groupName = "VRChat Kings",
            ownerEmail = "owner@example.com",
        });

        var addresses = await AdminAsync(host, "/api/admin/page-servers");
        var row = addresses.GetProperty("items").EnumerateArray().Single();

        Assert.Equal("grp_1", row.GetProperty("groupId").GetString());
        Assert.Equal("owner@example.com", row.GetProperty("ownerEmail").GetString());

        // And beside the registered server at the same address.
        await RegisterAndReportAsync(host, new { publicAddress = Address, groupName = "VRChat Kings" });

        var servers = await AdminAsync(host, "/api/admin/servers");
        var server = servers.GetProperty("items").EnumerateArray().Single();

        Assert.Equal("owner@example.com", server.GetProperty("ownerEmail").GetString());
    }

    [Fact]
    public async Task An_address_that_is_not_an_https_address_is_refused()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var refused = await host.SendAsync(
            HttpMethod.Post,
            "/api/v1/site/servers",
            new { url = "http://modbot.example", groupName = "VRChat Kings" },
            bearer: CloudTestHost.ProxyKey);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task A_picture_that_is_not_an_https_address_is_left_out()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        await SaveVisitAsync(host, Address);
        await LearnAsync(host, new
        {
            url = Address,
            groupName = "VRChat Kings",
            groupIconUrl = "http://modbot.example/icon.png",
        });

        var addresses = await AdminAsync(host, "/api/admin/page-servers");
        var row = addresses.GetProperty("items").EnumerateArray().Single();

        Assert.Equal("VRChat Kings", row.GetProperty("groupName").GetString());
        Assert.Null(row.GetProperty("groupIconUrl").GetString());
    }

    [Fact]
    public async Task The_old_page_instances_paths_and_names_still_answer()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        await SaveVisitAsync(host, Address);

        var addresses = await AdminAsync(host, "/api/admin/page-instances");
        var row = addresses.GetProperty("items").EnumerateArray().Single();

        Assert.Equal(Address, row.GetProperty("serverUrl").GetString());
        Assert.Equal(Address, row.GetProperty("instanceUrl").GetString());

        var stats = await AdminAsync(host, "/api/admin/registry-stats");
        Assert.Equal(1, stats.GetProperty("pageServers").GetInt32());
        Assert.Equal(1, stats.GetProperty("pageInstances").GetInt32());

        using var deleted = await host.SendAsync(
            HttpMethod.Delete,
            $"/api/admin/page-instances?url={Uri.EscapeDataString(Address)}",
            bearer: CloudTestHost.RootKey);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var after = await AdminAsync(host, "/api/admin/page-servers");
        Assert.Empty(after.GetProperty("items").EnumerateArray());
    }

    private static async Task LearnAsync(CloudTestHost host, object body)
    {
        using var response = await host.SendAsync(
            HttpMethod.Post, "/api/v1/site/servers", body, bearer: CloudTestHost.ProxyKey);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task SaveVisitAsync(CloudTestHost host, string url)
    {
        using var response = await host.SendAsync(
            HttpMethod.Post, "/api/v1/site/visits", new { address = Visitor, url }, bearer: CloudTestHost.ProxyKey);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>Registers a server and sends one report as that server. Returns its bearer.</summary>
    private static async Task<string> RegisterAndReportAsync(CloudTestHost host, object report)
    {
        using var registered = await host.SendAsync(
            HttpMethod.Post, "/api/v1/servers", new { publicAddress = Address, version = "2026.9.0" });

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        var body = await registered.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var bearer = $"{body.GetProperty("serverId").GetGuid()}.{body.GetProperty("secret").GetString()}";

        using var reported = await host.SendAsync(HttpMethod.Post, "/api/v1/servers/report", report, bearer: bearer);
        Assert.Equal(HttpStatusCode.Accepted, reported.StatusCode);

        return bearer;
    }

    private static async Task<List<JsonElement>> VisitsAsync(CloudTestHost host, string address)
    {
        using var response = await host.SendAsync(
            HttpMethod.Get,
            $"/api/v1/site/visits?address={Uri.EscapeDataString(address)}",
            bearer: CloudTestHost.ProxyKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return [.. body.GetProperty("items").EnumerateArray()];
    }

    private static async Task<JsonElement> AdminAsync(CloudTestHost host, string path)
    {
        using var response = await host.SendAsync(HttpMethod.Get, path, bearer: CloudTestHost.RootKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }
}
