using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat;
using Modbot.VRChat.Files;
using Newtonsoft.Json;
using VRChatFileModel = VRChat.API.Model.File;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// The VRChat picture of a calendar event (calendar design §2, added 2026-10-01): uploaded to VRChat
/// once when it is chosen, never sent again after a 429, refused before VRChat is asked when it is
/// too big or not a PNG or JPEG, behind Manage calendar, and a fact naming who uploaded it.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarPictureTests(PostgresFixture db)
{
    private const string Path = "/api/calendar/vrchat-picture";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] PngStart = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The first bytes of a PNG and some more: all the server looks at is the start.</summary>
    private static byte[] Png(int length = 64)
    {
        var bytes = new byte[length];
        PngStart.CopyTo(bytes, 0);
        return bytes;
    }

    private static VRChatFileModel Uploaded(string id) =>
        JsonConvert.DeserializeObject<VRChatFileModel>(
            $$"""{"id":"{{id}}","extension":".png","mimeType":"image/png","name":"picture","ownerId":"usr_modbot","tags":["gallery"],"versions":[]}""")!;

    private async Task<ApiTestHost> StartAsync(FakeVRChatGate gate, bool uploads = true, bool switchOn = true)
    {
        await using (var context = db.NewContext())
            await context.CalendarEvents.ExecuteDeleteAsync(Ct);

        // Off until an operator turns it on, so every test that uploads turns it on first.
        await SetUploadsAsync(switchOn);

        return await ApiTestHost.StartAsync(
            db, gate, configure: uploads ? s => s.AddSingleton<VRChatPictureUploads>() : null);
    }

    private static Task<HttpResponseMessage> UploadAsync(
        ApiTestHost host, string cookie, byte[] bytes, string type = "image/png", Guid? eventId = null)
    {
        var request = host.Authenticated(
            HttpMethod.Post, eventId is { } id ? $"{Path}?eventId={id}" : Path, cookie);

        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(type);

        return host.Client.SendAsync(request, Ct);
    }

    private static async Task<Guid> CreateEventAsync(ApiTestHost host, string cookie)
    {
        var start = host.Clock.UtcNow.AddDays(2);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", new
        {
            title = "Movie night",
            description = "Bring snacks",
            startsAt = start.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            endsAt = start.AddHours(2).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            timeZone = "UTC",
            repeat = "none",
            publishToVRChat = true,
            draft = true,
        }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task UploadingNeedsManageCalendar()
    {
        var gate = new FakeVRChatGate().Returns("UploadImage", Uploaded("file_test"));
        await using var host = await StartAsync(gate);
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);

        var response = await UploadAsync(host, viewer, Png());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(gate.Calls);
    }

    [Fact]
    public async Task APictureIsOneUpload_AndAnswersVRChatsFileId()
    {
        var gate = new FakeVRChatGate().Returns("UploadImage", Uploaded("file_test"));
        await using var host = await StartAsync(gate);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var response = await UploadAsync(host, manager, Png());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("file_test", (await ApiTestHost.BodyOf(response, Ct)).GetProperty("fileId").GetString());

        var call = Assert.Single(gate.Calls);
        Assert.Equal(VRChatEndpointClass.FilesUpload, call.Endpoint.Class);
        Assert.Equal(VRChatCallPriority.Interactive, call.Priority);
    }

    [Fact]
    public async Task AnUploadForANewEventIsAFactAboutTheFile()
    {
        var gate = new FakeVRChatGate().Returns("UploadImage", Uploaded("file_new"));
        await using var host = await StartAsync(gate);
        var (user, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(host, manager, Png())).StatusCode);

        var fact = Assert.Single(await host.FactsAsync(FactType.PlannedEventPictureUploaded, "file_new", Ct));
        Assert.Equal(user.Id.ToString(), fact.ActorId);

        var data = ApiTestHost.DataOf(fact);
        Assert.Equal("file_new", data.GetProperty("fileId").GetString());
        Assert.Equal("image/png", data.GetProperty("type").GetString());
    }

    [Fact]
    public async Task AnUploadForASavedEventIsAFactAboutTheEvent()
    {
        var gate = new FakeVRChatGate().Returns("UploadImage", Uploaded("file_saved"));
        await using var host = await StartAsync(gate);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var id = await CreateEventAsync(host, manager);

        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(host, manager, Png(), eventId: id)).StatusCode);

        var fact = Assert.Single(await host.FactsAsync(FactType.PlannedEventPictureUploaded, id.ToString(), Ct));
        var data = ApiTestHost.DataOf(fact);
        Assert.Equal("file_saved", data.GetProperty("fileId").GetString());
        Assert.Equal("Movie night", data.GetProperty("title").GetString());
    }

    /// <summary>Foundation §4.3.1: a 429 is a cold stop, and the upload is not sent again.</summary>
    [Fact]
    public async Task A429IsNotSentAgain_AndSaysSoPlainly()
    {
        var gate = new FakeVRChatGate().Returns(
            "UploadImage",
            VRChatResult<VRChatFileModel>.Failure(429, "Too many requests", kind: VRChatFailureKind.RateLimited));
        await using var host = await StartAsync(gate);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var response = await UploadAsync(host, manager, Png());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(
            "VRChat is not taking uploads right now. Try again in a few minutes.",
            (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());

        Assert.Single(gate.Calls);
        Assert.Empty(await host.FactsAsync(FactType.PlannedEventPictureUploaded, "file_test", Ct));
    }

    [Fact]
    public async Task WithUploadsTurnedOff_ThePictureIsRefusedAndVRChatIsNotAsked()
    {
        var gate = new FakeVRChatGate().Returns("UploadImage", Uploaded("file_test"));
        await using var host = await StartAsync(gate, switchOn: false);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var response = await UploadAsync(host, manager, Png());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Picture uploads are off.", (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
        Assert.Empty(gate.Calls);

        var view = await host.SendJsonAsync(HttpMethod.Get, "/api/calendar", null, manager, Ct);
        Assert.False((await ApiTestHost.BodyOf(view, Ct)).GetProperty("pictureUploads").GetBoolean());
    }

    [Fact]
    public async Task UploadsAreOffUntilTheOperatorTurnsThemOn()
    {
        Assert.False(new Modbot.Core.Data.Entities.Settings().VRChatPictureUploads);

        var gate = new FakeVRChatGate().Returns("UploadImage", Uploaded("file_test"));
        await using var host = await StartAsync(gate);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var view = await host.SendJsonAsync(HttpMethod.Get, "/api/calendar", null, manager, Ct);

        Assert.True((await ApiTestHost.BodyOf(view, Ct)).GetProperty("pictureUploads").GetBoolean());
    }

    private async Task SetUploadsAsync(bool on)
    {
        await using var context = db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        settings.VRChatPictureUploads = on;
        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task APictureTooBigIsRefusedBeforeVRChatIsAsked()
    {
        var gate = new FakeVRChatGate().Returns("UploadImage", Uploaded("file_test"));
        await using var host = await StartAsync(gate);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var response = await UploadAsync(host, manager, Png((int)VRChatPictureUploads.MaxBytes + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(gate.Calls);
    }

    [Theory]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "image/png")] // a GIF claiming to be a PNG
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67, 0x3E }, "image/svg+xml")] // <svg>
    [InlineData(new byte[0], "image/png")]
    public async Task AnythingButAPngOrJpegIsRefusedBeforeVRChatIsAsked(byte[] bytes, string type)
    {
        var gate = new FakeVRChatGate().Returns("UploadImage", Uploaded("file_test"));
        await using var host = await StartAsync(gate);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var response = await UploadAsync(host, manager, bytes, type);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(gate.Calls);
    }

    [Fact]
    public async Task AnEventThatIsNotThereIsRefusedBeforeVRChatIsAsked()
    {
        var gate = new FakeVRChatGate().Returns("UploadImage", Uploaded("file_test"));
        await using var host = await StartAsync(gate);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var response = await UploadAsync(host, manager, Png(), eventId: Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(gate.Calls);
    }

    [Fact]
    public async Task WithoutVRChatNothingCanBeUploaded()
    {
        var gate = new FakeVRChatGate();
        await using var host = await StartAsync(gate, uploads: false);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var response = await UploadAsync(host, manager, Png());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(gate.Calls);
    }

    /// <summary>The id the upload answered with is what the event saves, and what VRChat is sent.</summary>
    [Fact]
    public async Task TheUploadedIdIsSavedOnTheEvent()
    {
        var gate = new FakeVRChatGate().Returns("UploadImage", Uploaded("file_kept"));
        await using var host = await StartAsync(gate);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var id = await CreateEventAsync(host, manager);

        var fileId = (await ApiTestHost.BodyOf(await UploadAsync(host, manager, Png(), eventId: id), Ct))
            .GetProperty("fileId").GetString();

        var start = host.Clock.UtcNow.AddDays(2);
        var saved = await host.SendJsonAsync(HttpMethod.Put, $"/api/calendar/events/{id}", new
        {
            title = "Movie night",
            description = "Bring snacks",
            startsAt = start.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            endsAt = start.AddHours(2).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            timeZone = "UTC",
            repeat = "none",
            vrChatImageId = fileId,
            publishToVRChat = true,
            draft = true,
        }, manager, Ct);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("file_kept", (await ApiTestHost.BodyOf(saved, Ct)).GetProperty("vrChatImageId").GetString());
    }
}
