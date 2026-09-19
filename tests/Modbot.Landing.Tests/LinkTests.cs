using System.Net;
using Modbot.Landing.Configuration;

namespace Modbot.Landing.Tests;

/// <summary>
/// /discord and /github are short addresses people type and the site links to with an icon. The
/// server sends them on to whatever it was given, so the page can be built once and the addresses
/// changed without rebuilding it.
/// </summary>
public class LinkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DiscordSendsPeopleToTheAddressTheServerWasGiven()
    {
        await using var host = await LandingTestHost.StartAsync(discord: "https://discord.gg/modbot");

        using var response = await host.GetAsync("/discord");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://discord.gg/modbot", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task TheAddressIsNeverCached()
    {
        await using var host = await LandingTestHost.StartAsync(discord: "https://discord.gg/modbot");

        using var response = await host.GetAsync("/discord");

        // An invite is replaced from time to time, and the next visitor must get the new one.
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    /// <summary>
    /// The mark is on every page and the pages are built once, so it cannot be hidden when there is
    /// no address. The person who clicks it gets a page that says so.
    /// </summary>
    [Fact]
    public async Task WithoutADiscordAddressTheRouteIsThePageThatSaysSo()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync("/discord");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(LandingTestHost.NoDiscordHtml, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task BeforeABuildThatPageIsStillAFourOhFour()
    {
        await using var host = await LandingTestHost.StartAsync(built: false);

        using var response = await host.GetAsync("/discord");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ThatPageIsNotServedByFileName()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync("/discord.html");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GithubSendsPeopleToTheProjectsRepositoryWithoutBeingTold()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync("/github");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(LandingEnvironment.DefaultGithubUrl, response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task GithubCanBePointedAtAForkInstead()
    {
        await using var host = await LandingTestHost.StartAsync(github: "https://github.com/someone/fork");

        using var response = await host.GetAsync("/github");

        Assert.Equal("https://github.com/someone/fork", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task HeadOnAShortAddressAnswersLikeGet()
    {
        await using var host = await LandingTestHost.StartAsync(discord: "https://discord.gg/modbot");

        using var response = await host.SendAsync(HttpMethod.Head, "/discord");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://discord.gg/modbot", response.Headers.Location?.ToString());
    }
}
