using System.Net;

namespace Modbot.My.Tests;

[Collection(nameof(PostgresCollection))]
public class InstanceTests(PostgresFixture db)
{
    private const string Id = "4f2c7a1e-1111-4000-8000-00000000abcd";

    private static Task<HttpResponseMessage> RegisterAsync(MyTestHost host, string id, string url, string? version = "2026.9.0") =>
        host.PostJsonAsync("/api/instances/register", new { instanceId = id, instanceUrl = url, version });

    [Fact]
    public async Task RegisteringStoresTheInstanceWithOnlyItsOrigin()
    {
        await using var host = await MyTestHost.StartAsync(db);

        var registered = await RegisterAsync(host, Id, "https://modbot.example/settings?tab=data");
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);

        var instance = await host.ReadWithKeyAsync($"/api/instances/{Id}");

        Assert.Equal("https://modbot.example", instance.GetProperty("instanceUrl").GetString());
        Assert.Equal("2026.9.0", instance.GetProperty("version").GetString());
        Assert.False(instance.GetProperty("analyticsEnabled").GetBoolean());
    }

    [Fact]
    public async Task RegisteringAgainMovesTheInstanceAndKeepsWhenItFirstRegistered()
    {
        await using var host = await MyTestHost.StartAsync(db);

        await RegisterAsync(host, Id, "https://old.example");
        host.Time.Advance(TimeSpan.FromDays(3));
        await RegisterAsync(host, Id, "https://new.example", version: null);

        var instance = await host.ReadWithKeyAsync($"/api/instances/{Id}");

        Assert.Equal("https://new.example", instance.GetProperty("instanceUrl").GetString());
        Assert.Equal("2026.9.0", instance.GetProperty("version").GetString());
        Assert.True(instance.GetProperty("lastSeenAt").GetDateTimeOffset() > instance.GetProperty("registeredAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("http://modbot.example")]
    [InlineData("https://user:password@modbot.example")]
    [InlineData("modbot.example")]
    public async Task AUrlThatIsNotAPlainHttpsAddressIsRefused(string url)
    {
        await using var host = await MyTestHost.StartAsync(db);

        var response = await RegisterAsync(host, Id, url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UsageForAnInstanceThatNeverRegisteredIsRefused()
    {
        await using var host = await MyTestHost.StartAsync(db);

        var response = await host.PostJsonAsync($"/api/instances/{Id}/usage", new { version = "2026.9.0" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UsageIsStoredOnTheInstance()
    {
        await using var host = await MyTestHost.StartAsync(db);
        await RegisterAsync(host, Id, "https://modbot.example");

        var reported = await host.PostJsonAsync($"/api/instances/{Id}/usage", new
        {
            version = "2026.9.1",
            scaleBucket = "100-999",
            pairedClients = 4,
            discordConnected = true,
            termListsImported = new[] { "modbot_profanity_mild", "modbot_advertising_spam" },
            rateLimitColdStops = 1,
            wafBlocks = 0,
        });
        Assert.Equal(HttpStatusCode.Accepted, reported.StatusCode);

        var instance = await host.ReadWithKeyAsync($"/api/instances/{Id}");

        Assert.True(instance.GetProperty("analyticsEnabled").GetBoolean());
        Assert.Equal("2026.9.1", instance.GetProperty("version").GetString());
        Assert.Equal(4, instance.GetProperty("pairedClients").GetInt32());
        Assert.Equal(2, instance.GetProperty("termListsImported").GetArrayLength());
    }

    [Fact]
    public async Task TheListIsNewestFirstAndPaged()
    {
        await using var host = await MyTestHost.StartAsync(db);

        await RegisterAsync(host, "older", "https://older.example");
        host.Time.Advance(TimeSpan.FromHours(1));
        await RegisterAsync(host, "newer", "https://newer.example");

        var page = await host.ReadWithKeyAsync("/api/instances?limit=1");

        Assert.Equal(2, page.GetProperty("total").GetInt32());
        var only = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("newer", only.GetProperty("instanceId").GetString());
    }

    [Fact]
    public async Task AnUnknownInstanceIsNotFound()
    {
        await using var host = await MyTestHost.StartAsync(db);

        var response = await host.GetWithKeyAsync("/api/instances/nobody");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
