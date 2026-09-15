using System.Net;

namespace Modbot.Landing.Tests;

/// <summary>
/// /privacy is built from PRIVACY_POLICY.md when that file exists. Until it does, the route is the
/// same 404 as any unknown page.
/// </summary>
public class PrivacyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ThePolicyIsServedOnceItHasBeenBuilt()
    {
        await using var host = await LandingTestHost.StartAsync(privacy: true);

        using var response = await host.GetAsync("/privacy");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(LandingTestHost.PrivacyHtml, await response.Content.ReadAsStringAsync(Ct));
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.Contains("default-src 'self'", string.Join(";", response.Headers.GetValues("Content-Security-Policy")));
    }

    [Fact]
    public async Task WithoutAPolicyTheRouteIsTheNotFoundPage()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync("/privacy");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(LandingTestHost.NotFoundHtml, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task ThePolicyFileIsNotServedByFileName()
    {
        await using var host = await LandingTestHost.StartAsync(privacy: true);

        using var response = await host.GetAsync("/privacy.html");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(LandingTestHost.PrivacyHtml, await response.Content.ReadAsStringAsync(Ct));
    }
}
