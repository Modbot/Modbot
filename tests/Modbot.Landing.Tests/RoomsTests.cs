using System.Net;
using Modbot.Landing.Features.Rooms;

namespace Modbot.Landing.Tests;

/// <summary>
/// /rooms and the feed behind it. The key that opens Modbot Cloud stays on this server: the browser
/// asks this site, and this site asks Cloud.
/// </summary>
public class RoomsTests
{
    private const string OneGroup = """
        {"groups":[{"groupId":"grp_1","groupName":"Night Owls","groupIconUrl":"https://pictures.test/icon.png",
        "groupBannerUrl":null,"groupUrl":"https://vrchat.com/home/group/grp_1","reportedAt":"2026-09-16T12:00:00+00:00",
        "rooms":[{"location":"wrld_1:1~group(grp_1)","worldId":"wrld_1","worldName":"The Great Pug",
        "worldImageUrl":null,"joinLink":"https://vrchat.com/home/launch?worldId=wrld_1","region":"us",
        "openedAt":"2026-09-16T11:00:00+00:00"}]}]}
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ThePageIsServedAtRooms()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync("/rooms");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(LandingTestHost.RoomsHtml, await response.Content.ReadAsStringAsync(Ct));
        Assert.True(response.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task ThePageAllowsPicturesFromVRChatAndScriptsFromNowhereElse()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync("/rooms");
        var policy = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));

        Assert.Contains("img-src 'self' data: https:", policy, StringComparison.Ordinal);
        Assert.Contains("default-src 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self'", policy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePageFileIsNotServedByFileName()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync("/rooms.html");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TheFeedPassesOnWhatCloudSaid()
    {
        await using var host = await LandingTestHost.StartAsync();
        host.Cloud.Body = OneGroup;

        using var response = await host.GetAsync(RoomsEndpoints.Path);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Night Owls", body, StringComparison.Ordinal);
        Assert.Contains("The Great Pug", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheApiKeyGoesToCloudAndNeverToTheBrowser()
    {
        await using var host = await LandingTestHost.StartAsync();
        host.Cloud.Body = OneGroup;

        using var page = await host.GetAsync("/rooms");
        using var feed = await host.GetAsync(RoomsEndpoints.Path);

        var pageHtml = await page.Content.ReadAsStringAsync(Ct);
        var feedBody = await feed.Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain(LandingTestHost.CloudApiKey, pageHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(LandingTestHost.CloudApiKey, feedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(LandingTestHost.CloudApiKey, string.Join(";", page.Headers.Select(h => string.Join(',', h.Value))), StringComparison.Ordinal);
        Assert.DoesNotContain(LandingTestHost.CloudApiKey, string.Join(";", feed.Headers.Select(h => string.Join(',', h.Value))), StringComparison.Ordinal);

        var asked = Assert.Single(host.Cloud.Asked);
        Assert.Equal($"Bearer {LandingTestHost.CloudApiKey}", asked.Authorization);
        Assert.Equal(LandingTestHost.CloudUrl + OpenRooms.CloudPath, asked.Url?.ToString());
    }

    [Fact]
    public async Task OneReadIsSharedByEveryVisitorForAMinute()
    {
        await using var host = await LandingTestHost.StartAsync();
        host.Cloud.Body = OneGroup;

        for (var i = 0; i < 5; i++)
            (await host.GetAsync(RoomsEndpoints.Path)).Dispose();

        Assert.Single(host.Cloud.Asked);

        host.Time.Advance(OpenRooms.Freshness + TimeSpan.FromSeconds(1));
        (await host.GetAsync(RoomsEndpoints.Path)).Dispose();

        Assert.Equal(2, host.Cloud.Asked.Count);
    }

    [Fact]
    public async Task WithNoCloudConfiguredTheFeedIsEmptyAndCloudIsNeverAsked()
    {
        await using var host = await LandingTestHost.StartAsync(cloud: false);

        using var response = await host.GetAsync(RoomsEndpoints.Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OpenRooms.Empty, await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(host.Cloud.Asked);
    }

    [Fact]
    public async Task AnUnreachableCloudKeepsTheLastGoodAnswerAndThenEmpties()
    {
        await using var host = await LandingTestHost.StartAsync();
        host.Cloud.Body = OneGroup;

        (await host.GetAsync(RoomsEndpoints.Path)).Dispose();

        host.Cloud.Unreachable = true;
        host.Time.Advance(OpenRooms.Freshness + TimeSpan.FromSeconds(1));

        using (var stale = await host.GetAsync(RoomsEndpoints.Path))
            Assert.Contains("Night Owls", await stale.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        host.Time.Advance(OpenRooms.KeepStaleFor + TimeSpan.FromSeconds(1));

        using var empty = await host.GetAsync(RoomsEndpoints.Path);
        Assert.Equal(OpenRooms.Empty, await empty.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task ARefusalFromCloudIsNotPassedOnAsAFailedPage()
    {
        await using var host = await LandingTestHost.StartAsync();
        host.Cloud.Status = HttpStatusCode.Unauthorized;
        host.Cloud.Body = "{\"code\":\"unauthorized\"}";

        using var response = await host.GetAsync(RoomsEndpoints.Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OpenRooms.Empty, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task SomethingThatIsNotJsonIsNotPassedOn()
    {
        await using var host = await LandingTestHost.StartAsync();
        host.Cloud.Body = "<html>a proxy's error page</html>";

        using var response = await host.GetAsync(RoomsEndpoints.Path);

        Assert.Equal(OpenRooms.Empty, await response.Content.ReadAsStringAsync(Ct));
    }
}
