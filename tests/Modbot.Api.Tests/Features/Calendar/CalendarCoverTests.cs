using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Calendar;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Core.Net;
using Modbot.Core.Posts;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// The picture cropped for Discord (calendar design §15.4, added 2026-10-02): kept by Modbot behind
/// Manage calendar or Manage templates, a picture by its bytes and at most 8 MB, saved on the event by its id, and
/// deleted once no live event uses it.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarCoverTests(PostgresFixture db)
{
    private const string Path = "/api/calendar/cover";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Png(int length = 64)
    {
        var bytes = new byte[length];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    private async Task<ApiTestHost> StartAsync()
    {
        await using (var context = db.NewContext())
            await context.CalendarEvents.IgnoreAutoIncludes().ExecuteDeleteAsync(Ct);

        return await ApiTestHost.StartAsync(db);
    }

    private static async Task<string> ManagerAsync(ApiTestHost host) =>
        (await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct)).Cookie;

    private static Task<HttpResponseMessage> UploadAsync(ApiTestHost host, string cookie, byte[] bytes)
    {
        var request = host.Authenticated(HttpMethod.Post, Path, cookie);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return host.Client.SendAsync(request, Ct);
    }

    private static async Task<Guid> UploadedAsync(ApiTestHost host, string cookie)
    {
        var response = await UploadAsync(host, cookie, Png());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("coverId").GetGuid();
    }

    private static object Body(ApiTestHost host, Guid? cover)
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
            publishToDiscord = true,
            coverPictureId = cover,
            draft = true,
        };
    }

    private static async Task<Guid> CreateAsync(ApiTestHost host, string cookie, Guid? cover)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", Body(host, cover), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("id").GetGuid();
    }

    private async Task<bool> KeptAsync(Guid cover)
    {
        await using var context = db.NewContext();
        return await context.CalendarCoverPictures.AnyAsync(c => c.Id == cover, Ct);
    }

    [Fact]
    public async Task KeepingAPictureNeedsManageCalendarOrManageTemplates()
    {
        await using var host = await StartAsync();
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(host, viewer, Png())).StatusCode);
    }

    /// <summary>The template editor keeps a cover too, for someone who may change templates and not events.</summary>
    [Fact]
    public async Task ATemplateManagerWhoCanSeeTheCalendarMayKeepAPicture()
    {
        await using var host = await StartAsync();
        var (_, templateManager) = await host.SignedInAsync(
            ModbotPermissions.ViewCalendar | ModbotPermissions.ManageTemplates, Ct);

        Assert.True(await KeptAsync(await UploadedAsync(host, templateManager)));
    }

    [Fact]
    public async Task ManageTemplatesWithoutSeeCalendarMayNotKeepAPicture()
    {
        await using var host = await StartAsync();
        var (_, templateManager) = await host.SignedInAsync(ModbotPermissions.ManageTemplates, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(host, templateManager, Png())).StatusCode);
    }

    [Fact]
    public async Task APictureIsKept_AndReadBackWithItsType()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var cover = await UploadedAsync(host, manager);

        var read = await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, $"/api/calendar/covers/{cover}", manager), Ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("image/png", read.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png(), await read.Content.ReadAsByteArrayAsync(Ct));
        Assert.Contains("nosniff", read.Headers.GetValues("X-Content-Type-Options"));
    }

    private static Task<HttpResponseMessage> UploadBlueskyCopyAsync(ApiTestHost host, string cookie, Guid cover, byte[] bytes)
    {
        var request = host.Authenticated(HttpMethod.Put, $"{Path}/{cover}/bluesky", cookie);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return host.Client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task ABlueskyCopyIsKeptInTheCoversOwnRow_ReplacedByTheNext_AndOnlyAsAJpegUnderOneMegabyte()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var cover = await UploadedAsync(host, manager);

        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
        Assert.Equal(HttpStatusCode.NoContent, (await UploadBlueskyCopyAsync(host, manager, cover, jpeg)).StatusCode);

        await using (var context = db.NewContext())
        {
            var row = await context.CalendarCoverPictures.AsNoTracking().SingleAsync(c => c.Id == cover, Ct);
            Assert.Equal(jpeg, row.BlueskyBytes);
            Assert.Equal(Png(), row.Bytes);
        }

        // Not a JPEG, over 1,000,000 bytes, or no such cover.
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadBlueskyCopyAsync(host, manager, cover, Png())).StatusCode);

        var big = new byte[1_000_001];
        jpeg.CopyTo(big, 0);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await UploadBlueskyCopyAsync(host, manager, cover, big)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadBlueskyCopyAsync(host, manager, Guid.NewGuid(), jpeg)).StatusCode);

        var viewer = (await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct)).Cookie;
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadBlueskyCopyAsync(host, viewer, cover, jpeg)).StatusCode);
    }

    private static async Task<Guid> UploadedJpegAsync(ApiTestHost host, string cookie, int length)
    {
        var bytes = new byte[length];
        new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }.CopyTo(bytes, 0);

        var request = host.Authenticated(HttpMethod.Post, Path, cookie);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var response = await host.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("coverId").GetGuid();
    }

    private static async Task<(HttpStatusCode Status, List<Guid> Ids)> NeedingCopyAsync(
        ApiTestHost host, string cookie, params Guid[] skip)
    {
        var query = string.Join("&", skip.Select(id => $"skip={id}"));
        var response = await host.Client.SendAsync(
            host.Authenticated(
                HttpMethod.Get,
                "/api/calendar/covers/needing-bluesky-copy" + (query.Length > 0 ? "?" + query : ""),
                cookie),
            Ct);

        if (response.StatusCode != HttpStatusCode.OK)
            return (response.StatusCode, []);

        var ids = (await ApiTestHost.BodyOf(response, Ct)).GetProperty("coverIds")
            .EnumerateArray().Select(e => e.GetGuid()).ToList();
        return (response.StatusCode, ids);
    }

    [Fact]
    public async Task OnlyCoversAnEventUsesThatHaveNoCopyAndNeedOneAreListed()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var bigPng = await UploadedAsync(host, manager);
        await using (var context = db.NewContext())
            await context.CalendarCoverPictures.Where(c => c.Id == bigPng)
                .ExecuteUpdateAsync(c => c.SetProperty(x => x.Bytes, Png(1_000_001)), Ct);

        var smallPng = await UploadedAsync(host, manager);
        var bigJpeg = await UploadedJpegAsync(host, manager, 1_000_001);
        var smallJpeg = await UploadedJpegAsync(host, manager, 500_000);
        var hasCopy = await UploadedAsync(host, manager);
        Assert.Equal(HttpStatusCode.NoContent, (await UploadBlueskyCopyAsync(host, manager, hasCopy, [0xFF, 0xD8, 0xFF, 1])).StatusCode);
        var unused = await UploadedAsync(host, manager);

        foreach (var cover in new[] { bigPng, smallPng, bigJpeg, smallJpeg, hasCopy })
            await CreateAsync(host, manager, cover);

        var (status, ids) = await NeedingCopyAsync(host, manager);

        Assert.Equal(HttpStatusCode.OK, status);
        // A big picture and a PNG need a copy; a small JPEG, a cover with a copy and one no event uses do not.
        Assert.Equal(new[] { bigPng, smallPng, bigJpeg }.Order(), ids.Order());
        Assert.DoesNotContain(unused, ids);

        // Once the copy is kept, the cover drops out of the list.
        Assert.Equal(HttpStatusCode.NoContent, (await UploadBlueskyCopyAsync(host, manager, bigPng, [0xFF, 0xD8, 0xFF, 1])).StatusCode);
        Assert.DoesNotContain(bigPng, (await NeedingCopyAsync(host, manager)).Ids);
    }

    [Fact]
    public async Task AtMostTwentyCoversAreListedAndSomeoneWhoCannotEditIsRefused()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        for (var i = 0; i < CalendarEndpoints.MaxCoversNeedingBlueskyCopy + 2; i++)
            await CreateAsync(host, manager, await UploadedAsync(host, manager));

        var (_, first) = await NeedingCopyAsync(host, manager);
        Assert.Equal(CalendarEndpoints.MaxCoversNeedingBlueskyCopy, first.Count);

        // Leaving out the ones already tried brings up the rest, so none holds the front for good.
        var (_, rest) = await NeedingCopyAsync(host, manager, [.. first]);
        Assert.Equal(2, rest.Count);
        Assert.Empty(rest.Intersect(first));

        // Only the first 100 skip ids are read: the 20 after them are listed again.
        var filler = Enumerable.Range(0, CalendarEndpoints.MaxSkippedCoversNeedingBlueskyCopy).Select(_ => Guid.NewGuid());
        var (_, beyond) = await NeedingCopyAsync(host, manager, [.. filler, .. first]);
        Assert.Equal(first.Order(), beyond.Order());

        var viewer = (await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct)).Cookie;
        Assert.Equal(HttpStatusCode.Forbidden, (await NeedingCopyAsync(host, viewer)).Status);
    }

    [Fact]
    public async Task SomethingThatIsNotAPictureIsRefused()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var response = await UploadAsync(host, manager, "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"u8.ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task APictureOver8MBIsRefused()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var response = await UploadAsync(host, manager, Png(CalendarCoverPicture.MaxBytes + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(CalendarEndpoints.CoverTooBig, (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task AnEventIsSavedWithItsPicture()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var cover = await UploadedAsync(host, manager);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", Body(host, cover), manager, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(cover, (await ApiTestHost.BodyOf(response, Ct)).GetProperty("coverPictureId").GetGuid());
    }

    [Fact]
    public async Task APictureThatIsNotKeptIsRefused()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/calendar/events", Body(host, Guid.CreateVersion7()), manager, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static object BodyWithBlueskyPictures(ApiTestHost host, IEnumerable<(Guid Id, string Alt)> pictures, Guid? cover = null)
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
            publishToDiscord = true,
            coverPictureId = cover,
            blueskyPictures = pictures.Select(p => new { pictureId = p.Id, alt = p.Alt }).ToArray(),
            draft = true,
        };
    }

    [Fact]
    public async Task AnEventIsSavedWithItsBlueskyPicturesInOrder_EachWithItsAltText()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var first = await UploadedAsync(host, manager);
        var second = await UploadedAsync(host, manager);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/calendar/events", BodyWithBlueskyPictures(host, [(second, "The cabin"), (first, "")]), manager, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pictures = (await ApiTestHost.BodyOf(response, Ct)).GetProperty("blueskyPictures").EnumerateArray().ToList();
        Assert.Equal([second, first], pictures.Select(p => p.GetProperty("pictureId").GetGuid()));
        Assert.Equal(["The cabin", string.Empty], pictures.Select(p => p.GetProperty("alt").GetString()));
    }

    [Fact]
    public async Task MoreThanFourBlueskyPicturesAreRefused()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var pictures = new List<(Guid, string)>();

        for (var i = 0; i < CalendarBlueskyText.MostPictures + 1; i++)
            pictures.Add((await UploadedAsync(host, manager), string.Empty));

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/calendar/events", BodyWithBlueskyPictures(host, pictures), manager, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(CalendarBlueskyText.TooManyPictures, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlueskyAltTextOver2000CharactersIsRefused()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var picture = await UploadedAsync(host, manager);

        var response = await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/calendar/events",
            BodyWithBlueskyPictures(host, [(picture, new string('x', CalendarBlueskyText.AltMaxLength + 1))]),
            manager,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(CalendarBlueskyText.AltTooLong, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlueskyPictureThatIsNotKeptIsRefused()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/calendar/events", BodyWithBlueskyPictures(host, [(Guid.CreateVersion7(), "Gone")]), manager, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Bluesky's video ---

    private static byte[] Mp4() => [0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8];

    private static async Task<Guid> VideoUploadedAsync(ApiTestHost host, string cookie)
    {
        var request = host.Authenticated(HttpMethod.Post, "/api/calendar/bluesky-video", cookie);
        request.Content = new ByteArrayContent(Mp4());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");

        var response = await host.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("videoId").GetGuid();
    }

    private static object BodyWithBlueskyVideo(
        ApiTestHost host, Guid? videoId = null, string? url = null, string? alt = null, IEnumerable<(Guid Id, string Alt)>? pictures = null)
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
            publishToDiscord = true,
            blueskyVideo = new { videoId, url, alt },
            blueskyPictures = pictures?.Select(p => new { pictureId = p.Id, alt = p.Alt }).ToArray(),
            draft = true,
        };
    }

    [Fact]
    public async Task AnEventIsSavedWithItsBlueskyVideoFile_AndItsAltText()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var video = await VideoUploadedAsync(host, manager);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/calendar/events", BodyWithBlueskyVideo(host, videoId: video, alt: "The trailer"), manager, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await ApiTestHost.BodyOf(response, Ct)).GetProperty("blueskyVideo");
        Assert.Equal(video, saved.GetProperty("videoId").GetGuid());
        Assert.Equal("The trailer", saved.GetProperty("alt").GetString());
    }

    [Fact]
    public async Task AnEventIsSavedWithItsBlueskyVideoLink_AsAnAddressOnly()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/calendar/events", BodyWithBlueskyVideo(host, url: " https://videos.example/trailer.mp4 "), manager, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await ApiTestHost.BodyOf(response, Ct)).GetProperty("blueskyVideo");
        Assert.Equal("https://videos.example/trailer.mp4", saved.GetProperty("url").GetString());
        Assert.False(saved.TryGetProperty("videoId", out var kept) && kept.ValueKind == System.Text.Json.JsonValueKind.String);
    }

    [Fact]
    public async Task ABlueskyVideoFileWithPicturesIsRefused()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var video = await VideoUploadedAsync(host, manager);
        var picture = await UploadedAsync(host, manager);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/calendar/events", BodyWithBlueskyVideo(host, videoId: video, pictures: [(picture, "Poster")]), manager, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(CalendarBlueskyText.VideoAndPictures, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlueskyVideoLinkWithPicturesIsRefused()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var picture = await UploadedAsync(host, manager);

        var response = await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/calendar/events",
            BodyWithBlueskyVideo(host, url: "https://videos.example/trailer.mp4", pictures: [(picture, "Poster")]),
            manager,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(CalendarBlueskyText.VideoAndPictures, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlueskyVideoThatIsBothAFileAndALinkIsRefused()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var video = await VideoUploadedAsync(host, manager);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/calendar/events", BodyWithBlueskyVideo(host, videoId: video, url: "https://videos.example/trailer.mp4"), manager, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(CalendarBlueskyText.VideoNeedsOneSource, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlueskyVideoAltTextOver2000CharactersIsRefused()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var video = await VideoUploadedAsync(host, manager);

        var response = await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/calendar/events",
            BodyWithBlueskyVideo(host, videoId: video, alt: new string('x', CalendarBlueskyText.AltMaxLength + 1)),
            manager,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(CalendarBlueskyText.VideoAltTooLong, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://videos.example/trailer.mp4")]
    [InlineData("not a link")]
    [InlineData("https://videos.example:8443/trailer.mp4")]
    [InlineData("https://127.0.0.1/trailer.mp4")]
    [InlineData("https://localhost/trailer.mp4")]
    public async Task ABlueskyVideoLinkThatMayNotBeFetchedIsRefused(string link)
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/calendar/events", BodyWithBlueskyVideo(host, url: link), manager, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(VideoLinks.Problem(link)!, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlueskyVideoFileThatIsNotKeptIsRefused()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/calendar/events", BodyWithBlueskyVideo(host, videoId: Guid.CreateVersion7()), manager, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(CalendarBlueskyText.VideoGone, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    private static object BodyWithBlueskyChoices(ApiTestHost host, string[]? whoCanReply, bool? quotesOff, string[]? languages)
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
            publishToDiscord = true,
            blueskyWhoCanReply = whoCanReply,
            blueskyQuotesOff = quotesOff,
            blueskyLanguages = languages,
            draft = true,
        };
    }

    [Fact]
    public async Task AnEventIsSavedWithWhoCanReplyQuotePostsAndLanguages_AndALeftOutFieldKeepsThem()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var created = await host.SendJsonAsync(
            HttpMethod.Post, "/api/calendar/events", BodyWithBlueskyChoices(host, ["followers", "mentioned"], true, ["en", "de"]), manager, Ct);

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var saved = await ApiTestHost.BodyOf(created, Ct);
        Assert.Equal(["followers", "mentioned"], saved.GetProperty("blueskyWhoCanReply").EnumerateArray().Select(w => w.GetString()));
        Assert.True(saved.GetProperty("blueskyQuotesOff").GetBoolean());
        Assert.Equal(["en", "de"], saved.GetProperty("blueskyLanguages").EnumerateArray().Select(l => l.GetString()));

        // Saved again without them: the event keeps what it has.
        var id = saved.GetProperty("id").GetGuid();
        var kept = await host.SendJsonAsync(
            HttpMethod.Put, $"/api/calendar/events/{id}", BodyWithBlueskyChoices(host, null, null, null), manager, Ct);

        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        var again = await ApiTestHost.BodyOf(kept, Ct);
        Assert.Equal(["followers", "mentioned"], again.GetProperty("blueskyWhoCanReply").EnumerateArray().Select(w => w.GetString()));
        Assert.True(again.GetProperty("blueskyQuotesOff").GetBoolean());
        Assert.Equal(["en", "de"], again.GetProperty("blueskyLanguages").EnumerateArray().Select(l => l.GetString()));

        // Anyone, quote posts allowed and no language put it back.
        var reset = await host.SendJsonAsync(
            HttpMethod.Put, $"/api/calendar/events/{id}", BodyWithBlueskyChoices(host, ["anyone"], false, []), manager, Ct);

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var open = await ApiTestHost.BodyOf(reset, Ct);
        Assert.Equal(["anyone"], open.GetProperty("blueskyWhoCanReply").EnumerateArray().Select(w => w.GetString()));
        Assert.False(open.GetProperty("blueskyQuotesOff").GetBoolean());
        Assert.Empty(open.GetProperty("blueskyLanguages").EnumerateArray());
    }

    [Theory]
    [InlineData("nobody", "followers")]
    [InlineData("anyone", "nobody")]
    [InlineData("followers", "everyone")]
    public async Task AReplyChoiceThatIsNotABlueskyChoiceIsRefused(string first, string second)
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/calendar/events", BodyWithBlueskyChoices(host, [first, second], false, []), manager, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(CalendarBlueskyText.ReplyChoiceNotAllowed, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en", "de", "fr", "es")]
    [InlineData("en", "not a code", "", "")]
    public async Task MoreThanThreeLanguagesOrABadCodeIsRefused(string a, string b, string c, string d)
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/calendar/events", BodyWithBlueskyChoices(host, ["anyone"], false, [a, b, c, d]), manager, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(CalendarBlueskyText.LanguagesNotAllowed, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFormerCoverStaysKeptWhileTheEventsBlueskyPicturesListIt()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var picture = await UploadedAsync(host, manager);
        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/calendar/events", BodyWithBlueskyPictures(host, [(picture, "Poster")], cover: picture), manager, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = (await ApiTestHost.BodyOf(response, Ct)).GetProperty("id").GetGuid();

        var other = await UploadedAsync(host, manager);
        var updated = await host.SendJsonAsync(HttpMethod.Put, $"/api/calendar/events/{id}", Body(host, other), manager, Ct);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.True(await KeptAsync(picture));
    }

    [Fact]
    public async Task DeletingTheEventDeletesItsPicture()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var cover = await UploadedAsync(host, manager);
        var id = await CreateAsync(host, manager, cover);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await host.SendJsonAsync(HttpMethod.Delete, $"/api/calendar/events/{id}", null, manager, Ct)).StatusCode);

        Assert.False(await KeptAsync(cover));
    }

    [Fact]
    public async Task AnotherPictureDeletesTheOldOne()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var first = await UploadedAsync(host, manager);
        var id = await CreateAsync(host, manager, first);
        var second = await UploadedAsync(host, manager);

        var updated = await host.SendJsonAsync(HttpMethod.Put, $"/api/calendar/events/{id}", Body(host, second), manager, Ct);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.False(await KeptAsync(first));
        Assert.True(await KeptAsync(second));
    }

    /// <summary>A copy of an event shares its picture, so the picture stays while either still uses it.</summary>
    [Fact]
    public async Task APictureACopyStillUsesIsKept()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var cover = await UploadedAsync(host, manager);
        var original = await CreateAsync(host, manager, cover);
        await CreateAsync(host, manager, cover);

        await host.SendJsonAsync(HttpMethod.Delete, $"/api/calendar/events/{original}", null, manager, Ct);

        Assert.True(await KeptAsync(cover));
    }

    /// <summary>
    /// The calendar's hourly sweep deletes a picture no event was saved with once it is a day old,
    /// whether or not anybody uploads again, and keeps a younger one and one an event uses.
    /// </summary>
    [Fact]
    public async Task TheSweepDeletesAPictureNeverSaved_OnceItIsADayOld()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var unsaved = await UploadedAsync(host, manager);
        var saved = await UploadedAsync(host, manager);
        await CreateAsync(host, manager, saved);

        host.Clock.Advance(TimeSpan.FromHours(23));
        await using (var context = db.NewContext())
            await new CalendarCoverSweep(context, host.Clock).RunOnceAsync(Ct);

        Assert.True(await KeptAsync(unsaved));

        host.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        var young = await UploadedAsync(host, manager);
        await using (var context = db.NewContext())
            await new CalendarCoverSweep(context, host.Clock).RunOnceAsync(Ct);

        Assert.False(await KeptAsync(unsaved));
        Assert.True(await KeptAsync(saved));
        Assert.True(await KeptAsync(young));
    }

    /// <summary>
    /// A Bluesky post that was sent keeps every picture of its list (pictures 2 to 4 sit only in the
    /// destination's options): the sweep does not delete them, and nor does letting go of the cover.
    /// </summary>
    [Fact]
    public async Task ThePicturesOfASentBlueskyPostAreNeitherSweptNorDropped()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var first = await UploadedAsync(host, manager);
        var second = await UploadedAsync(host, manager);
        var third = await UploadedAsync(host, manager);
        var unsaved = await UploadedAsync(host, manager);
        var eventId = await CreateAsync(host, manager, first);

        await using (var context = db.NewContext())
        {
            var post = new Post
            {
                Id = Guid.CreateVersion7(),
                Kind = PostKinds.EventDate,
                Status = PostStatuses.Scheduled,
                Title = "Movie night",
                Text = "Bring snacks",
                CreatedAt = host.Clock.UtcNow,
                UpdatedAt = host.Clock.UtcNow,
            };
            post.Destinations.Add(new PostDestination
            {
                Id = Guid.CreateVersion7(),
                Network = PostNetworks.Bluesky,
                State = PostDestinationStates.Posted,
                Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions(
                    Pictures: [new BlueskyPostPicture(first, "Poster"), new BlueskyPostPicture(second, ""), new BlueskyPostPicture(third, "Crowd")])),
            });
            context.Posts.Add(post);
            await context.SaveChangesAsync(Ct);
        }

        host.Clock.Advance(CalendarCoverSweep.KeptUnsaved + TimeSpan.FromMinutes(1));
        await using (var context = db.NewContext())
            await new CalendarCoverSweep(context, host.Clock).RunOnceAsync(Ct);

        Assert.True(await KeptAsync(second));
        Assert.True(await KeptAsync(third));
        Assert.False(await KeptAsync(unsaved));

        await host.SendJsonAsync(HttpMethod.Delete, $"/api/calendar/events/{eventId}", null, manager, Ct);

        Assert.True(await KeptAsync(first));
    }

    /// <summary>Uploading deletes nothing: the sweep alone does.</summary>
    [Fact]
    public async Task UploadingDeletesNoOtherPicture()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var unsaved = await UploadedAsync(host, manager);

        host.Clock.Advance(TimeSpan.FromDays(2));
        await UploadedAsync(host, manager);

        Assert.True(await KeptAsync(unsaved));
    }
}
