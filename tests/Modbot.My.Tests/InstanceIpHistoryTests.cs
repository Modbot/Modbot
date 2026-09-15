using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Modbot.My.Tests;

/// <summary>Where registered deployments call from, kept per address.</summary>
[Collection(nameof(PostgresCollection))]
public class InstanceIpHistoryTests(PostgresFixture db)
{
    private const string Id = "4f2c7a1e-2222-4000-8000-00000000abcd";
    private const string FirstIp = "203.0.113.50";
    private const string SecondIp = "2001:db8::50";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> RegisterAsync(MyTestHost host, string ip, string id = Id, string url = "https://modbot.example", string version = "2026.9.0") =>
        host.PostJsonAsync("/api/instances/register", new { instanceId = id, instanceUrl = url, version }, ip);

    private static async Task<JsonElement[]> HistoryAsync(MyTestHost host) =>
        (await host.ReadWithKeyAsync($"/api/instances/{Id}/ip-history")).GetProperty("items").EnumerateArray().ToArray();

    [Fact]
    public async Task RegisteringStoresTheCallersAddressOnTheInstanceAndInItsHistory()
    {
        await using var host = await MyTestHost.StartAsync(db);

        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(host, FirstIp)).StatusCode);

        var instance = await host.ReadWithKeyAsync($"/api/instances/{Id}");
        Assert.Equal(FirstIp, instance.GetProperty("ipAddress").GetString());

        var row = Assert.Single(await HistoryAsync(host));
        Assert.Equal(FirstIp, row.GetProperty("ipAddress").GetString());
        Assert.Equal(1, row.GetProperty("requests").GetInt32());
    }

    [Fact]
    public async Task UsageFromTheSameAddressAddsARequestToTheSameRow()
    {
        await using var host = await MyTestHost.StartAsync(db);
        var registered = host.Time.GetUtcNow();
        await RegisterAsync(host, FirstIp);

        host.Time.Advance(TimeSpan.FromHours(1));
        var usage = await host.PostJsonAsync($"/api/instances/{Id}/usage", new { version = "2026.9.1" }, FirstIp);
        Assert.Equal(HttpStatusCode.Accepted, usage.StatusCode);

        var row = Assert.Single(await HistoryAsync(host));
        Assert.Equal(2, row.GetProperty("requests").GetInt32());
        Assert.Equal(registered, row.GetProperty("firstSeenAt").GetDateTimeOffset());
        Assert.Equal(registered.AddHours(1), row.GetProperty("lastSeenAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task AServerThatMovesToANewAddressGetsANewRow()
    {
        await using var host = await MyTestHost.StartAsync(db);
        var first = host.Time.GetUtcNow();
        await RegisterAsync(host, FirstIp);

        host.Time.Advance(TimeSpan.FromDays(1));
        await host.PostJsonAsync($"/api/instances/{Id}/usage", new { version = "2026.9.1" }, SecondIp);

        var instance = await host.ReadWithKeyAsync($"/api/instances/{Id}");
        Assert.Equal(SecondIp, instance.GetProperty("ipAddress").GetString());

        var rows = await HistoryAsync(host);
        Assert.Equal([SecondIp, FirstIp], rows.Select(r => r.GetProperty("ipAddress").GetString()));
        Assert.Equal(first, rows[1].GetProperty("lastSeenAt").GetDateTimeOffset());
        Assert.All(rows, r => Assert.Equal(1, r.GetProperty("requests").GetInt32()));
    }

    [Fact]
    public async Task AServersAddressFollowsTheSameRuleThroughCloudflare()
    {
        await using var host = await MyTestHost.StartAsync(db);

        var headers = new Dictionary<string, string>
        {
            ["X-Forwarded-For"] = "162.158.0.1",
            ["CF-Connecting-IP"] = "198.51.100.77",
        };
        await host.SendAsync(
            HttpMethod.Post,
            "/api/instances/register",
            body: new { instanceId = Id, instanceUrl = "https://modbot.example" },
            headers: headers);

        Assert.Equal("198.51.100.77", Assert.Single(await HistoryAsync(host)).GetProperty("ipAddress").GetString());
    }

    [Fact]
    public async Task DeletingAnInstanceDeletesItsHistory()
    {
        await using var host = await MyTestHost.StartAsync(db);
        await RegisterAsync(host, FirstIp);

        var deleted = await host.SendAsync(HttpMethod.Delete, $"/api/instances/{Id}", bearer: MyTestHost.RootKey);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await host.GetWithKeyAsync($"/api/instances/{Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetWithKeyAsync($"/api/instances/{Id}/ip-history")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await host.SendAsync(HttpMethod.Delete, $"/api/instances/{Id}", bearer: MyTestHost.RootKey)).StatusCode);

        await using var context = db.NewContext();
        Assert.Empty(await context.RegisteredInstanceIps.ToListAsync(Ct));
    }

    [Fact]
    public async Task TheListCanBeSearchedByIdUrlOrVersion()
    {
        await using var host = await MyTestHost.StartAsync(db);
        await RegisterAsync(host, FirstIp, id: "alpha", url: "https://alpha.example", version: "2026.9.0");
        await RegisterAsync(host, FirstIp, id: "beta", url: "https://beta.example", version: "2026.9.1");

        async Task<string?[]> Search(string term) =>
            (await host.ReadWithKeyAsync($"/api/instances?search={Uri.EscapeDataString(term)}"))
                .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("instanceId").GetString()).ToArray();

        Assert.Equal(["beta"], await Search("BETA.example"));
        Assert.Equal(["beta"], await Search("2026.9.1"));
        Assert.Equal(["alpha"], await Search("alph"));
        Assert.Empty(await Search("%"));
        Assert.Equal(2, (await Search("")).Length);
    }
}
