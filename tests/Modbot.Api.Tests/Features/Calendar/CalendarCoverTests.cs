using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// The picture cropped for Discord (calendar design §15.4, added 2026-10-02): kept by Modbot behind
/// Manage calendar, a picture by its bytes and at most 8 MB, saved on the event by its id, and
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
            await context.CalendarEvents.ExecuteDeleteAsync(Ct);

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
    public async Task KeepingAPictureNeedsManageCalendar()
    {
        await using var host = await StartAsync();
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(host, viewer, Png())).StatusCode);
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

    /// <summary>A picture kept and never saved on an event is deleted a day later, by the next upload.</summary>
    [Fact]
    public async Task APictureNeverSavedIsDeletedADayLater()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var unsaved = await UploadedAsync(host, manager);

        host.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        await UploadedAsync(host, manager);

        Assert.False(await KeptAsync(unsaved));
    }
}
