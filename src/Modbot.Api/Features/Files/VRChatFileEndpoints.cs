using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Modbot.Core.Data;
using Modbot.VRChat;
using Modbot.VRChat.Files;

namespace Modbot.Api.Features.Files;

/// <summary>
/// Pictures and videos from VRChat, fetched by Modbot because a browser cannot fetch them itself.
/// </summary>
/// <remarks>
/// <para>
/// A VRChat file address needs the session cookie and answers with a redirect to a delivery host,
/// and a browser has neither the cookie nor a way to be given one. So an <c>&lt;img&gt;</c> that
/// points straight at <c>api.vrchat.cloud</c> shows nothing, and every face in Modbot comes
/// through here instead (VRChat files design).
/// </para>
/// <para>
/// <strong>Any signed-in person may use it, and no permission gates it.</strong> These files are
/// public on VRChat's own delivery network to anyone who has the address, and the address is
/// already in the profile the caller just read; fetching it for them adds no reach. What the
/// route must not become is an open fetcher for the internet, and that is what the address check
/// is for: VRChat's hosts and nothing else, on the first request and on every redirect.
/// </para>
/// <para>
/// <strong>The API's own answers keep VRChat's real addresses.</strong> A profile's
/// <c>profilePictureUrl</c> is the address VRChat gave, unchanged, because a program holding an
/// API key wants the real one. Turning it into a Modbot address is the web app's job and it does
/// it in the browser.
/// </para>
/// </remarks>
public static class VRChatFileEndpoints
{
    /// <summary>The route, which the web app builds its picture addresses from.</summary>
    public const string Path = "/api/files/vrchat";

    /// <summary>
    /// A week. The bytes never change -- the address names a version -- so this could be a year;
    /// a week is long enough that a moderator's day of scrolling costs one fetch per picture and
    /// short enough that a browser cache is not a place things live for ever.
    /// </summary>
    public const string CacheControl = "private, max-age=604800";

    public static IEndpointRouteBuilder MapVRChatFiles(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(Path).WithTags("Files").RequireAuthorization();

        group.MapGet("", async (
                HttpContext http,
                [FromQuery] string? url,
                [FromServices] IVRChatGate gate,
                [FromServices] VRChatFileCache cache,
                [FromServices] ModbotContext db,
                CancellationToken ct) => await ServeAsync(http, url, gate, cache, db, ct))
            .WithName("GetVRChatFile")
            .WithSummary("A picture or video from VRChat")
            .WithDescription(
                "Fetches a VRChat file address through Modbot and returns the bytes. VRChat's "
                + "file addresses need the account's session cookie and redirect to a delivery "
                + "host, so a browser cannot load one directly. Only addresses on vrchat.cloud "
                + "are accepted, only pictures and video are returned, and the answer is cached "
                + "on disk because a VRChat file address names a version and never changes.")
            // Listed one by one: a wildcard content type is not merely undocumented, it throws
            // while the route is being mapped and takes every other endpoint down with it.
            .Produces<byte[]>(
                StatusCodes.Status200OK,
                "image/png", "image/jpeg", "image/webp", "image/gif", "video/mp4")
            .Produces(StatusCodes.Status304NotModified)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status415UnsupportedMediaType)
            .Produces(StatusCodes.Status502BadGateway);

        return app;
    }

    private static async Task<IResult> ServeAsync(
        HttpContext http,
        string? url,
        IVRChatGate gate,
        VRChatFileCache cache,
        ModbotContext db,
        CancellationToken ct)
    {
        // The operator's switch, checked before the cache as well as before VRChat: off means this
        // server does not serve VRChat pictures, not that it serves the ones it happens to hold.
        var settings = await db.GetSettingsAsync(ct);
        if (!settings.VRChatImagesProxied)
            return Problem(StatusCodes.Status404NotFound, "This server does not proxy VRChat pictures.");

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var address))
            return Problem(StatusCodes.Status400BadRequest, "Give a url to fetch.");

        if (!VRChatFiles.IsVRChatAddress(address))
            return Problem(StatusCodes.Status400BadRequest, "That is not an address VRChat serves files from.");

        // Keyed on what the caller asked for, character for character, so the address the web app
        // built and the address a second screen built hit the same file.
        // The cache hands back an open file rather than a path, and the result disposes it. A path
        // would only be true until the sweep or another store used the name; the open file is the
        // bytes themselves.
        if (cache.Find(url) is { } held)
        {
            Headers(http, CacheControl);

            return Results.Stream(
                held.Content,
                held.ContentType,
                entityTag: Tag(held.Tag),
                enableRangeProcessing: true);
        }

        var fetched = await gate.FetchFileAsync(address, ct);

        if (fetched.Outcome is not VRChatFileOutcome.Fetched || fetched.File is null)
            return Refused(fetched);

        // The cap the operator set, read now rather than held, so raising it takes effect on the
        // next picture instead of on the next restart. One scalar read, on the slow path only.
        var capacity = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => (long?)s.VRChatFileCacheBytes)
            .FirstOrDefaultAsync(ct) ?? 0;

        await cache.StoreAsync(url, fetched.File, capacity, ct);

        Headers(http, CacheControl);

        return Results.Bytes(
            fetched.File.Bytes,
            fetched.File.ContentType,
            entityTag: Tag(VRChatFileCache.KeyFor(url)));
    }

    /// <summary>
    /// How every picture goes out.
    /// </summary>
    /// <remarks>
    /// A proxy turns somebody else's file into a file on Modbot's own address, which is the whole
    /// reason the evidence endpoints are as careful as they are. Only pictures and video get this
    /// far and SVG never does, but the bytes inside a file called <c>image/png</c> are still
    /// somebody else's: <c>nosniff</c> stops a browser deciding they are HTML after all, and the
    /// sandbox policy means a tab opened straight at this address has no origin to attack from.
    /// </remarks>
    private static void Headers(HttpContext http, string cacheControl)
    {
        http.Response.Headers.CacheControl = cacheControl;
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers.ContentSecurityPolicy = "sandbox";
    }

    /// <summary>
    /// What each refusal answers with.
    /// </summary>
    /// <remarks>
    /// A deployment with no VRChat session -- one still being set up, and every demo -- answers
    /// 404 rather than an error, because from the browser's side there is genuinely no picture
    /// there, and a broken image is a better answer than a page of red. A demo therefore shows
    /// whatever its cache was seeded with and nothing else.
    /// </remarks>
    private static IResult Refused(VRChatFileResult fetched) => fetched.Outcome switch
    {
        VRChatFileOutcome.NotVRChatAddress =>
            Problem(StatusCodes.Status400BadRequest, fetched.Problem ?? "That is not one of VRChat's addresses."),

        VRChatFileOutcome.NoSession or VRChatFileOutcome.NotFound =>
            Problem(StatusCodes.Status404NotFound, fetched.Problem ?? "There is no such file."),

        VRChatFileOutcome.NotShowable =>
            Problem(
                StatusCodes.Status415UnsupportedMediaType,
                fetched.Problem ?? "That file is not a picture or a video."),

        VRChatFileOutcome.TooBig =>
            Problem(StatusCodes.Status502BadGateway, fetched.Problem ?? "That file is too large to pass on."),

        _ => Problem(StatusCodes.Status502BadGateway, fetched.Problem ?? "VRChat could not be reached."),
    };

    private static IResult Problem(int status, string error) =>
        Results.Json(new { error }, statusCode: status);

    private static EntityTagHeaderValue Tag(string key) =>
        new('"' + key + '"');
}
