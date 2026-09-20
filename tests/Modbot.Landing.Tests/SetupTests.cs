using System.Net;
using Modbot.Landing.Features.Setup;

namespace Modbot.Landing.Tests;

/// <summary>
/// <c>/get.sh</c> and <c>/docker-compose.yml</c>: what a self-hoster's curl and browser both get.
/// </summary>
public class SetupTests
{
    [Fact]
    public async Task TheInstallScriptIsServedAsPlainText()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync(SetupEndpoints.ScriptPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal(
            LandingTestHost.InstallScript,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheComposeFileIsServedAsPlainText()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync(SetupEndpoints.ComposePath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            LandingTestHost.ComposeFileText,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A browser must show the script rather than offer to save it, which is the whole reason it is
    /// served as text: a person is asked to read it before piping it to a shell.
    /// </summary>
    [Fact]
    public async Task TheScriptIsNotOfferedAsADownload()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync(SetupEndpoints.ScriptPath);

        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Theory]
    [InlineData(SetupEndpoints.ScriptPath)]
    [InlineData(SetupEndpoints.ComposePath)]
    public async Task NeitherFileIsCachedWithoutBeingChecked(string path)
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync(path);

        Assert.True(response.Headers.CacheControl?.NoCache);
    }

    [Theory]
    [InlineData(SetupEndpoints.ScriptPath)]
    [InlineData(SetupEndpoints.ComposePath)]
    public async Task CurlCanCheckTheAddressBeforeFetchingIt(string path)
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.SendAsync(HttpMethod.Head, path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// Before a build there is nothing to serve. The body is a comment in both shell and YAML, so a
    /// person who piped it to sh without asking curl to fail on an error runs nothing.
    /// </summary>
    [Theory]
    [InlineData(SetupEndpoints.ScriptPath)]
    [InlineData(SetupEndpoints.ComposePath)]
    public async Task AnUnbuiltFileSaysSoAndRunsNothing(string path)
    {
        await using var host = await LandingTestHost.StartAsync(built: false);

        using var response = await host.GetAsync(path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.StartsWith("#", body, StringComparison.Ordinal);
    }

    /// <summary>The script names the compose file, and this site serves both, so they must agree.</summary>
    [Fact]
    public void TheTwoFilesAreServedAtTheAddressesTheScriptUses()
    {
        Assert.Equal("/get.sh", SetupEndpoints.ScriptPath);
        Assert.Equal("/docker-compose.yml", SetupEndpoints.ComposePath);
    }
}
