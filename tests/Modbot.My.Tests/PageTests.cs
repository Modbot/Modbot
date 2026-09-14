using System.Net;

namespace Modbot.My.Tests;

[Collection(nameof(PostgresCollection))]
public class PageTests(PostgresFixture db)
{
    [Theory]
    [InlineData("/")]
    [InlineData("/go")]
    [InlineData("/go?redir=/pair")]
    public async Task EachSelectorRouteServesThePageOnFirstLoad(string path)
    {
        await using var host = await MyTestHost.StartAsync(db);

        var html = await host.GetStringAsync(path);

        Assert.Contains("my.modbot.co", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePageHandlesTheGoRoute()
    {
        await using var host = await MyTestHost.StartAsync(db);

        var html = await host.GetStringAsync("/go?redir=/pair");

        Assert.Contains("route === '/go'", html, StringComparison.Ordinal);
        Assert.Contains("q.get('redir')", html, StringComparison.Ordinal);
    }

    /// <summary>/go replaced both; one route redirects to an instance, not three.</summary>
    [Theory]
    [InlineData("/pair")]
    [InlineData("/instanceredirect?path=/audit")]
    public async Task TheOldRedirectRoutesAreGone(string path)
    {
        await using var host = await MyTestHost.StartAsync(db);

        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task TermListsAreServedToAnyone()
    {
        await using var host = await MyTestHost.StartAsync(db);

        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/termlists/index.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/termlists/modbot_profanity_mild.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync("/termlists/not-a-list.json")).StatusCode);
    }

    [Fact]
    public async Task ReadyAnswersWhenTheDatabaseIsReachable()
    {
        await using var host = await MyTestHost.StartAsync(db);

        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/health/ready")).StatusCode);
    }
}
