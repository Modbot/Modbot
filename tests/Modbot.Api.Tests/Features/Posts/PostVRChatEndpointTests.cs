using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Posts;

/// <summary>
/// The Marketing tab's VRChat group posts (posts design §3.6, §4.3–§4.6): the VRChat section and
/// what it saves, VRChat's need for a title, the picture only while uploads are on, the preview
/// being what the sender sends, edit and delete in the group, and the VRChat posts switch.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PostVRChatEndpointTests(PostgresFixture db)
{
    private const string Group = "grp_marketing";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const ModbotPermissions Manager = ModbotPermissions.ViewPosts | ModbotPermissions.ManagePosts;

    /// <summary>Stands in for VRChat: records what was asked of the group and answers as told.</summary>
    private sealed class FakeVRChatPosts : IVRChatPostActions
    {
        public List<(string GroupId, string PostId, string Title, string Text, string Visibility, IReadOnlyList<string> Roles, string? ImageId)> Edits { get; } = [];

        public List<(string GroupId, string PostId)> Deletes { get; } = [];

        public PostSiteOutcome Answer { get; set; } = PostSiteOutcome.Ok;

        public Task<PostSiteOutcome> EditAsync(
            string groupId, string postId, string title, string text, string visibility, IReadOnlyList<string> roleIds, string? imageId, CancellationToken ct = default)
        {
            Edits.Add((groupId, postId, title, text, visibility, roleIds, imageId));
            return Task.FromResult(Answer);
        }

        public Task<PostSiteOutcome> DeleteAsync(string groupId, string postId, CancellationToken ct = default)
        {
            Deletes.Add((groupId, postId));
            return Task.FromResult(Answer);
        }
    }

    private async Task<(ApiTestHost Host, FakeVRChatPosts VRChat)> StartAsync(bool uploadsOn = false, string? group = Group)
    {
        await using (var context = db.NewContext())
        {
            await context.Posts.ExecuteDeleteAsync(Ct);
            var settings = await context.GetSettingsAsync(Ct);
            settings.ManagedGroupId = group;
            settings.VRChatUsername = "modbot";
            settings.VRChatPasswordEncrypted = "sealed";
            settings.VRChatPictureUploads = uploadsOn;
            settings.PostsPaused = false;
            settings.VRChatPostsOn = true;
            await context.SaveChangesAsync(Ct);
        }

        var vrchat = new FakeVRChatPosts();
        var host = await ApiTestHost.StartAsync(db, configure: s => s.AddSingleton<IVRChatPostActions>(vrchat));
        return (host, vrchat);
    }

    private static object Body(ApiTestHost host, object vrChat, string? title = "Movie night", Guid? pictureId = null) => new
    {
        title,
        text = "Friday at eight. Bring snacks.",
        pictureId,
        when = "later",
        sendAt = host.Clock.UtcNow.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        timeZone = "UTC",
        vrChat,
    };

    private static async Task<JsonElement> CreateAsync(ApiTestHost host, string cookie, object body)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/posts", body, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.BodyOf(response, Ct);
    }

    private static async Task<List<string?>> ProblemsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return [.. (await ApiTestHost.BodyOf(response, Ct)).GetProperty("problems").EnumerateArray().Select(p => p.GetString())];
    }

    private async Task<PostDestination> DestinationAsync(Guid postId)
    {
        await using var context = db.NewContext();
        return await context.PostDestinations.AsNoTracking().SingleAsync(d => d.PostId == postId, Ct);
    }

    private async Task PostedAsync(Guid postId, string externalId, VRChatPostOptions? options = null)
    {
        await using var context = db.NewContext();
        var destination = await context.PostDestinations.SingleAsync(d => d.PostId == postId, Ct);
        destination.State = PostDestinationStates.Posted;
        destination.ExternalId = externalId;
        destination.SentTitle = "Movie night";
        destination.SentText = "Friday at eight. Bring snacks.";
        if (options is not null)
            destination.Options = PostTexts.WriteVRChatOptions(options);
        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task SchedulingSavesAWaitingVRChatDestinationForTheGroup()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        var post = await CreateAsync(host, manager, Body(host, new { visibility = "group", roleIds = new[] { "grol_a", "grol_a", " " } }));

        var destination = post.GetProperty("destinations")[0];
        Assert.Equal(PostNetworks.VRChat, destination.GetProperty("network").GetString());
        Assert.Equal(PostDestinationStates.Waiting, destination.GetProperty("state").GetString());
        Assert.Equal(Group, destination.GetProperty("target").GetString());

        var vrchat = destination.GetProperty("vrChat");
        Assert.Equal("group", vrchat.GetProperty("visibility").GetString());
        Assert.Equal(["grol_a"], vrchat.GetProperty("roleIds").EnumerateArray().Select(r => r.GetString()));
        Assert.False(vrchat.GetProperty("notify").GetBoolean());
    }

    [Fact]
    public async Task VRChatNeedsATitle()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        var none = await host.SendJsonAsync(HttpMethod.Post, "/api/posts", Body(host, new { }, title: null), manager, Ct);
        Assert.Contains("VRChat needs a title.", await ProblemsAsync(none));

        // Its own title is enough.
        var own = await CreateAsync(host, manager, Body(host, new { title = "Cinema" }, title: null));
        Assert.Equal("Cinema", own.GetProperty("destinations")[0].GetProperty("titleOverride").GetString());
    }

    [Fact]
    public async Task WithNoGroupVRChatCannotBeScheduled()
    {
        var (host, _) = await StartAsync(group: null);
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/posts", Body(host, new { }), manager, Ct);

        Assert.Contains("No VRChat group is set up yet.", await ProblemsAsync(response));
    }

    [Fact]
    public async Task WhoSeesItIsGroupOrEveryone_AndEveryoneHasNoRoles()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        var wrong = await host.SendJsonAsync(HttpMethod.Post, "/api/posts", Body(host, new { visibility = "friends" }), manager, Ct);
        Assert.Contains("Who sees it must be Group or Everyone.", await ProblemsAsync(wrong));

        var everyone = await CreateAsync(host, manager, Body(host, new { visibility = "public", roleIds = new[] { "grol_a" } }));
        var vrchat = everyone.GetProperty("destinations")[0].GetProperty("vrChat");
        Assert.Equal("public", vrchat.GetProperty("visibility").GetString());
        Assert.Empty(vrchat.GetProperty("roleIds").EnumerateArray());
    }

    /// <summary>The preview is built by the code the sender uses: what it shows is what goes out.</summary>
    [Fact]
    public async Task ThePreviewIsWhatTheSenderSends()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var body = Body(host, new { title = "Cinema", text = "Popcorn provided." });

        var preview = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Post, "/api/posts/preview", body, manager, Ct), Ct);
        var created = await CreateAsync(host, manager, body);

        await using var context = db.NewContext();
        var post = await context.Posts.AsNoTracking().Include(p => p.Destinations)
            .SingleAsync(p => p.Id == created.GetProperty("id").GetGuid(), Ct);

        var vrchat = preview.GetProperty("vrChat");
        Assert.Equal(PostTexts.TitleFor(post, post.Destinations[0]), vrchat.GetProperty("title").GetString());
        Assert.Equal(PostTexts.TextFor(post, post.Destinations[0]), vrchat.GetProperty("text").GetString());
        Assert.Equal("Cinema", vrchat.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, vrchat.GetProperty("pictureUrl").ValueKind);
        Assert.Empty(preview.GetProperty("problems").EnumerateArray());
    }

    [Fact]
    public async Task ThePicturesVRChatIdIsKeptOnlyWhileUploadsAreOn()
    {
        var picture = Guid.CreateVersion7();

        await using (var context = db.NewContext())
        {
            context.CalendarCoverPictures.Add(new CalendarCoverPicture
            {
                Id = picture,
                Bytes = [0x89, 0x50, 0x4E, 0x47],
                ContentType = "image/png",
                CreatedAt = DateTimeOffset.UnixEpoch,
            });
            await context.SaveChangesAsync(Ct);
        }

        var (host, _) = await StartAsync(uploadsOn: false);
        await using (host)
        {
            var (_, manager) = await host.SignedInAsync(Manager, Ct);
            var off = await CreateAsync(host, manager, Body(host, new { imageId = "file_1" }, pictureId: picture));
            Assert.Equal(JsonValueKind.Null, off.GetProperty("destinations")[0].GetProperty("vrChat").GetProperty("imageId").ValueKind);

            // Off, VRChat picture uploads send nothing to VRChat at all.
            var upload = await host.SendJsonAsync(HttpMethod.Post, "/api/posts/vrchat-picture", new { pictureId = picture }, manager, Ct);
            Assert.Equal(HttpStatusCode.Conflict, upload.StatusCode);
        }

        var (onHost, _) = await StartAsync(uploadsOn: true);
        await using (onHost)
        {
            var (_, manager) = await onHost.SignedInAsync(Manager, Ct);
            var on = await CreateAsync(onHost, manager, Body(onHost, new { imageId = "file_1" }, pictureId: picture));
            var vrchat = on.GetProperty("destinations")[0].GetProperty("vrChat");
            Assert.Equal("file_1", vrchat.GetProperty("imageId").GetString());
            Assert.Equal(picture, vrchat.GetProperty("pictureId").GetGuid());

            // A post with no picture sends VRChat no picture id, whatever is said.
            var none = await CreateAsync(onHost, manager, Body(onHost, new { imageId = "file_1" }));
            Assert.Equal(JsonValueKind.Null, none.GetProperty("destinations")[0].GetProperty("vrChat").GetProperty("imageId").ValueKind);
        }
    }

    [Fact]
    public async Task AnEditInTheGroupSendsThePictureAndAudienceAgain_AndWritesAFact()
    {
        var (host, vrchat) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var id = (await CreateAsync(host, manager, Body(host, new { visibility = "group", roleIds = new[] { "grol_a" } }))).GetProperty("id").GetGuid();

        await PostedAsync(id, "not_1", new VRChatPostOptions(VRChatPostVisibilities.Group, ["grol_a"], Notify: true, ImageId: "file_1"));

        var destination = await DestinationAsync(id);
        var response = await host.SendJsonAsync(
            HttpMethod.Post, $"/api/posts/{id}/destinations/{destination.Id}/edit", new { title = "Cinema", text = "Now at nine." }, manager, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var edit = Assert.Single(vrchat.Edits);
        Assert.Equal((Group, "not_1", "Cinema", "Now at nine.", "group", "file_1"), (edit.GroupId, edit.PostId, edit.Title, edit.Text, edit.Visibility, edit.ImageId));
        Assert.Equal(["grol_a"], edit.Roles);

        var after = await DestinationAsync(id);
        Assert.Equal("Now at nine.", after.SentText);
        Assert.Equal("Cinema", after.SentTitle);

        var fact = ApiTestHost.DataOf(Assert.Single(await host.FactsAsync(FactType.PostEdited, id.ToString(), Ct)));
        Assert.Equal(PostNetworks.VRChat, fact.GetProperty("network").GetString());
        Assert.Equal("Friday at eight. Bring snacks.", fact.GetProperty("before").GetString());
    }

    [Fact]
    public async Task AnEditInTheGroupNeedsATitle()
    {
        var (host, vrchat) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var id = (await CreateAsync(host, manager, Body(host, new { }))).GetProperty("id").GetGuid();
        await PostedAsync(id, "not_1");

        var destination = await DestinationAsync(id);
        var response = await host.SendJsonAsync(
            HttpMethod.Post, $"/api/posts/{id}/destinations/{destination.Id}/edit", new { title = " ", text = "Now at nine." }, manager, Ct);

        Assert.Contains("VRChat needs a title.", await ProblemsAsync(response));
        Assert.Empty(vrchat.Edits);
    }

    [Fact]
    public async Task DeletingInTheGroupMarksItDeleted_AndWritesAFact()
    {
        var (host, vrchat) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var id = (await CreateAsync(host, manager, Body(host, new { }))).GetProperty("id").GetGuid();
        await PostedAsync(id, "not_1");

        var destination = await DestinationAsync(id);
        var response = await host.SendJsonAsync(HttpMethod.Delete, $"/api/posts/{id}/destinations/{destination.Id}", null, manager, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((Group, "not_1"), Assert.Single(vrchat.Deletes));
        Assert.Equal(PostDestinationStates.Removed, (await DestinationAsync(id)).State);
        Assert.Single(await host.FactsAsync(FactType.PostRemoved, id.ToString(), Ct));
    }

    [Fact]
    public async Task TheVRChatPostsSwitchIsRecorded_AndHoldsVRChat()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var (_, settings) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Put, "/api/settings/posts", new { vrChat = false }, settings, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await ApiTestHost.BodyOf(response, Ct)).GetProperty("vrChat").GetBoolean());

        var fact = ApiTestHost.DataOf((await host.FactsAsync(FactType.SettingsChanged, "settings", Ct))[0]);
        Assert.False(fact.GetProperty("changed").GetProperty("vrchat").GetProperty("new").GetBoolean());

        var post = await CreateAsync(host, manager, Body(host, new { }));
        Assert.Equal(PostHolds.Off, post.GetProperty("destinations")[0].GetProperty("shown").GetString());
    }
}
