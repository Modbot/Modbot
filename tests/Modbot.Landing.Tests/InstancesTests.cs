using System.Net;
using Modbot.Landing.Features.Instances;

namespace Modbot.Landing.Tests;

/// <summary>
/// /instances and the feed behind it. The key that opens Modbot Cloud stays on this server: the browser
/// asks this site, and this site asks Cloud.
/// </summary>
public class InstancesTests
{
    private const string OneGroup = """
        {"groups":[{"groupId":"grp_1","groupName":"Night Owls","groupIconUrl":"https://pictures.test/icon.png",
        "groupBannerUrl":null,"groupUrl":"https://vrchat.com/home/group/grp_1","reportedAt":"2026-09-16T12:00:00+00:00",
        "instances":[{"location":"wrld_1:1~group(grp_1)","worldId":"wrld_1","worldName":"The Great Pug",
        "worldImageUrl":null,"joinLink":"https://vrchat.com/home/launch?worldId=wrld_1","region":"us",
        "openedAt":"2026-09-16T11:00:00+00:00"}]}]}
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ThePageIsServedAtInstances()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync("/instances");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(LandingTestHost.InstancesHtml, await response.Content.ReadAsStringAsync(Ct));
        Assert.True(response.Headers.CacheControl?.NoCache);
    }

    /// <summary>The page was /rooms until 2026-09-17, and other sites link it.</summary>
    [Fact]
    public async Task TheOldAddressRedirectsToInstances()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync("/rooms");

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("/instances", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task ThePageAllowsPicturesFromVRChatAndScriptsFromNowhereElse()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync("/instances");
        var policy = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));

        Assert.Contains("img-src 'self' data: https:", policy, StringComparison.Ordinal);
        Assert.Contains("default-src 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self'", policy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePageFileIsNotServedByFileName()
    {
        await using var host = await LandingTestHost.StartAsync();

        using var response = await host.GetAsync("/instances.html");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TheFeedPassesOnWhatCloudSaid()
    {
        await using var host = await LandingTestHost.StartAsync();
        host.Cloud.Body = OneGroup;

        using var response = await host.GetAsync(InstancesEndpoints.Path);
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

        using var page = await host.GetAsync("/instances");
        using var feed = await host.GetAsync(InstancesEndpoints.Path);

        var pageHtml = await page.Content.ReadAsStringAsync(Ct);
        var feedBody = await feed.Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain(LandingTestHost.CloudApiKey, pageHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(LandingTestHost.CloudApiKey, feedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(LandingTestHost.CloudApiKey, string.Join(";", page.Headers.Select(h => string.Join(',', h.Value))), StringComparison.Ordinal);
        Assert.DoesNotContain(LandingTestHost.CloudApiKey, string.Join(";", feed.Headers.Select(h => string.Join(',', h.Value))), StringComparison.Ordinal);

        var asked = Assert.Single(host.Cloud.Asked);
        Assert.Equal($"Bearer {LandingTestHost.CloudApiKey}", asked.Authorization);
        Assert.Equal(LandingTestHost.CloudUrl + OpenInstances.CloudPath, asked.Url?.ToString());
    }

    [Fact]
    public async Task OneReadIsSharedByEveryVisitorForAMinute()
    {
        await using var host = await LandingTestHost.StartAsync();
        host.Cloud.Body = OneGroup;

        for (var i = 0; i < 5; i++)
            (await host.GetAsync(InstancesEndpoints.Path)).Dispose();

        Assert.Single(host.Cloud.Asked);

        host.Time.Advance(OpenInstances.Freshness + TimeSpan.FromSeconds(1));
        (await host.GetAsync(InstancesEndpoints.Path)).Dispose();

        Assert.Equal(2, host.Cloud.Asked.Count);
    }

    [Fact]
    public async Task WithNoCloudConfiguredTheFeedIsEmptyAndCloudIsNeverAsked()
    {
        await using var host = await LandingTestHost.StartAsync(cloud: false);

        using var response = await host.GetAsync(InstancesEndpoints.Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OpenInstances.Empty, await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(host.Cloud.Asked);
    }

    [Fact]
    public async Task AnUnreachableCloudKeepsTheLastGoodAnswerAndThenEmpties()
    {
        await using var host = await LandingTestHost.StartAsync();
        host.Cloud.Body = OneGroup;

        (await host.GetAsync(InstancesEndpoints.Path)).Dispose();

        host.Cloud.Unreachable = true;
        host.Time.Advance(OpenInstances.Freshness + TimeSpan.FromSeconds(1));

        using (var stale = await host.GetAsync(InstancesEndpoints.Path))
            Assert.Contains("Night Owls", await stale.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        host.Time.Advance(OpenInstances.KeepStaleFor + TimeSpan.FromSeconds(1));

        using var empty = await host.GetAsync(InstancesEndpoints.Path);
        Assert.Equal(OpenInstances.Empty, await empty.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task ARefusalFromCloudIsNotPassedOnAsAFailedPage()
    {
        await using var host = await LandingTestHost.StartAsync();
        host.Cloud.Status = HttpStatusCode.Unauthorized;
        host.Cloud.Body = "{\"code\":\"unauthorized\"}";

        using var response = await host.GetAsync(InstancesEndpoints.Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OpenInstances.Empty, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task SomethingThatIsNotJsonIsNotPassedOn()
    {
        await using var host = await LandingTestHost.StartAsync();
        host.Cloud.Body = "<html>a proxy's error page</html>";

        using var response = await host.GetAsync(InstancesEndpoints.Path);

        Assert.Equal(OpenInstances.Empty, await response.Content.ReadAsStringAsync(Ct));
    }
}
