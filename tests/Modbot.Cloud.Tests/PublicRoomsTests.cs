using System.Net;
using System.Net.Http.Json;
using Modbot.Cloud.Features.Installs;
using Modbot.Cloud.Features.PublicRooms;

namespace Modbot.Cloud.Tests;

/// <summary>
/// The public rooms feed: a Modbot server reports its group's open rooms, and modbot.co reads them
/// with a key.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PublicRoomsTests
{
    private const string Path = "/api/v1/public-rooms";

    private readonly PostgresFixture _db;

    public PublicRoomsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A server id and secret the way a Modbot makes them up for itself.</summary>
    private static (Guid Id, string Bearer) NewServer()
    {
        var id = Guid.NewGuid();
        return (id, $"{id:D}.{InstallSecrets.NewSecret()}");
    }

    private static object Report(string groupId, params object[] rooms) => new
    {
        groupId,
        groupName = "Night Owls",
        groupIconUrl = "https://pictures.test/icon.png",
        groupBannerUrl = "https://pictures.test/banner.png",
        rooms,
    };

    private static object Room(string location, string worldId = "wrld_1") => new
    {
        location,
        worldId,
        worldName = "The Great Pug",
        worldImageUrl = "https://pictures.test/world.png",
        joinLink = "https://vrchat.com/home/launch?worldId=wrld_1",
        region = "us",
        openedAt = CloudTestHost.Start.AddHours(-1),
    };

    private static async Task<PublicRoomsFeed> ReadAsync(CloudTestHost host, string? key = CloudTestHost.RoomsKey)
    {
        using var response = await host.GetAsync(Path, key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PublicRoomsFeed>(Ct))!;
    }

    [Fact]
    public async Task AReportIsTakenAndReadBack()
    {
        await using var host = await CloudTestHost.StartAsync(_db);
        var (_, bearer) = NewServer();

        using var sent = await host.SendAsync(
            HttpMethod.Put, Path, Report("grp_1", Room("wrld_1:1~group(grp_1)~groupAccessType(public)")), bearer: bearer);
        Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);

        var feed = await ReadAsync(host);

        var group = Assert.Single(feed.Groups);
        Assert.Equal("grp_1", group.GroupId);
        Assert.Equal("Night Owls", group.GroupName);
        Assert.Equal("https://vrchat.com/home/group/grp_1", group.GroupUrl);

        var room = Assert.Single(group.Rooms);
        Assert.Equal("The Great Pug", room.WorldName);
        Assert.Equal("us", room.Region);
    }

    [Fact]
    public async Task ARoomThatStopsBeingReportedDisappears()
    {
        await using var host = await CloudTestHost.StartAsync(_db);
        var (_, bearer) = NewServer();

        using (await host.SendAsync(
            HttpMethod.Put,
            Path,
            Report("grp_1", Room("wrld_1:1~group(grp_1)"), Room("wrld_1:2~group(grp_1)")),
            bearer: bearer))
        {
        }

        Assert.Equal(2, (await ReadAsync(host)).Groups.Single().Rooms.Count);

        using (await host.SendAsync(HttpMethod.Put, Path, Report("grp_1", Room("wrld_1:2~group(grp_1)")), bearer: bearer))
        {
        }

        var left = Assert.Single((await ReadAsync(host)).Groups.Single().Rooms);
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
        Assert.Empty(group.Rooms);
    }

    [Fact]
    public async Task AServerThatStopsReportingDropsOffTheFeed()
    {
        await using var host = await CloudTestHost.StartAsync(_db);
        var (_, bearer) = NewServer();

        using (await host.SendAsync(HttpMethod.Put, Path, Report("grp_1", Room("wrld_1:1")), bearer: bearer))
        {
        }

        Assert.Single((await ReadAsync(host)).Groups);

        host.Time.Advance(PublicRoomsEndpoints.StaleAfter + TimeSpan.FromMinutes(1));

        Assert.Empty((await ReadAsync(host)).Groups);
    }

    [Fact]
    public async Task TurningTheSettingOffTakesTheGroupOffAtOnce()
    {
        await using var host = await CloudTestHost.StartAsync(_db);
        var (_, bearer) = NewServer();

        using (await host.SendAsync(HttpMethod.Put, Path, Report("grp_1", Room("wrld_1:1")), bearer: bearer))
        {
        }

        using var dropped = await host.SendAsync(HttpMethod.Delete, Path, bearer: bearer);
        Assert.Equal(HttpStatusCode.NoContent, dropped.StatusCode);

        Assert.Empty((await ReadAsync(host)).Groups);
    }

    [Fact]
    public async Task AnotherServerCannotRewriteAGroupsRooms()
    {
        await using var host = await CloudTestHost.StartAsync(_db);
        var (_, mine) = NewServer();
        var (_, theirs) = NewServer();

        using (await host.SendAsync(HttpMethod.Put, Path, Report("grp_1", Room("wrld_1:1")), bearer: mine))
        {
        }

        using var taken = await host.SendAsync(
            HttpMethod.Put, Path, Report("grp_1", Room("wrld_bad:9")), bearer: theirs);

        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);

        var room = Assert.Single((await ReadAsync(host)).Groups.Single().Rooms);
        Assert.Equal("wrld_1:1", room.Location);
    }

    [Fact]
    public async Task AWrongSecretForAKnownServerIsRefused()
    {
        await using var host = await CloudTestHost.StartAsync(_db);
        var (id, bearer) = NewServer();

        using (await host.SendAsync(HttpMethod.Put, Path, Report("grp_1", Room("wrld_1:1")), bearer: bearer))
        {
        }

        using var wrong = await host.SendAsync(
            HttpMethod.Put,
            Path,
            Report("grp_1", Room("wrld_bad:9")),
            bearer: $"{id:D}.{InstallSecrets.NewSecret()}");

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task ADeadServersGroupCanBeTakenOverByItsReplacement()
    {
        await using var host = await CloudTestHost.StartAsync(_db);
        var (_, before) = NewServer();
        var (_, after) = NewServer();

        using (await host.SendAsync(HttpMethod.Put, Path, Report("grp_1", Room("wrld_1:1")), bearer: before))
        {
        }

        host.Time.Advance(PublicRoomsEndpoints.StaleAfter + TimeSpan.FromMinutes(1));

        using var again = await host.SendAsync(
            HttpMethod.Put, Path, Report("grp_1", Room("wrld_2:2")), bearer: after);

        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);

        var room = Assert.Single((await ReadAsync(host)).Groups.Single().Rooms);
        Assert.Equal("wrld_2:2", room.Location);
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
        await using var host = await CloudTestHost.StartAsync(_db, rootApiKey: null, roomsApiKey: null);

        using var response = await host.GetAsync(Path, CloudTestHost.RoomsKey);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AReportWithNoGroupIsRefused()
    {
        await using var host = await CloudTestHost.StartAsync(_db);
        var (_, bearer) = NewServer();

        using var response = await host.SendAsync(
            HttpMethod.Put, Path, new { rooms = Array.Empty<object>() }, bearer: bearer);

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
                rooms = new[]
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

        var room = Assert.Single(group.Rooms);
        Assert.Null(room.WorldImageUrl);
        Assert.Null(room.JoinLink);
    }

    [Fact]
    public async Task TheFeedIsNeverCachedInBetween()
    {
        await using var host = await CloudTestHost.StartAsync(_db);

        using var response = await host.GetAsync(Path, CloudTestHost.RoomsKey);

        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.Private);
    }
}
