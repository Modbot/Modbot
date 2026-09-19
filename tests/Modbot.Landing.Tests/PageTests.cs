using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Modbot.Landing.Tests;

public class PageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheRootServesTheBuiltPage()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(LandingTestHost.LandingHtml, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task ThePageIsCheckedAgainOnEveryVisit()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync("/");

        // It names one build's hashed files, so a cached copy must not outlive a deploy.
        Assert.True(response.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task AQueryStringStillServesThePage()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync("/?utm_source=discord");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HeadOnTheRootAnswersLikeGet()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.SendAsync(HttpMethod.Head, "/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ThePagePolicyAllowsItsOwnInlineScriptAndNoOther()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync("/");
        var policy = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));

        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(LandingTestHost.ThemeScript)));
        Assert.Contains($"script-src 'self' 'sha256-{hash}';", policy);
        Assert.DoesNotContain("unsafe-inline' 'sha256", policy);
        Assert.Contains("frame-ancestors 'none'", policy);
    }

    [Fact]
    public async Task EachPageBesideTheLandingPageIsServedAtItsOwnAddress()
    {
        await using var host = await LandingTestHost.StartAsync();

        foreach (var (path, _, html) in LandingTestHost.OtherPages)
        {
            using var response = await host.GetAsync(path);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(html, await response.Content.ReadAsStringAsync(Ct));
            Assert.True(response.Headers.CacheControl?.NoCache);
        }
    }

    [Fact]
    public async Task BeforeABuildThosePagesAreTheNotFoundPage()
    {
        await using var host = await LandingTestHost.StartAsync(built: false);

        foreach (var (path, _, _) in LandingTestHost.OtherPages)
        {
            using var response = await host.GetAsync(path);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task EveryResponseCarriesTheSecurityHeaders()
    {
        await using var host = await LandingTestHost.StartAsync();

        foreach (var path in new[] { "/", "/about", "/discord", LandingTestHost.AssetPath, "/nothing-here", "/health/live" })
        {
            using var response = await host.GetAsync(path);

            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
            Assert.Equal("strict-origin-when-cross-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        }
    }

    [Fact]
    public async Task BeforeABuildTheRootSaysSoRatherThanServingNothing()
    {
        await using var host = await LandingTestHost.StartAsync(built: false);

        using var response = await host.GetAsync("/");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
