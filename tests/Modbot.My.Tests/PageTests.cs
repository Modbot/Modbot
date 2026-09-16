using System.Net;

namespace Modbot.My.Tests;

/// <summary>
/// Only the app's own routes serve it. Everything else is a 404, and under /api a JSON one.
/// </summary>
public class PageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/")]
    [InlineData("/register")]
    [InlineData("/go")]
    [InlineData("/go?redir=/pair")]
    public async Task EachAppRouteServesTheAppOnFirstLoad(string path)
    {
        await using var host = await MyTestHost.StartAsync();

        using var response = await host.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.Equal(MyTestHost.AppHtml, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// /go replaced /pair and /instanceredirect; one route redirects to an instance, not three. A
    /// fallback that served the app for any path would bring both back as pages. /admin is gone with
    /// the registry, which moved to Modbot Cloud (central services spec 4.4).
    /// </summary>
    [Theory]
    [InlineData("/pair")]
    [InlineData("/pair?code=123456")]
    [InlineData("/instanceredirect")]
    [InlineData("/instanceredirect?path=/audit")]
    [InlineData("/admin")]
    [InlineData("/admin/instances/some-id")]
    [InlineData("/index.html")]
    [InlineData("/register/extra")]
    [InlineData("/nothing-here")]
    [InlineData("/missing.js")]
    public async Task EveryOtherPathIsNotFound(string path)
    {
        await using var host = await MyTestHost.StartAsync();

        using var response = await host.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("GET", "/api/nothing")]
    [InlineData("GET", "/api/instances")]
    [InlineData("GET", "/api/stats")]
    [InlineData("POST", "/api/admin/login")]
    [InlineData("DELETE", "/api/admin")]
    public async Task AnUnknownApiPathIsAJsonNotFound(string method, string path)
    {
        await using var host = await MyTestHost.StartAsync();

        using var response = await host.SendAsync(new HttpMethod(method), path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task BuiltAssetsAreServedAndCachedForGood()
    {
        await using var host = await MyTestHost.StartAsync();

        using var response = await host.GetAsync(MyTestHost.AssetPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.MaxAge >= TimeSpan.FromDays(365));
    }

    [Fact]
    public async Task TermListsRedirectToCloud()
    {
        await using var host = await MyTestHost.StartAsync();

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

    /// <summary>
    /// Readiness does not depend on Cloud. A Cloud that is down is a page with fewer instances on
    /// it, not a service that should be taken out of rotation.
    /// </summary>
    [Fact]
    public async Task ReadyAnswersEvenWhenCloudIsUnreachable()
    {
        await using var host = await MyTestHost.StartAsync();
        host.Cloud.Unreachable = true;

        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/health/live")).StatusCode);
    }
}
