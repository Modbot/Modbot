using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Configuration;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Conventions;

/// <summary>
/// MODBOT_CORS_ORIGINS (API conventions design §6): a listed address may call the API from a
/// browser with a key; anything else, and every address when the variable is unset, gets no CORS
/// headers; and the session cookie is never allowed to ride along.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CorsTests
{
    private const string Tools = "https://tools.example.org";

    private readonly PostgresFixture _db;

    public CorsTests(PostgresFixture db) => _db = db;

    private Task<ApiTestHost> StartAsync(params string[] origins)
        => ApiTestHost.StartAsync(_db, configure: services =>
            services.AddSingleton(new ModbotEnvironment { CorsOrigins = origins }));

    private static HttpRequestMessage Preflight(string path, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        return request;
    }

    [Fact]
    public async Task AListedAddress_IsAnsweredWithoutSigningIn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(Tools);

        var response = await host.Client.SendAsync(Preflight("/api/members", Tools), ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(Tools, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task AnAnswer_CarriesTheHeaderEvenWhenItIsARefusal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(Tools);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/members");
        request.Headers.Add("Origin", Tools);
        var response = await host.Client.SendAsync(request, ct);

        // Not signed in, and the page can read that it is not.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(Tools, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task AnAddressNotListed_GetsNoHeaders()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(Tools);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/members");
        request.Headers.Add("Origin", "https://elsewhere.example");
        var response = await host.Client.SendAsync(request, ct);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Unset_NoAddressIsAllowed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/members");
        request.Headers.Add("Origin", Tools);
        var response = await host.Client.SendAsync(request, ct);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ServerDetails_KeepTheirOwnAnyOriginRule()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(Tools);

        var response = await host.Client.SendAsync(Preflight("/api/server", "https://my.example"), ct);

        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }
}
