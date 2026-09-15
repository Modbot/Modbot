using System.Net;

namespace Modbot.Landing.Tests;

public class StaticFileTests
{
    [Fact]
    public async Task BuiltAssetsAreCachedForAYearAndNeverRechecked()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync(LandingTestHost.AssetPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Public);
        Assert.Equal(TimeSpan.FromDays(365), response.Headers.CacheControl?.MaxAge);
        Assert.Contains("immutable", response.Headers.CacheControl?.Extensions.Select(e => e.Name) ?? []);
    }

    [Fact]
    public async Task FilesThatKeepTheirNameAreCachedForADay()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync("/favicon.svg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TimeSpan.FromDays(1), response.Headers.CacheControl?.MaxAge);
        Assert.DoesNotContain("immutable", response.Headers.CacheControl?.Extensions.Select(e => e.Name) ?? []);
    }

    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    public async Task ScriptsAreCompressedWhenTheBrowserAsks(string encoding)
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.SendAsync(
            HttpMethod.Get,
            LandingTestHost.AssetPath,
            new Dictionary<string, string> { ["Accept-Encoding"] = encoding });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(encoding, Assert.Single(response.Content.Headers.ContentEncoding));
    }

    [Fact]
    public async Task ThePageIsCompressedToo()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.SendAsync(
            HttpMethod.Get,
            "/",
            new Dictionary<string, string> { ["Accept-Encoding"] = "br" });

        Assert.Equal("br", Assert.Single(response.Content.Headers.ContentEncoding));
    }
}
