using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Files;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Calendar;

/// <summary>The id a video kept for Bluesky's post answers with.</summary>
/// <param name="VideoId">Save it in the event's <c>blueskyVideo</c>.</param>
public sealed record CalendarBlueskyVideoFileView(Guid VideoId);

/// <summary>
/// The video file of an event's Bluesky post: kept by Modbot from the moment it is picked until the
/// post goes out, like a cover picture (<see cref="CalendarCoverPicture"/>).
/// </summary>
internal static class CalendarBlueskyVideoFiles
{
    public const string TooBig = "The video is larger than 50 MB.";
    public const string NotAVideo = "The video must be an MP4, MPEG, WebM or QuickTime file.";

    public static async Task<IResult> UploadAsync(HttpContext http, ModbotContext db, IModbotClock clock, CancellationToken ct)
    {
        if (CalendarEndpoints.RefusedWithoutEditorRights(http) is { } refused)
            return refused;

        if (http.Request.ContentLength > VideoFormats.MaxBytes)
            return Results.Json(new { error = TooBig }, statusCode: StatusCodes.Status413PayloadTooLarge);

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = VideoFormats.MaxBytes + 1;

        var bytes = await ReadAsync(http.Request.Body, http.Request.ContentLength, ct);

        if (bytes.Length > VideoFormats.MaxBytes)
            return Results.Json(new { error = TooBig }, statusCode: StatusCodes.Status413PayloadTooLarge);

        // The type its bytes have, never what the browser said.
        if (VideoFormats.Sniff(bytes) is not { } type)
            return Results.BadRequest(new { error = NotAVideo });

        var video = new CalendarBlueskyVideo
        {
            Id = Guid.CreateVersion7(),
            Bytes = bytes,
            ContentType = type,
            CreatedAt = clock.UtcNow,
            CreatedByUserId = ModbotAuth.UserIdOf(http.User),
        };

        db.CalendarBlueskyVideos.Add(video);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new CalendarBlueskyVideoFileView(video.Id));
    }

    public static async Task<IResult> GetAsync(HttpContext http, Guid id, ModbotContext db, CancellationToken ct)
    {
        var video = await db.CalendarBlueskyVideos.AsNoTracking()
            .Where(v => v.Id == id)
            .Select(v => new { v.Bytes, v.ContentType })
            .FirstOrDefaultAsync(ct);

        if (video is null)
            return Results.NotFound();

        // Never changes once kept, so the address can be kept as long as it lives.
        http.Response.Headers.CacheControl = "private, max-age=604800, immutable";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers.ContentSecurityPolicy = "sandbox";

        return Results.Bytes(video.Bytes, video.ContentType, enableRangeProcessing: true);
    }

    /// <summary>
    /// The video in a request body, read until it runs one byte past <see cref="VideoFormats.MaxBytes"/>
    /// and no further. A body that says its length (a browser's file always does) is read straight into
    /// an array of that size, with no second copy.
    /// </summary>
    internal static async Task<byte[]> ReadAsync(Stream body, long? length, CancellationToken ct)
    {
        if (length is { } size and <= VideoFormats.MaxBytes)
        {
            var whole = new byte[size];
            var filled = 0;

            while (filled < whole.Length)
            {
                var read = await body.ReadAsync(whole.AsMemory(filled), ct);
                if (read == 0)
                    break;

                filled += read;
            }

            if (filled < whole.Length)
                Array.Resize(ref whole, filled);

            return whole;
        }

        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int got;

        while ((got = await body.ReadAsync(buffer, ct)) > 0)
        {
            var room = VideoFormats.MaxBytes + 1 - (int)copy.Length;
            copy.Write(buffer, 0, Math.Min(got, room));

            if (copy.Length > VideoFormats.MaxBytes)
                break;
        }

        return copy.ToArray();
    }
}
