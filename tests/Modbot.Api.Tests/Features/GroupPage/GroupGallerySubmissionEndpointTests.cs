using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.GroupPage;
using Modbot.Api.Tests.Fakes;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.TestSupport;
using Modbot.VRChat;
using Modbot.VRChat.Files;
using Modbot.VRChat.Sync;
using Newtonsoft.Json;
using VRChat.API.Model;
using VRChatFileModel = VRChat.API.Model.File;

namespace Modbot.Api.Tests.Features.GroupPage;

/// <summary>
/// The pictures members posted in Discord for the group gallery: the queue on the Gallery page, the
/// picture itself, adding one to a gallery on VRChat (an upload, then a gallery entry, each once)
/// and declining one. Adding needs Manage group gallery, picture uploads switched on and an account
/// not known to lack VRChat+; a refused gallery call keeps the upload so pressing again does not
/// upload twice.
/// </summary>
/// <remarks>The gate is the scripted fake, so nothing here reaches VRChat or Discord.</remarks>
[Collection(nameof(PostgresCollection))]
public class GroupGallerySubmissionEndpointTests
{
    private const string GroupId = "grp_1";
    private const string Path = "/api/group/gallery/submissions";

    private static readonly DateTimeOffset Day = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static readonly byte[] PngStart = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly PostgresFixture _db;

    public GroupGallerySubmissionEndpointTests(PostgresFixture db) => _db = db;

    private sealed class RecordingReactions : IDiscordMessageReactions
    {
        public List<(string ChannelId, string MessageId, string Emoji)> Added { get; } = [];

        public Task<bool> AddAsync(string channelId, string messageId, string emoji, CancellationToken ct = default)
        {
            Added.Add((channelId, messageId, emoji));
            return Task.FromResult(true);
        }
    }

    private static byte[] Png(int length = 64)
    {
        var bytes = new byte[length];
        PngStart.CopyTo(bytes, 0);
        return bytes;
    }

    private static VRChatFileModel Uploaded(string id) =>
        JsonConvert.DeserializeObject<VRChatFileModel>(
            $$"""{"id":"{{id}}","extension":".png","mimeType":"image/png","name":"picture","ownerId":"usr_modbot","tags":["gallery"],"versions":[]}""")!;

    private static GroupGalleryImage Entry(string id) =>
        JsonConvert.DeserializeObject<GroupGalleryImage>(
            $$"""{"id":"{{id}}","galleryId":"ggal_1","imageUrl":"https://api.vrchat.cloud/x","approved":true}""")!;

    private sealed record Scene(ReadSurfaceTestHost Host, FakeVRChatGate Gate, RecordingReactions Reactions);

    private async Task<Scene> StartAsync(
        CancellationToken ct, bool uploadsOn = true, bool? hasPlus = null, bool uploadsRegistered = true)
    {
        var gate = new FakeVRChatGate()
            .Returns("UploadImage", Uploaded("file_new"))
            .Returns("AddGroupGalleryImage", Entry("ggim_new"));

        var reactions = new RecordingReactions();

        var host = await ReadSurfaceTestHost.StartAsync(_db, gate, configure: services =>
        {
            if (uploadsRegistered)
                services.AddSingleton<VRChatPictureUploads>();

            services.AddSingleton<IDiscordMessageReactions>(reactions);
        });

        await host.ResetAsync(ct);
        host.Clock.UtcNow = Day;

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        await db.GallerySubmissions.ExecuteDeleteAsync(ct);

        var settings = await db.GetSettingsAsync(ct);
        settings.ManagedGroupId = GroupId;
        settings.ManagedGroupGalleries = GroupGallerySnapshot.ToJson(
            [new GroupGallerySnapshot("ggal_1", "Photos", null, false), new GroupGallerySnapshot("ggal_2", "Art", null, true)]);
        settings.VRChatPictureUploads = uploadsOn;
        settings.VRChatHasPlus = hasPlus;

        await db.SaveChangesAsync(ct);
        return new Scene(host, gate, reactions);
    }

    private static async Task<Guid> SeedAsync(
        ReadSurfaceTestHost host, CancellationToken ct, string messageId = "msg_1", string? fileId = null)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var row = new GallerySubmission
        {
            Id = Guid.NewGuid(),
            DiscordMessageId = messageId,
            DiscordChannelId = "chan_1",
            MessageUrl = "https://discord.com/channels/1/chan_1/" + messageId,
            AuthorDiscordId = "42",
            AuthorName = "Rowan",
            FileName = "sunset.png",
            ContentType = "image/png",
            Bytes = Png(),
            SubmittedAt = Day.AddHours(-1),
            VRChatFileId = fileId,
        };

        db.GallerySubmissions.Add(row);
        await db.SaveChangesAsync(ct);
        return row.Id;
    }

    private static async Task<GallerySubmission> RowAsync(ReadSurfaceTestHost host, Guid id, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        return await db.GallerySubmissions.AsNoTracking().SingleAsync(s => s.Id == id, ct);
    }

    private static async Task<List<ModbotEvent>> FactsAsync(ReadSurfaceTestHost host, string type, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        return await db.Events.AsNoTracking().Where(e => e.Type == type).OrderBy(e => e.Id).ToListAsync(ct);
    }

    private static Task<HttpResponseMessage> AddAsync(
        ReadSurfaceTestHost host, Guid id, string cookie, CancellationToken ct, string galleryId = "ggal_1")
        => host.PostJsonAsync($"{Path}/{id}/add", new { galleryId }, cookie, ct);

    // ── The queue ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheQueueListsWhatIsWaiting_OldestFirst_AndAsksNobody()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, gate, _) = await StartAsync(ct);
        await using var disposeHost = host;

        var first = await SeedAsync(host, ct, "msg_1");
        var second = await SeedAsync(host, ct, "msg_2");

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            (await db.GallerySubmissions.SingleAsync(s => s.Id == first, ct)).SubmittedAt = Day.AddHours(-3);
            (await db.GallerySubmissions.SingleAsync(s => s.Id == second, ct)).Status = GallerySubmissionStatus.Declined;
            await db.SaveChangesAsync(ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var list = await host.GetJsonAsync<GallerySubmissionList>(Path, cookie, ct);

        var row = Assert.Single(list.Submissions);
        Assert.Equal(first, row.Id);
        Assert.Equal("Rowan", row.AuthorName);
        Assert.Equal($"{Path}/{first}/picture", row.PictureUrl);
        Assert.Empty(gate.Calls);
    }

    [Fact]
    public async Task TheQueueNeedsToSeeAnalytics()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, _) = await StartAsync(ct);
        await using var disposeHost = host;

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageCalendar, ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(Path, cookie, ct)).StatusCode);
    }

    [Fact]
    public async Task APictureIsServedAsItsOwnType_WhileWaiting()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, _) = await StartAsync(ct);
        await using var disposeHost = host;

        var id = await SeedAsync(host, ct);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);

        var response = await host.GetAsync($"{Path}/{id}/picture", cookie, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png(), await response.Content.ReadAsByteArrayAsync(ct));
    }

    [Fact]
    public async Task ADecidedPicturesCopyIsGone()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, _) = await StartAsync(ct);
        await using var disposeHost = host;

        var id = await SeedAsync(host, ct);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageGroupGallery, ct);

        Assert.Equal(HttpStatusCode.NoContent, (await host.PostJsonAsync($"{Path}/{id}/decline", null, cookie, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync($"{Path}/{id}/picture", cookie, ct)).StatusCode);
    }

    // ── Adding ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddingNeedsManageGroupGallery()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, gate, _) = await StartAsync(ct);
        await using var disposeHost = host;

        var id = await SeedAsync(host, ct);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await AddAsync(host, id, cookie, ct)).StatusCode);
        Assert.Empty(gate.Calls);
    }

    [Fact]
    public async Task WithPictureUploadsOff_NothingIsAskedOfVRChat()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, gate, _) = await StartAsync(ct, uploadsOn: false);
        await using var disposeHost = host;

        var id = await SeedAsync(host, ct);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupGallery, ct);

        Assert.Equal(HttpStatusCode.Conflict, (await AddAsync(host, id, cookie, ct)).StatusCode);
        Assert.Empty(gate.Calls);
        Assert.Equal(GallerySubmissionStatus.Waiting, (await RowAsync(host, id, ct)).Status);
    }

    [Fact]
    public async Task AnAccountKnownToLackVRChatPlus_IsNotAsked()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, gate, _) = await StartAsync(ct, hasPlus: false);
        await using var disposeHost = host;

        var id = await SeedAsync(host, ct);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupGallery, ct);

        Assert.Equal(HttpStatusCode.Conflict, (await AddAsync(host, id, cookie, ct)).StatusCode);
        Assert.Empty(gate.Calls);
    }

    [Fact]
    public async Task AddingIsAnUploadThenAGalleryEntry_AFactAndATick_AndTheCopyIsDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, gate, reactions) = await StartAsync(ct);
        await using var disposeHost = host;

        var id = await SeedAsync(host, ct);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupGallery, ct);

        var response = await AddAsync(host, id, cookie, ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(
            [
                (VRChatEndpointClass.FilesUpload, "UploadImage"),
                (VRChatEndpointClass.GroupsGalleryWrite, "AddGroupGalleryImage"),
            ],
            gate.Calls.Select(c => (c.Endpoint.Class, c.Endpoint.Operation!)).ToList());
        Assert.All(gate.Calls, c => Assert.Equal(VRChatCallPriority.Interactive, c.Priority));

        var row = await RowAsync(host, id, ct);
        Assert.Equal(GallerySubmissionStatus.Added, row.Status);
        Assert.Equal("ggal_1", row.GalleryId);
        Assert.Equal("ggim_new", row.VRChatImageId);
        Assert.Empty(row.Bytes);
        Assert.Equal(Day, row.DecidedAt);

        var fact = Assert.Single(await FactsAsync(host, FactType.GroupGalleryImageAdded, ct));
        Assert.Equal(GroupId, fact.SubjectId);
        var data = JsonDocument.Parse(fact.Data).RootElement;
        Assert.Equal("Photos", data.GetProperty("galleryName").GetString());
        Assert.Equal("Rowan", data.GetProperty("submittedByName").GetString());

        var tick = Assert.Single(reactions.Added);
        Assert.Equal(("chan_1", "msg_1", GroupGallerySubmissionEndpoints.AddedReaction), tick);
    }

    [Fact]
    public async Task AGalleryRefusalKeepsTheUpload_AndTheSecondPressUploadsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, gate, reactions) = await StartAsync(ct);
        await using var disposeHost = host;

        gate.Returns("AddGroupGalleryImage", VRChatResult<GroupGalleryImage>.Failure(
            403,
            "Forbidden",
            rawResponse: "Error calling AddGroupGalleryImage: {\"error\":{\"message\":\"You do not have permission to do that.\",\"status_code\":403}}"));

        var id = await SeedAsync(host, ct);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupGallery, ct);

        var first = await AddAsync(host, id, cookie, ct);

        Assert.False(first.IsSuccessStatusCode);

        var kept = await RowAsync(host, id, ct);
        Assert.Equal(GallerySubmissionStatus.Waiting, kept.Status);
        Assert.Equal("file_new", kept.VRChatFileId);
        Assert.NotEmpty(kept.Bytes);
        Assert.Empty(await FactsAsync(host, FactType.GroupGalleryImageAdded, ct));
        Assert.Empty(reactions.Added);

        gate.Returns("AddGroupGalleryImage", Entry("ggim_new"));
        gate.Calls.Clear();

        var second = await AddAsync(host, id, cookie, ct);

        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        var call = Assert.Single(gate.Calls);
        Assert.Equal("AddGroupGalleryImage", call.Endpoint.Operation);
        Assert.Equal(GallerySubmissionStatus.Added, (await RowAsync(host, id, ct)).Status);
    }

    [Fact]
    public async Task APictureThatIsAlreadyAddedIsNotAddedTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, gate, _) = await StartAsync(ct);
        await using var disposeHost = host;

        var id = await SeedAsync(host, ct);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupGallery, ct);

        Assert.Equal(HttpStatusCode.NoContent, (await AddAsync(host, id, cookie, ct)).StatusCode);
        gate.Calls.Clear();

        Assert.Equal(HttpStatusCode.Conflict, (await AddAsync(host, id, cookie, ct)).StatusCode);
        Assert.Empty(gate.Calls);
    }

    // ── Declining ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DecliningAsksVRChatNothing_AndDeletesTheCopy()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, gate, reactions) = await StartAsync(ct);
        await using var disposeHost = host;

        var id = await SeedAsync(host, ct);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupGallery, ct);

        var response = await host.PostJsonAsync($"{Path}/{id}/decline", null, cookie, ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(gate.Calls);
        Assert.Empty(reactions.Added);

        var row = await RowAsync(host, id, ct);
        Assert.Equal(GallerySubmissionStatus.Declined, row.Status);
        Assert.Empty(row.Bytes);
        Assert.NotNull(row.DecidedByUserId);
    }

    [Fact]
    public async Task DecliningNeedsManageGroupGallery()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _, _) = await StartAsync(ct);
        await using var disposeHost = host;

        var id = await SeedAsync(host, ct);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostJsonAsync($"{Path}/{id}/decline", null, cookie, ct)).StatusCode);
        Assert.Equal(GallerySubmissionStatus.Waiting, (await RowAsync(host, id, ct)).Status);
    }

    // ── VRChat's own waiting pictures ──────────────────────────────────────────────────────

    /// <remarks>
    /// The fake gate never runs the call, so it cannot show that <c>approved=false</c> went out;
    /// that is pinned by <c>GroupGalleries.ListAsync</c> passing the flag through. This checks the
    /// switch is accepted and is still one read of the gallery.
    /// </remarks>
    [Fact]
    public async Task TheWaitingFilterIsStillOneRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, gate, _) = await StartAsync(ct);
        await using var disposeHost = host;

        gate.Returns("GetGroupGalleryImages", VRChatResult<object>.Ok(
            null, 200, """[{"id":"ggim_1","galleryId":"ggal_1","imageUrl":"https://api.vrchat.cloud/x","approved":false}]"""));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<GroupGalleryPage>("/api/group/gallery?waiting=true", cookie, ct);

        Assert.False(Assert.Single(page.Images).Approved);

        var call = Assert.Single(gate.Calls);
        Assert.Equal(VRChatEndpointClass.GroupsGallery, call.Endpoint.Class);
    }
}
