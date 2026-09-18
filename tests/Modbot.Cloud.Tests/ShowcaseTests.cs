using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Modbot.Cloud.Tests;

/// <summary>
/// The sponsors and early adopters every Modbot reads, and who may change them.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ShowcaseTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Entry(
        string kind = "sponsor",
        string name = "Someone",
        string link = "https://example.com",
        string? groupId = null,
        int order = 0) => new
        {
            kind,
            name,
            link,
            imageUrl = "https://example.com/picture.png",
            vrChatGroupId = groupId,
            groupImageUrl = groupId is null ? null : "https://example.com/group.png",
            groupBannerUrl = groupId is null ? null : "https://example.com/banner.png",
            sortOrder = order,
        };

    private static Task<HttpResponseMessage> AddAsync(CloudTestHost host, object entry) =>
        host.SendAsync(HttpMethod.Post, "/api/admin/showcase", entry, bearer: CloudTestHost.RootKey);

    [Fact]
    public async Task ASponsorTypedInAdminIsReadableByEveryModbot()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using (var added = await AddAsync(host, Entry(name: "Kind Person", groupId: "grp_1234")))
        {
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        }

        // No sign-in at all: every Modbot in the world reads this.
        using var response = await host.GetAsync("/api/v1/sponsors");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var item = page.GetProperty("items")[0];

        Assert.Equal("Kind Person", item.GetProperty("name").GetString());
        Assert.Equal("grp_1234", item.GetProperty("vrChatGroupId").GetString());
        Assert.Equal("https://example.com/banner.png", item.GetProperty("groupBannerUrl").GetString());
    }

    [Fact]
    public async Task TheTwoKindsAreReadSeparately()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using (var a = await AddAsync(host, Entry(name: "A sponsor")))
            Assert.Equal(HttpStatusCode.Created, a.StatusCode);

        using (var b = await AddAsync(host, Entry(kind: "early-adopter", name: "An early group")))
            Assert.Equal(HttpStatusCode.Created, b.StatusCode);

        using var sponsors = await host.GetAsync("/api/v1/sponsors");
        var sponsorPage = await sponsors.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("A sponsor", sponsorPage.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal(1, sponsorPage.GetProperty("items").GetArrayLength());

        using var early = await host.GetAsync("/api/v1/early-adopters");
        var earlyPage = await early.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("An early group", earlyPage.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal(1, earlyPage.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task TheListIsInTheOrderTheAdminChose()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        foreach (var (name, order) in new[] { ("Third", 30), ("First", 10), ("Second", 20) })
        {
            using var added = await AddAsync(host, Entry(name: name, order: order));
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        }

        using var response = await host.GetAsync("/api/v1/sponsors");
        var page = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(
            ["First", "Second", "Third"],
            page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task OnlyAnAdministratorMayChangeIt()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var (_, installBearer) = await host.RegisterAsync();

        using var anonymous = await host.SendAsync(HttpMethod.Post, "/api/admin/showcase", Entry());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var asInstall = await host.SendAsync(
            HttpMethod.Post, "/api/admin/showcase", Entry(), bearer: installBearer);
        Assert.Equal(HttpStatusCode.Unauthorized, asInstall.StatusCode);

        await using var cloud = db.NewCloudContext();
        Assert.Equal(0, await cloud.ShowcaseEntries.CountAsync(Ct));
    }

    [Fact]
    public async Task ALinkThatIsNotHttpIsRefused()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var response = await AddAsync(host, new
        {
            kind = "sponsor",
            name = "Trouble",
            link = "javascript:alert(1)",
            imageUrl = "",
            vrChatGroupId = (string?)null,
            groupImageUrl = (string?)null,
            groupBannerUrl = (string?)null,
            sortOrder = 0,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AKindCloudDoesNotKnowIsRefused()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var response = await AddAsync(host, Entry(kind: "friend"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnEntryCanBeChangedAndRemoved()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        Guid id;
        using (var added = await AddAsync(host, Entry(name: "Before")))
        {
            var body = await added.Content.ReadFromJsonAsync<JsonElement>(Ct);
            id = body.GetProperty("id").GetGuid();
        }

        using (var saved = await host.SendAsync(
            HttpMethod.Put, $"/api/admin/showcase/{id}", Entry(name: "After"), bearer: CloudTestHost.RootKey))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        using (var response = await host.GetAsync("/api/v1/sponsors"))
        {
            var page = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal("After", page.GetProperty("items")[0].GetProperty("name").GetString());
        }

        using (var removed = await host.SendAsync(
            HttpMethod.Delete, $"/api/admin/showcase/{id}", bearer: CloudTestHost.RootKey))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        using (var response = await host.GetAsync("/api/v1/sponsors"))
        {
            var page = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal(0, page.GetProperty("items").GetArrayLength());
        }
    }

    [Fact]
    public async Task ContributorsAreAnEmptyListWhenGitHubCannotBeAsked()
    {
        // No GITHUB_TOKEN in the test host, and the repository is private, so GitHub answers 404.
        // An empty list is the right answer: the Credits page simply has no contributors on it.
        await using var host = await CloudTestHost.StartAsync(db);

        using var response = await host.GetAsync("/api/v1/contributors");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(0, page.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task ThePictureOnASavedRowIsServedByCloudItself()
    {
        // The address an administrator types in is usually VRChat's, and VRChat will not serve it to
        // anybody else. Cloud fetches it once, on save, and hands every reader its own address.
        await using var host = await CloudTestHost.StartAsync(db);

        using (var added = await AddAsync(host, new
        {
            kind = "sponsor",
            name = "Kind Person",
            link = "https://example.com",
            imageUrl = CloudTestHost.PictureAddress,
            vrChatGroupId = "grp_1234",
            groupImageUrl = CloudTestHost.PictureAddress,
            groupBannerUrl = CloudTestHost.BannerAddress,
            sortOrder = 0,
        }))
        {
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        }

        using var response = await host.GetAsync("/api/v1/sponsors");
        var item = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items")[0];

        var picture = item.GetProperty("imageUrl").GetString()!;
        var banner = item.GetProperty("groupBannerUrl").GetString()!;

        Assert.StartsWith($"{CloudTestHost.PublicAddress}api/v1/showcase-pictures/", picture, StringComparison.Ordinal);
        Assert.StartsWith($"{CloudTestHost.PublicAddress}api/v1/showcase-pictures/", banner, StringComparison.Ordinal);
        Assert.NotEqual(picture, banner);

        // No sign-in: the companion reads this from a moderator's own PC.
        using var served = await host.GetAsync(new Uri(picture).AbsolutePath);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(CloudTestHost.PictureBytes, await served.Content.ReadAsByteArrayAsync(Ct));

        using var servedBanner = await host.GetAsync(new Uri(banner).AbsolutePath);
        Assert.Equal(CloudTestHost.BannerBytes, await servedBanner.Content.ReadAsByteArrayAsync(Ct));
    }

    [Theory]
    [InlineData(CloudTestHost.NotAPictureAddress)]
    [InlineData(CloudTestHost.TooBigAddress)]
    [InlineData("https://pictures.test/nothing-there.png")]
    public async Task SomethingThatIsNotAPictureIsNotKept(string address)
    {
        // A page calling itself a picture, something far bigger than Cloud will keep, and an address
        // that answers nothing. None of them becomes bytes served from Cloud's own domain.
        await using var host = await CloudTestHost.StartAsync(db);

        using (var added = await AddAsync(host, new
        {
            kind = "sponsor",
            name = "Trouble",
            link = "https://example.com",
            imageUrl = address,
            vrChatGroupId = (string?)null,
            groupImageUrl = (string?)null,
            groupBannerUrl = (string?)null,
            sortOrder = 0,
        }))
        {
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        }

        await using (var cloud = db.NewCloudContext())
        {
            Assert.Equal(0, await cloud.ShowcasePictures.CountAsync(Ct));
        }

        // The row still carries what was typed in, so an administrator can see and fix it.
        using var response = await host.GetAsync("/api/v1/sponsors");
        var item = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items")[0];

        Assert.Equal(address, item.GetProperty("imageUrl").GetString());
    }

    [Fact]
    public async Task AGroupsIdNameIconAndBannerAllSurviveTheRoundTrip()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        Guid id;
        using (var added = await AddAsync(host, new
        {
            kind = "early-adopter",
            name = "Cat Lounge",
            link = "https://example.com/cats",
            imageUrl = "",
            vrChatGroupId = "grp_cats",
            groupImageUrl = CloudTestHost.PictureAddress,
            groupBannerUrl = CloudTestHost.BannerAddress,
            sortOrder = 5,
        }))
        {
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
            id = (await added.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        }

        // What an administrator sees: the four fields exactly as typed, plus the copies Cloud kept.
        using (var admin = await host.GetAsync("/api/admin/showcase", bearer: CloudTestHost.RootKey))
        {
            var row = (await admin.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items")[0];

            Assert.Equal(id, row.GetProperty("id").GetGuid());
            Assert.Equal("Cat Lounge", row.GetProperty("name").GetString());
            Assert.Equal("grp_cats", row.GetProperty("vrChatGroupId").GetString());
            Assert.Equal(CloudTestHost.PictureAddress, row.GetProperty("groupImageUrl").GetString());
            Assert.Equal(CloudTestHost.BannerAddress, row.GetProperty("groupBannerUrl").GetString());
            Assert.NotNull(row.GetProperty("savedGroupImageUrl").GetString());
            Assert.NotNull(row.GetProperty("savedGroupBannerUrl").GetString());
        }

        // What every Modbot sees: the same four, with Cloud's own addresses for the two pictures.
        using (var read = await host.GetAsync("/api/v1/early-adopters"))
        {
            var row = (await read.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items")[0];

            Assert.Equal("Cat Lounge", row.GetProperty("name").GetString());
            Assert.Equal("grp_cats", row.GetProperty("vrChatGroupId").GetString());
            Assert.StartsWith(
                $"{CloudTestHost.PublicAddress}api/v1/showcase-pictures/",
                row.GetProperty("groupImageUrl").GetString()!,
                StringComparison.Ordinal);
            Assert.StartsWith(
                $"{CloudTestHost.PublicAddress}api/v1/showcase-pictures/",
                row.GetProperty("groupBannerUrl").GetString()!,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ClearingAnAddressTakesTheKeptPictureWithIt()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        Guid id;
        using (var added = await AddAsync(host, new
        {
            kind = "sponsor",
            name = "Kind Person",
            link = "https://example.com",
            imageUrl = CloudTestHost.PictureAddress,
            vrChatGroupId = (string?)null,
            groupImageUrl = (string?)null,
            groupBannerUrl = (string?)null,
            sortOrder = 0,
        }))
        {
            id = (await added.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        }

        await using (var cloud = db.NewCloudContext())
        {
            Assert.Equal(1, await cloud.ShowcasePictures.CountAsync(Ct));
        }

        using (var saved = await host.SendAsync(HttpMethod.Put, $"/api/admin/showcase/{id}", new
        {
            kind = "sponsor",
            name = "Kind Person",
            link = "https://example.com",
            imageUrl = "",
            vrChatGroupId = (string?)null,
            groupImageUrl = (string?)null,
            groupBannerUrl = (string?)null,
            sortOrder = 0,
        }, bearer: CloudTestHost.RootKey))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        await using (var cloud = db.NewCloudContext())
        {
            Assert.Equal(0, await cloud.ShowcasePictures.CountAsync(Ct));
        }
    }

    [Fact]
    public async Task RemovingARowRemovesItsPictures()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        Guid id;
        using (var added = await AddAsync(host, new
        {
            kind = "sponsor",
            name = "Kind Person",
            link = "https://example.com",
            imageUrl = CloudTestHost.PictureAddress,
            vrChatGroupId = "grp_1234",
            groupImageUrl = CloudTestHost.PictureAddress,
            groupBannerUrl = CloudTestHost.BannerAddress,
            sortOrder = 0,
        }))
        {
            id = (await added.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        }

        using (var removed = await host.SendAsync(
            HttpMethod.Delete, $"/api/admin/showcase/{id}", bearer: CloudTestHost.RootKey))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        await using var check = db.NewCloudContext();
        Assert.Equal(0, await check.ShowcasePictures.CountAsync(Ct));
    }
}
