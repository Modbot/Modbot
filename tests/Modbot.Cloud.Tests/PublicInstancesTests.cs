using System.Net;
using System.Net.Http.Json;
using Modbot.Cloud.Features.Installs;
using Modbot.Cloud.Features.PublicInstances;

namespace Modbot.Cloud.Tests;

/// <summary>
/// The public instances feed: a Modbot server reports its group's open instances, and modbot.co reads them
/// with a key.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PublicInstancesTests
{
    private const string Path = "/api/v1/public-instances";

    private readonly PostgresFixture _db;

    public PublicInstancesTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A server id and secret the way a Modbot makes them up for itself.</summary>
    private static (Guid Id, string Bearer) NewServer()
    {
        var id = Guid.NewGuid();
        return (id, $"{id:D}.{InstallSecrets.NewSecret()}");
    }

    private static object Report(string groupId, params object[] instances) => new
    {
        groupId,
        groupName = "Night Owls",
        groupIconUrl = "https://pictures.test/icon.png",
        groupBannerUrl = "https://pictures.test/banner.png",
        instances,
    };

    private static object Instance(string location, string worldId = "wrld_1") => new
    {
        location,
        worldId,
        worldName = "The Great Pug",
        worldImageUrl = "https://pictures.test/world.png",
        joinLink = "https://vrchat.com/home/launch?worldId=wrld_1",
        region = "us",
        openedAt = CloudTestHost.Start.AddHours(-1),
    };

    private static async Task<PublicInstancesFeed> ReadAsync(CloudTestHost host, string? key = CloudTestHost.InstancesKey)
    {
        using var response = await host.GetAsync(Path, key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PublicInstancesFeed>(Ct))!;
    }

    [Fact]
    public async Task AReportIsTakenAndReadBack()
    {
        await using var host = await CloudTestHost.StartAsync(_db);
        var (_, bearer) = NewServer();

        using var sent = await host.SendAsync(
            HttpMethod.Put, Path, Report("grp_1", Instance("wrld_1:1~group(grp_1)~groupAccessType(public)")), bearer: bearer);
        Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);

        var feed = await ReadAsync(host);

        var group = Assert.Single(feed.Groups);
        Assert.Equal("grp_1", group.GroupId);
        Assert.Equal("Night Owls", group.GroupName);
        Assert.Equal("https://vrchat.com/home/group/grp_1", group.GroupUrl);

        var instance = Assert.Single(group.Instances);
        Assert.Equal("The Great Pug", instance.WorldName);
        Assert.Equal("us", instance.Region);
    }

    /// <summary>
    /// A Modbot built before 2026-09-17 reports to <c>/api/v1/public-rooms</c> with a <c>rooms</c>
    /// field. Its group must not fall off modbot.co for being a version behind.
    /// </summary>
    [Fact]
    public async Task AReportInTheOldShapeToTheOldPathStillLands()
    {
        await using var host = await CloudTestHost.StartAsync(_db);
        var (_, bearer) = NewServer();

        var old = new
        {
            groupId = "grp_1",
            groupName = "Night Owls",
            rooms = new[] { Instance("wrld_1:1~group(grp_1)~groupAccessType(public)") },
        };

        using var sent = await host.SendAsync(HttpMethod.Put, "/api/v1/public-rooms", old, bearer: bearer);
        Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);

        var instance = Assert.Single(Assert.Single((await ReadAsync(host)).Groups).Instances);
        Assert.Equal("wrld_1:1~group(grp_1)~groupAccessType(public)", instance.Location);

        using var stopped = await host.SendAsync(HttpMethod.Delete, "/api/v1/public-rooms", bearer: bearer);
        Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);
    }

    [Fact]
    public async Task AnInstanceThatStopsBeingReportedDisappears()
    {
        await using var host = await CloudTestHost.StartAsync(_db);
        var (_, bearer) = NewServer();

        using (await host.SendAsync(
            HttpMethod.Put,
            Path,
            Report("grp_1", Instance("wrld_1:1~group(grp_1)"), Instance("wrld_1:2~group(grp_1)")),
            bearer: bearer))
        {
        }

        Assert.Equal(2, (await ReadAsync(host)).Groups.Single().Instances.Count);

        using (await host.SendAsync(HttpMethod.Put, Path, Report("grp_1", Instance("wrld_1:2~group(grp_1)")), bearer: bearer))
        {
        }

        var left = Assert.Single((await ReadAsync(host)).Groups.Single().Instances);
        Assert.Equal("wrld_1:2~group(grp_1)", left.Location);
    }

    [Fact]
    public async Task AGroupWithNothingOpenIsStillListed()
    {
        await using var host = await CloudTestHost.StartAsync(_db);
        var (_, bearer) = NewServer();

        using (await host.SendAsync(HttpMethod.Put, Path, Report("grp_1"), bearer: bearer))
        {
        }

        var group = Assert.Single((await ReadAsync(host)).Groups);
        Assert.Empty(group.Instances);
    }

    [Fact]
    public async Task AServerThatStopsReportingDropsOffTheFeed()
    {
        await using var host = await CloudTestHost.StartAsync(_db);
        var (_, bearer) = NewServer();

        using (await host.SendAsync(HttpMethod.Put, Path, Report("grp_1", Instance("wrld_1:1")), bearer: bearer))
        {
        }

        Assert.Single((await ReadAsync(host)).Groups);

        host.Time.Advance(PublicInstancesEndpoints.StaleAfter + TimeSpan.FromMinutes(1));

        Assert.Empty((await ReadAsync(host)).Groups);
    }

    [Fact]
    public async Task TurningTheSettingOffTakesTheGroupOffAtOnce()
    {
        await using var host = await CloudTestHost.StartAsync(_db);
        var (_, bearer) = NewServer();

        using (await host.SendAsync(HttpMethod.Put, Path, Report("grp_1", Instance("wrld_1:1")), bearer: bearer))
        {
        }

        using var dropped = await host.SendAsync(HttpMethod.Delete, Path, bearer: bearer);
        Assert.Equal(HttpStatusCode.NoContent, dropped.StatusCode);

        Assert.Empty((await ReadAsync(host)).Groups);
    }

    [Fact]
    public async Task AnotherServerCannotRewriteAGroupsInstances()
    {
        await using var host = await CloudTestHost.StartAsync(_db);
        var (_, mine) = NewServer();
        var (_, theirs) = NewServer();

        using (await host.SendAsync(HttpMethod.Put, Path, Report("grp_1", Instance("wrld_1:1")), bearer: mine))
        {
        }

        using var taken = await host.SendAsync(
            HttpMethod.Put, Path, Report("grp_1", Instance("wrld_bad:9")), bearer: theirs);

        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);

        var instance = Assert.Single((await ReadAsync(host)).Groups.Single().Instances);
        Assert.Equal("wrld_1:1", instance.Location);
    }

    [Fact]
    public async Task AWrongSecretForAKnownServerIsRefused()
    {
        await using var host = await CloudTestHost.StartAsync(_db);
        var (id, bearer) = NewServer();

        using (await host.SendAsync(HttpMethod.Put, Path, Report("grp_1", Instance("wrld_1:1")), bearer: bearer))
        {
        }

        using var wrong = await host.SendAsync(
            HttpMethod.Put,
            Path,
            Report("grp_1", Instance("wrld_bad:9")),
            bearer: $"{id:D}.{InstallSecrets.NewSecret()}");

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task ADeadServersGroupCanBeTakenOverByItsReplacement()
    {
        await using var host = await CloudTestHost.StartAsync(_db);
        var (_, before) = NewServer();
        var (_, after) = NewServer();

        using (await host.SendAsync(HttpMethod.Put, Path, Report("grp_1", Instance("wrld_1:1")), bearer: before))
        {
        }

        host.Time.Advance(PublicInstancesEndpoints.StaleAfter + TimeSpan.FromMinutes(1));

        using var again = await host.SendAsync(
            HttpMethod.Put, Path, Report("grp_1", Instance("wrld_2:2")), bearer: after);

        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);

        var instance = Assert.Single((await ReadAsync(host)).Groups.Single().Instances);
        Assert.Equal("wrld_2:2", instance.Location);
    }

    [Fact]
    public async Task TheFeedNeedsAKey()
    {
        await using var host = await CloudTestHost.StartAsync(_db);

        using var none = await host.GetAsync(Path);
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);

        using var wrong = await host.GetAsync(Path, "not-the-key");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task TheRootKeyOpensTheFeedToo()
    {
        await using var host = await CloudTestHost.StartAsync(_db);

        using var response = await host.GetAsync(Path, CloudTestHost.RootKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WithNoKeysSetTheFeedRefusesEveryone()
    {
        await using var host = await CloudTestHost.StartAsync(_db, rootApiKey: null, instancesApiKey: null);

        using var response = await host.GetAsync(Path, CloudTestHost.InstancesKey);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AReportWithNoGroupIsRefused()
    {
        await using var host = await CloudTestHost.StartAsync(_db);
        var (_, bearer) = NewServer();

        using var response = await host.SendAsync(
            HttpMethod.Put, Path, new { instances = Array.Empty<object>() }, bearer: bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AReportWithNoAuthorizationIsRefused()
    {
        await using var host = await CloudTestHost.StartAsync(_db);

        using var response = await host.SendAsync(HttpMethod.Put, Path, Report("grp_1"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A picture or link that is not plain https is dropped where it arrives. These end up in an
    /// <c>img src</c> and an <c>href</c> on a public page.
    /// </summary>
    [Fact]
    public async Task AddressesThatAreNotHttpsAreDropped()
    {
        await using var host = await CloudTestHost.StartAsync(_db);
        var (_, bearer) = NewServer();

        using (await host.SendAsync(
            HttpMethod.Put,
            Path,
            new
            {
                groupId = "grp_1",
                groupName = "Night Owls",
                groupIconUrl = "javascript:alert(1)",
                groupBannerUrl = "http://pictures.test/banner.png",
                instances = new[]
                {
                    new
                    {
                        location = "wrld_1:1",
                        worldId = "wrld_1",
                        worldName = "The Great Pug",
                        worldImageUrl = "data:image/png;base64,AAAA",
                        joinLink = "javascript:alert(2)",
                        region = "us",
                        openedAt = CloudTestHost.Start,
                    },
                },
            },
            bearer: bearer))
        {
        }

        var group = Assert.Single((await ReadAsync(host)).Groups);

        Assert.Null(group.GroupIconUrl);
        Assert.Null(group.GroupBannerUrl);

        var instance = Assert.Single(group.Instances);
        Assert.Null(instance.WorldImageUrl);
        Assert.Null(instance.JoinLink);
    }

    [Fact]
    public async Task TheFeedIsNeverCachedInBetween()
    {
        await using var host = await CloudTestHost.StartAsync(_db);

        using var response = await host.GetAsync(Path, CloudTestHost.InstancesKey);

        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.Private);
    }
}
