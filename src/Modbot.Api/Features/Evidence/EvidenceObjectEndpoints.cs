using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Evidence.Health;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Upload;

namespace Modbot.Api.Features.Evidence;

/// <summary>
/// Serving evidence, and destroying it (design §10 and §6).
/// </summary>
/// <remarks>
/// <para>
/// A moderation tool where staff routinely open files uploaded by other staff is a near-ideal
/// stored-XSS target: the attacker is already authenticated, the audience is exactly the people
/// with the most permissions, and the delivery mechanism is a feature. So every byte Modbot serves
/// goes out as an attachment, typed from Modbot's own determination rather than anything a client
/// said, with <c>nosniff</c> and a sandbox CSP — and SVG and HTML could not have been stored in
/// the first place.
/// </para>
/// <para>
/// <strong>"Unavailable", "destroyed" and "never existed" are three different facts and the
/// response says which.</strong> Collapsing them into a 404 would make a lost store look like a
/// case file that never had evidence, which is the one confusion design §8 exists to prevent.
/// </para>
/// </remarks>
public static class EvidenceObjectEndpoints
{
    private const string LoggerName = "Modbot.Evidence.Serving";

    internal static IEndpointRouteBuilder MapEvidenceObjects(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/evidence").WithTags("Evidence").RequireAuthorization();

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                [FromQuery] string? reportId,
                CancellationToken ct) =>
            {
                var query = db.EvidenceBlobs.AsNoTracking();

                query = reportId is { Length: > 0 }
                    ? query.Where(b => b.ReportId == reportId)
                    : query.Where(b => b.ReportId != null);

                var rows = await query
                    .OrderByDescending(b => b.FirstStoredAt)
                    .Take(500)
                    .ToListAsync(ct);

                return Results.Ok(rows.Select(Describe).ToList());
            })
            .RequiresFlag(ModbotPermissions.ViewEvidence)
            .WithName("ListEvidence")
            .WithSummary("What is attached to a case file")
            .WithDescription(
                "Answered entirely from the blob record, so listing evidence costs the store "
                + "no request and no egress. Destroyed items are listed too, because a case file "
                + "that looks like it never had evidence is indistinguishable from one nobody ever "
                + "documented.")
            .Produces<IReadOnlyList<EvidenceObjectView>>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/{hash:length(64)}/metadata", async (
                string hash,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var row = await db.EvidenceBlobs.AsNoTracking().FirstOrDefaultAsync(b => b.Hash == hash, ct);

                return row is null ? NeverExisted() : Results.Ok(Describe(row));
            })
            .RequiresFlag(ModbotPermissions.ViewEvidence)
            .WithName("GetEvidenceMetadata")
            .WithSummary("Everything about a piece of evidence except its bytes")
            .Produces<EvidenceObjectView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{hash:length(64)}", async (
                HttpContext http,
                string hash,
                [FromServices] ModbotContext db,
                [FromServices] IEvidenceStore store,
                [FromServices] EvidenceStoreMonitor monitor,
                [FromServices] EvidenceOptions options,
                [FromServices] ILoggerFactory loggers,
                CancellationToken ct) =>
            {
                if (!EvidenceHash.TryParse(hash, out var parsed))
                    return NeverExisted();

                var row = await db.EvidenceBlobs.AsNoTracking().FirstOrDefaultAsync(b => b.Hash == hash, ct);
                if (row is null)
                    return NeverExisted();

                if (row.IsDestroyed)
                {
                    return Results.Json(
                        new
                        {
                            error = $"These bytes were destroyed on {row.DestroyedAt:u} by "
                                + $"{row.DestroyedBy}: {row.DestroyedReason}. The record that this "
                                + "evidence existed is kept; the file itself is gone permanently.",
                        },
                        statusCode: StatusCodes.Status410Gone);
                }

                var health = monitor.Current;
                if (health.State is EvidenceStoreState.Unavailable or EvidenceStoreState.NotConfigured)
                {
                    return Results.Json(
                        new { error = health.Explanation },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                var fileName = SafeFileName(row.FileName, hash, row.ContentType);

                // The cheap path, and on Railway the free one: the bucket talks to the browser and
                // Modbot steps out of the way. It is honestly a little weaker — a presigned GET can
                // pin the disposition and the type by signing them but cannot add nosniff or a CSP,
                // because those are not response-override parameters in the S3 API. The format
                // allowlist is what is doing the work there, which is why it is closed.
                if (options.DirectDeliveryEnabled
                    && store.Capabilities.HasFlag(EvidenceStoreCapabilities.PresignedRead))
                {
                    var url = await store.TryCreatePresignedReadAsync(
                        parsed,
                        options.PresignedUrlTtl,
                        new PresignedReadOptions(row.ContentType, fileName),
                        ct);

                    if (url is not null)
                    {
                        // The URL must not be cached anywhere shared: anyone holding it within its
                        // window can fetch the object without authenticating, which is intrinsic to
                        // presigning and is why the window is five minutes.
                        http.Response.Headers.CacheControl = "private, no-store";
                        return Results.Redirect(url.ToString(), permanent: false);
                    }
                }

                var range = store.Capabilities.HasFlag(EvidenceStoreCapabilities.RangeRead)
                    ? ResolveRange(http.Request.Headers.Range, row.ByteSize)
                    : null;

                if (range is { Satisfiable: false })
                {
                    http.Response.Headers.ContentRange = $"bytes */{row.ByteSize}";
                    return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
                }

                var slice = range is { Satisfiable: true, First: var first, Last: var last }
                    ? new ByteRange(first, last)
                    : (ByteRange?)null;

                var body = await store.OpenReadAsync(parsed, slice, ct);
                if (body is null)
                {
                    // The blob record says these bytes exist and the store cannot find them. That is
                    // the partial case of §8.3 — an object deleted by hand, a lifecycle rule
                    // somebody added, bit rot — and it is a different failure from wholesale loss.
                    loggers.CreateLogger(LoggerName).LogError(
                        "Evidence {Hash} is recorded in the blob record and absent from {Store}.",
                        hash,
                        store.Description);

                    return Results.Json(
                        new
                        {
                            error = "Modbot's records say this evidence is stored and the store "
                                + "cannot find it. Nothing has been destroyed; something is wrong "
                                + "with the store. This has been logged for the operator.",
                        },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                await using (body)
                {
                    ApplyHeaders(http.Response, row.ContentType, fileName);

                    if (slice is { } served)
                    {
                        var length = served.Length ?? row.ByteSize - served.First;

                        http.Response.StatusCode = StatusCodes.Status206PartialContent;
                        http.Response.Headers.AcceptRanges = "bytes";
                        http.Response.Headers.ContentRange =
                            $"bytes {served.First}-{served.Last ?? row.ByteSize - 1}/{row.ByteSize}";
                        http.Response.ContentLength = length;

                        await body.CopyToAsync(http.Response.Body, ct);
                        return Results.Empty;
                    }

                    http.Response.StatusCode = StatusCodes.Status200OK;
                    http.Response.Headers.AcceptRanges =
                        store.Capabilities.HasFlag(EvidenceStoreCapabilities.RangeRead) ? "bytes" : "none";
                    http.Response.ContentLength = row.ByteSize;

                    // Hashed on the way past, because this is the one delivery path where Modbot can
                    // see the bytes. An object at key <h> whose contents do not hash to <h> is
                    // detectably wrong, and evidence that can be swapped without detection is worth
                    // nothing in the argument it exists to settle.
                    await using var counting = new CountingHashStream(body, row.ByteSize);
                    await counting.CopyToAsync(http.Response.Body, ct);

                    if (counting.BytesRead == row.ByteSize && counting.Hash == parsed)
                        return Results.Empty;

                    // The headers and most of the body are already on the wire, so the only
                    // remaining way to say "do not trust this" is to break the connection. A
                    // truncated download is a worse experience than a corrupt one and a much better
                    // outcome.
                    loggers.CreateLogger(LoggerName).LogError(
                        "Evidence {Hash} read back from {Store} as {Actual} over {Bytes} bytes. The "
                        + "object does not match its content address and the response was aborted.",
                        hash,
                        store.Description,
                        counting.BytesRead == row.ByteSize ? counting.Hash.Hex : "a different length",
                        counting.BytesRead);

                    http.Abort();
                    return Results.Empty;
                }
            })
            .RequiresFlag(ModbotPermissions.ViewEvidence)
            .WithName("GetEvidence")
            .WithSummary("The bytes, as an attachment")
            .WithDescription(
                "Served with Content-Disposition: attachment, the content type Modbot determined "
                + "from the bytes themselves, X-Content-Type-Options: nosniff and a sandbox CSP. On "
                + "a store that can presign and with direct delivery on, this redirects to a "
                + "five-minute URL and the bucket serves the bytes; otherwise they stream through "
                + "Modbot and are re-hashed on the way past. Range requests are answered where the "
                + "backend supports them, because that is what makes video seeking work.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status206PartialContent)
            .Produces(StatusCodes.Status302Found)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status410Gone)
            .Produces(StatusCodes.Status416RangeNotSatisfiable)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/{hash:length(64)}/destroy", async (
                HttpContext http,
                string hash,
                EvidenceDestroyRequest body,
                [FromServices] EvidenceDestroyer destroyer,
                CancellationToken ct) =>
            {
                if (!EvidenceHash.TryParse(hash, out var parsed))
                    return NeverExisted();

                if (string.IsNullOrWhiteSpace(body.Reason))
                {
                    return Results.BadRequest(new
                    {
                        error = "A reason is required. It is recorded permanently, alongside who "
                            + "destroyed the evidence and when, and it is the only account that "
                            + "will survive the bytes.",
                    });
                }

                var actor = http.User.Identity?.Name ?? "unknown";

                try
                {
                    var result = await destroyer.DestroyAsync(parsed, actor, body.Reason.Trim(), ct);

                    return Results.Ok(new EvidenceDestroyResponse(
                        result.Destroyed, result.BlockedByReports, result.Message));
                }
                catch (EvidenceStoreUnavailableException e)
                {
                    return Results.Json(
                        new { error = e.Message },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            })
            .RequiresFlag(ModbotPermissions.DestroyEvidence)
            .WithName("DestroyEvidence")
            .WithSummary("Delete the bytes permanently, keeping the record that they existed")
            .WithDescription(
                "Refcounted: content addressing means two case files can cite one object, so the "
                + "reports still referencing these bytes are named back rather than having their "
                + "evidence quietly removed. There is no undo — no backend Modbot supports could "
                + "implement one — and what survives is the hash, the size, the type, who "
                + "destroyed it and why.")
            .Produces<EvidenceDestroyResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static IResult NeverExisted() => Results.NotFound(new
    {
        error = "No evidence with that content address has ever been stored here. That is not the "
            + "same as destroyed, and not the same as a store Modbot cannot reach.",
    });

    private static EvidenceObjectView Describe(EvidenceBlob blob) => new(
        blob.Hash,
        blob.ByteSize,
        blob.ContentType,
        blob.FileName,
        blob.UploaderId,
        blob.ReportId,
        blob.Origin.ToString(),
        blob.FirstStoredAt,
        blob.IsDestroyed,
        blob.DestroyedAt,
        blob.DestroyedBy,
        blob.DestroyedReason);

    /// <summary>The four headers of design §10.2, on every byte Modbot serves itself.</summary>
    private static void ApplyHeaders(HttpResponse response, string contentType, string fileName)
    {
        response.ContentType = contentType;
        response.Headers.ContentDisposition = Disposition(fileName);

        // Without this a browser is free to decide the bytes are something more interesting than
        // what the Content-Type said, which is most of how an "image upload" becomes an XSS.
        response.Headers["X-Content-Type-Options"] = "nosniff";

        // The strongest single mitigation available without a second hostname: anything that did
        // slip through the allowlist renders in an opaque origin with no script, no forms and no
        // same-origin access.
        response.Headers.ContentSecurityPolicy = "sandbox";

        // The bytes are immutable — they are named after their own hash — but the URL is
        // authenticated, so it must not be cached anywhere shared.
        response.Headers.CacheControl = "private, no-store";
    }

    /// <summary>
    /// Builds the disposition header from a filename a person typed.
    /// </summary>
    /// <remarks>
    /// The filename is hostile input and always has been: it came from an uploader and it has no
    /// bearing on where the bytes live, because keys are the hash and nothing else. So it is
    /// reduced to something that cannot break out of a quoted header, and sent twice — once
    /// plainly for old clients and once percent-encoded per RFC 5987 so a non-ASCII name survives.
    /// </remarks>
    private static string Disposition(string fileName)
    {
        var encoded = Uri.EscapeDataString(fileName);
        return $"attachment; filename=\"{fileName}\"; filename*=UTF-8''{encoded}";
    }

    /// <summary>
    /// A filename safe to put in a header, derived from the uploader's and falling back to the hash.
    /// </summary>
    private static string SafeFileName(string? declared, string hash, string contentType)
    {
        var extension = contentType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            _ => ".bin",
        };

        if (string.IsNullOrWhiteSpace(declared))
            return $"evidence-{hash[..12]}{extension}";

        var cleaned = new StringBuilder(declared.Length);
        foreach (var c in declared)
        {
            // Quotes and backslashes would end the header value early; separators and control
            // characters have no business in a name that is only ever displayed.
            if (char.IsControl(c) || c is '"' or '\\' or '/' or ':' or ';' or ',' or '|')
                continue;

            cleaned.Append(c);
        }

        var name = cleaned.ToString().Trim().Trim('.');

        return name.Length == 0
            ? $"evidence-{hash[..12]}{extension}"
            : name.Length > 120 ? name[..120] : name;
    }

    /// <summary>
    /// Turns a <c>Range</c> header into the slice to serve, or into "this cannot be satisfied".
    /// </summary>
    /// <remarks>
    /// One range only. Multipart byte ranges exist and nothing needs them here: the reason ranges
    /// are supported at all is that <c>&lt;video&gt;</c> seeking is HTTP range requests, and a
    /// player asks for one contiguous run at a time.
    /// </remarks>
    private static RangeRequest? ResolveRange(string? header, long size)
    {
        if (string.IsNullOrWhiteSpace(header) || size <= 0)
            return null;

        if (!RangeHeaderValue.TryParse(header, out var parsed)
            || !parsed.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase)
            || parsed.Ranges.Count != 1)
        {
            return null;
        }

        var range = parsed.Ranges.Single();

        if (range.From is null)
        {
            // A suffix range: the last N bytes. Asking for more than exists is not an error — it
            // means the whole object, which is what every player expects.
            if (range.To is not { } suffix || suffix <= 0)
                return null;

            var start = Math.Max(0, size - suffix);
            return new RangeRequest(start, size - 1, true);
        }

        var first = range.From.Value;
        if (first >= size)
            return new RangeRequest(0, 0, false);

        var last = range.To is { } to ? Math.Min(to, size - 1) : size - 1;

        return last < first ? new RangeRequest(0, 0, false) : new RangeRequest(first, last, true);
    }

    /// <param name="Satisfiable">False when the range starts past the end of the object.</param>
    private readonly record struct RangeRequest(long First, long Last, bool Satisfiable);
}
