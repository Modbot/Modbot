using System.Net;

namespace Modbot.Landing.Tests;

public class NotFoundTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/nothing-here")]
    [InlineData("/pricing")]
    [InlineData("/go")]
    [InlineData("/docs/setup")]
    public async Task AnUnknownPageIsA404WithTheNotFoundPage(string path)
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(LandingTestHost.NotFoundHtml, await response.Content.ReadAsStringAsync(Ct));
        Assert.True(response.Headers.CacheControl?.NoCache);
    }

    /// <summary>The built pages are only served through their routes, never under their file names.</summary>
    [Theory]
    [InlineData("/index.html")]
    [InlineData("/404.html")]
    [InlineData("/INDEX.HTML")]
    [InlineData("/features.html")]
    [InlineData("/self-host.html")]
    [InlineData("/about.html")]
    [InlineData("/license.html")]
    public async Task TheBuiltPagesAreNotServedByFileName(string path)
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/assets/missing.js")]
    [InlineData("/missing.png")]
    public async Task AMissingFileIsABare404(string path)
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task APostIsNotAnswered()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.SendAsync(HttpMethod.Post, "/");

        Assert.False(response.IsSuccessStatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}
