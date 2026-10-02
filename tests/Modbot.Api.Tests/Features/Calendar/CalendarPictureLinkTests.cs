using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Data.Entities;
using Modbot.Core.Files;
using Modbot.Core.Net;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// An event's pictures as links (calendar design §15.1, §15.2, added 2026-10-02): a VRChat picture
/// pasted as any VRChat link is saved as the file id inside it, text with no id in it is refused with
/// an example, and the form's picture-link fetch is behind Manage calendar and refuses what it may
/// not reach before anything is sent.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarPictureLinkTests(PostgresFixture db)
{
    private const string Id = "file_6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7";
    private const string PictureLink = "/api/calendar/picture-link";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ApiTestHost> StartAsync(FakeVRChatGate? gate = null)
    {
        await using (var context = db.NewContext())
            await context.CalendarEvents.ExecuteDeleteAsync(Ct);

        return await ApiTestHost.StartAsync(db, gate ?? new FakeVRChatGate());
    }

    private static object Body(ApiTestHost host, string? vrchatImageId, string? imageUrl = null)
    {
        var start = host.Clock.UtcNow.AddDays(2);

        return new
        {
            title = "Movie night",
            description = "Bring snacks",
            startsAt = start.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            endsAt = start.AddHours(2).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            timeZone = "UTC",
            repeat = "none",
            publishToVRChat = true,
            imageUrl,
            vrChatImageId = vrchatImageId,
            draft = true,
        };
    }

    private static async Task<string> ManagerAsync(ApiTestHost host) =>
        (await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct)).Cookie;

    // ── The VRChat picture as a link ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(Id)]
    [InlineData("https://api.vrchat.cloud/api/1/file/" + Id + "/1/file")]
    [InlineData("https://api.vrchat.cloud/api/1/file/" + Id + "/1/file?width=512")]
    [InlineData(Id + "_blob")]
    [InlineData("api.vrchat.cloud/api/1/file/" + Id + "/1/file_blob")]
    public async Task AnyVRChatLinkIsSavedAsTheFileIdInsideIt(string pasted)
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", Body(host, pasted), manager, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Id, (await ApiTestHost.BodyOf(response, Ct)).GetProperty("vrChatImageId").GetString());
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7")]
    [InlineData("https://pictures.example/" + Id + ".png")]
    public async Task TextWithNoVRChatFileIdIsRefused_WithAnExample(string pasted)
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", Body(host, pasted), manager, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString();
        Assert.Equal(VRChatFileIds.NotFound, error);
        Assert.Contains(VRChatFileIds.Example, error, StringComparison.Ordinal);
    }

    /// <summary>An id the event already holds is never refused, whatever it looks like (foundation §3.1.1).</summary>
    [Fact]
    public async Task AnIdTheEventAlreadyHoldsIsKeptAsItIs()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var created = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", Body(host, Id), manager, Ct);
        var id = (await ApiTestHost.BodyOf(created, Ct)).GetProperty("id").GetGuid();

        // As an event read back from VRChat can bring with it.
        await using (var context = db.NewContext())
        {
            var saved = await context.CalendarEvents.SingleAsync(e => e.Id == id, Ct);
            saved.VRChatImageId = "an-older-kind-of-id";
            await context.SaveChangesAsync(Ct);
        }

        var updated = await host.SendJsonAsync(
            HttpMethod.Put, $"/api/calendar/events/{id}", Body(host, "an-older-kind-of-id"), manager, Ct);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("an-older-kind-of-id", (await ApiTestHost.BodyOf(updated, Ct)).GetProperty("vrChatImageId").GetString());
    }

    [Fact]
    public async Task NoVRChatPictureStaysNone()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", Body(host, "  "), manager, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await ApiTestHost.BodyOf(response, Ct)).GetProperty("vrChatImageId").ValueKind);
    }

    // ── Fetching a picture link for the crop box ─────────────────────────────────────────

    [Fact]
    public async Task FetchingAPictureLinkNeedsManageCalendar()
    {
        await using var host = await StartAsync();
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, PictureLink, new { url = "https://pictures.example/movie.png" }, viewer, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("http://pictures.example/movie.png", PictureLinks.NotHttps)]
    [InlineData("https://pictures.example:8443/movie.png", PictureLinks.NotHttps)]
    [InlineData("https://user:secret@pictures.example/movie.png", PictureLinks.NotHttps)]
    [InlineData("", PictureLinks.NotHttps)]
    [InlineData("https://localhost/movie.png", PictureLinks.Private)]
    [InlineData("https://127.0.0.1/movie.png", PictureLinks.Private)]
    [InlineData("https://169.254.169.254/latest/meta-data/", PictureLinks.Private)]
    [InlineData("https://10.1.2.3/movie.png", PictureLinks.Private)]
    [InlineData("https://[::1]/movie.png", PictureLinks.Private)]
    public async Task ALinkThatMayNotBeFetchedIsRefused(string url, string problem)
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var response = await host.SendJsonAsync(HttpMethod.Post, PictureLink, new { url }, manager, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(problem, (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
    }

    /// <summary>
    /// A VRChat file link is fetched with Modbot's VRChat session, which the operator's switch for
    /// VRChat pictures turns off (off by default): then VRChat is not asked.
    /// </summary>
    [Fact]
    public async Task AVRChatLinkIsNotFetched_WithVRChatPicturesOff()
    {
        var gate = new FakeVRChatGate();
        await using var host = await StartAsync(gate);
        var manager = await ManagerAsync(host);

        // The database is shared with other tests, which may have turned it on.
        await using (var context = db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.VRChatImagesProxied = false;
            await context.SaveChangesAsync(Ct);
        }

        var response = await host.SendJsonAsync(
            HttpMethod.Post,
            PictureLink,
            new { url = "https://api.vrchat.cloud/api/1/file/" + Id + "/1/file" },
            manager,
            Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(gate.Calls);
    }
}
