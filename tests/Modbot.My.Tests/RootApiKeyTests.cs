using System.Net;
using Modbot.My.Auth;

namespace Modbot.My.Tests;

/// <summary>
/// Reading the registry needs ROOT_API_KEY. Registering and reporting do not, because a deployment
/// has no key.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class RootApiKeyTests(PostgresFixture db)
{
    public static TheoryData<string> ReadingEndpoints =>
    [
        "/api/instances",
        "/api/instances/some-id",
        "/api/instances/some-id/ip-history",
        "/api/register-page-instances",
        "/api/stats",
        "/api/admin/session",
    ];

    [Theory]
    [MemberData(nameof(ReadingEndpoints))]
    public async Task AReadingEndpointRefusesARequestWithNoKey(string path)
    {
        await using var host = await MyTestHost.StartAsync(db);

        var response = await host.GetWithKeyAsync(path, key: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ReadingEndpoints))]
    public async Task AReadingEndpointRefusesTheWrongKey(string path)
    {
        await using var host = await MyTestHost.StartAsync(db);

        var response = await host.GetWithKeyAsync(path, key: MyTestHost.RootKey + "x");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ReadingEndpoints))]
    public async Task WithNoRootKeySetOnTheServerEveryReadIsRefused(string path)
    {
        await using var host = await MyTestHost.StartAsync(db, rootApiKey: null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetWithKeyAsync(path, key: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetWithKeyAsync(path, key: "anything")).StatusCode);
    }

    [Fact]
    public async Task TheRightKeyIsAccepted()
    {
        await using var host = await MyTestHost.StartAsync(db);

        var response = await host.GetWithKeyAsync("/api/stats");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RegisteringNeedsNoKey()
    {
        await using var host = await MyTestHost.StartAsync(db);

        var response = await host.PostJsonAsync("/api/instances/register", new
        {
            instanceId = "b6f0d9a4-0000-4000-8000-000000000001",
            instanceUrl = "https://modbot.example",
            version = "2026.9.0",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void AnEmptyKeyCountsAsNoKey()
    {
        var key = new RootApiKey("");

        Assert.False(key.IsConfigured);
        Assert.False(key.Matches(""));
    }
}
