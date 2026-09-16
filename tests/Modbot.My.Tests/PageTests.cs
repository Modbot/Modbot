using System.Net;

namespace Modbot.My.Tests;

/// <summary>
/// Only the app's own routes serve it. Everything else is a 404, and under /api a JSON one.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PageTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/")]
    [InlineData("/register")]
    [InlineData("/go")]
    [InlineData("/go?redir=/pair")]
    [InlineData("/admin")]
    [InlineData("/admin/instances/some-id")]
    [InlineData("/admin/register-page")]
    public async Task EachAppRouteServesTheAppOnFirstLoad(string path)
    {
        await using var host = await MyTestHost.StartAsync(db);

        using var response = await host.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.Equal(MyTestHost.AppHtml, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// /go replaced /pair and /instanceredirect; one route redirects to an instance, not three. A
    /// fallback that served the app for any path would bring both back as pages.
    /// </summary>
    [Theory]
    [InlineData("/pair")]
    [InlineData("/pair?code=123456")]
    [InlineData("/instanceredirect")]
    [InlineData("/instanceredirect?path=/audit")]
    [InlineData("/index.html")]
    [InlineData("/register/extra")]
    [InlineData("/nothing-here")]
    [InlineData("/missing.js")]
    public async Task EveryOtherPathIsNotFound(string path)
    {
        await using var host = await MyTestHost.StartAsync(db);

        using var response = await host.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("GET", "/api/nothing")]
    [InlineData("GET", "/api/instances/some-id/nothing")]
    [InlineData("POST", "/api/nothing")]
    [InlineData("DELETE", "/api/admin")]
    public async Task AnUnknownApiPathIsAJsonNotFound(string method, string path)
    {
        await using var host = await MyTestHost.StartAsync(db);

        using var response = await host.SendAsync(new HttpMethod(method), path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task BuiltAssetsAreServedAndCachedForGood()
    {
        await using var host = await MyTestHost.StartAsync(db);

        using var response = await host.GetAsync(MyTestHost.AssetPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.MaxAge >= TimeSpan.FromDays(365));
    }

    [Fact]
    public async Task TermListsRedirectToCloud()
    {
        await using var host = await MyTestHost.StartAsync(db);

        // The lists moved to Cloud; these routes stay so that anything already pointed at them
        // keeps working (Cloud accounts and registry spec 4).
        using var index = await host.GetAsync("/termlists/index.json");
        Assert.Equal(HttpStatusCode.PermanentRedirect, index.StatusCode);
        Assert.Equal(new Uri(MyTestHost.CloudEndpoint, "termlists/index.json"), index.Headers.Location);

        using var list = await host.GetAsync("/termlists/modbot_profanity_mild.json");
        Assert.Equal(HttpStatusCode.PermanentRedirect, list.StatusCode);

        using var schema = await host.GetAsync("/termlists/_schema.json");
        Assert.Equal(HttpStatusCode.PermanentRedirect, schema.StatusCode);

        // Nothing a caller invented is turned into a redirect.
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync("/termlists/not-a-list.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync("/termlists/..%2Fsecret.json")).StatusCode);
    }

    [Fact]
    public async Task ReadyAnswersWhenTheDatabaseIsReachable()
    {
        await using var host = await MyTestHost.StartAsync(db);

        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/health/ready")).StatusCode);
    }
}
