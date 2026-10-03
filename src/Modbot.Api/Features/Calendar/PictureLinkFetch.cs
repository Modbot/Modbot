using Microsoft.AspNetCore.Http;
using Modbot.Core.Data;
using Modbot.Core.Net;

namespace Modbot.Api.Features.Calendar;

/// <summary>
/// The picture behind a picture link, for a form to crop and turn into a PNG in the browser
/// (calendar design §15.2): the event form's, and the Marketing composer's (posts design §4.3).
/// </summary>
/// <remarks>
/// A browser cannot read another site's picture into a canvas, and VRChat serves its files only to
/// a signed-in session, so Modbot fetches it: any other link under <see cref="PictureLinks"/>' guard
/// (https, public addresses on every hop, size and time limits, a picture by its bytes), a VRChat file
/// link through the VRChat side. Nothing is kept.
/// </remarks>
internal static class PictureLinkFetch
{
    public static async Task<IResult> AnswerAsync(
        HttpContext http, string? url, ModbotContext db, Core.Files.IPictures? vrchatPictures, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)
            || url.Length > PictureLinks.MaxLinkLength
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var link))
        {
            return Results.BadRequest(new { error = PictureLinks.NotHttps });
        }

        if (PictureLinks.Problem(link) is { } problem)
            return Results.BadRequest(new { error = problem });

        Core.Files.PictureBytes? picture;

        if (Core.Files.VRChatFileIds.IsVRChatFileHost(link.Host))
        {
            // The operator's switch for VRChat pictures: off, this server fetches none.
            var settings = await db.GetSettingsAsync(ct);

            if (!settings.VRChatImagesProxied)
            {
                return Results.Json(
                    new { error = "VRChat pictures are not fetched through this server." },
                    statusCode: StatusCodes.Status409Conflict);
            }

            picture = vrchatPictures is null
                ? null
                : await vrchatPictures.FetchAsync(link.ToString(), ct);

            if (picture is null)
            {
                return Results.Json(
                    new { error = "Could not fetch the picture from VRChat." },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        }
        else
        {
            var fetched = await PictureLinks.Shared.FetchAsync(link.ToString(), ct);

            if (fetched.Picture is null)
            {
                return Results.Json(
                    new { error = fetched.Problem ?? PictureLinks.Unreachable },
                    statusCode: fetched.Problem is PictureLinks.Private or PictureLinks.NotHttps
                        ? StatusCodes.Status400BadRequest
                        : StatusCodes.Status502BadGateway);
            }

            picture = fetched.Picture;
        }

        // The type its bytes have, never what the host said (PictureFormats).
        if (Core.Files.PictureFormats.Sniff(picture.Bytes) is not { } type
            || picture.Bytes.Length > PictureLinks.MaxBytes)
        {
            return Results.Json(
                new { error = PictureLinks.NotAPicture },
                statusCode: StatusCodes.Status502BadGateway);
        }

        // Somebody else's bytes on Modbot's own address: never sniffed into something else,
        // never a page with an origin, never kept by the browser.
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers.ContentSecurityPolicy = "sandbox";

        return Results.Bytes(picture.Bytes, type);
    }
}
