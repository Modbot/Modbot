using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Imports;

/// <summary>
/// Uploading old data, and watching it come in (import design §4).
/// </summary>
/// <remarks>
/// <para>
/// The upload is stored and queued; the job reads it. A file that is not JSON at all ends as a
/// failed import with the parser's reason rather than a 400, so that the answer to "what
/// happened to my upload" is always in the same place.
/// </para>
/// <para>
/// Two body shapes: the file as the body, or a multipart form with a <c>file</c> part. The
/// first is what <c>curl --data-binary</c> sends; the second is what a browser's file picker
/// sends. Kestrel's default body limit is below the cap, so it is raised on this endpoint only.
/// </para>
/// </remarks>
public static class ImportEndpoints
{
    public const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>How many past imports the list shows.</summary>
    public const int ListLength = 50;

    public static IEndpointRouteBuilder MapImports(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/imports")
            .WithTags("Imports")
            .RequiresFlag(ModbotPermissions.ImportOldData);

        group.MapPost("/", async (
                HttpContext http,
                [FromQuery] string? source,
                [FromQuery] string? seenBy,
                [FromQuery] bool? dryRun,
                [FromQuery] bool? dedup,
                [FromQuery] string? fileName,
                [FromServices] ModbotContext db,
                [FromServices] ImportSignal signal,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                if (Actor.Of(http) is not { } actor)
                    return Results.Unauthorized();

                // Multipart overhead on top of the file itself; the cap is enforced against
                // the file's own bytes below.
                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                    limit.MaxRequestBodySize = MaxBytes + 1024 * 1024;

                byte[]? body;
                var label = source;
                var seen = seenBy;
                var dry = dryRun ?? false;
                // On unless the upload says otherwise: the check exists because a file almost
                // always overlaps what Modbot recorded itself.
                var skipKnown = dedup ?? true;
                var name = fileName;

                if (http.Request.HasFormContentType)
                {
                    var form = await http.Request.ReadFormAsync(ct);
                    var file = form.Files.Count > 0 ? form.Files[0] : null;

                    if (file is null)
                        return Results.BadRequest(new { error = "The form has no file." });

                    label ??= form["source"].ToString();
                    seen ??= form["seenBy"].ToString();
                    name ??= form["fileName"].ToString();
                    if (string.IsNullOrEmpty(name))
                        name = file.FileName;

                    if (dryRun is null && bool.TryParse(form["dryRun"].ToString(), out var formDry))
                        dry = formDry;

                    if (dedup is null && bool.TryParse(form["dedup"].ToString(), out var formDedup))
                        skipKnown = formDedup;

                    await using var stream = file.OpenReadStream();
                    body = await ReadCappedAsync(stream, file.Length, ct);
                }
                else
                {
                    body = await ReadCappedAsync(http.Request.Body, http.Request.ContentLength, ct);
                }

                if (body is null)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

                label = label?.Trim() ?? string.Empty;
                if (label.Length == 0)
                    return Results.BadRequest(new { error = "An import needs a source: the name of the platform the file came from." });

                if (label.Length > Import.MaxSourceLength)
                    return Results.BadRequest(new { error = $"That source is longer than {Import.MaxSourceLength} characters." });

                if (body.Length == 0)
                    return Results.BadRequest(new { error = "The upload is empty." });

                if (!ImportSources.TryParse(seen, out var seenSource, out var seenProblem))
                    return Results.BadRequest(new { error = seenProblem });

                name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
                if (name is { Length: > Import.MaxFileNameLength })
                    name = name[..Import.MaxFileNameLength];

                var import = new Import
                {
                    Source = label,
                    FileName = name,
                    DryRun = dry,
                    Dedup = skipKnown,
                    SeenBy = seenSource,
                    StartedByUserId = actor.Id,
                    StartedByName = actor.Username,
                    CreatedAt = clock.UtcNow,
                    Body = body,
                };

                db.Imports.Add(import);
                await db.SaveChangesAsync(ct);
                signal.Pulse();

                return Results.Ok(ImportView.Of(import));
            })
            // No declared request body, as with the evidence transfer endpoint: the body is a
            // file, and the reference generator cannot draw a sample of one.
            .WithName("StartImport")
            .WithSummary("Start an import")
            .WithDescription(
                "The body is the file: a JSON array of records, or one record per line. Send it "
                + "as the body with Content-Type application/json and the source as a query "
                + "parameter, or as a multipart form with a file part and a source field. At most "
                + "64 MB. The import runs in the background; ask GET /api/imports/{id} how it is "
                + "going. dryRun=true reads and counts without writing anything. dedup=false "
                + "writes every record even when Modbot already has the event from somewhere "
                + "else; it does not turn off the check that stops the same file being imported "
                + "twice, which always runs. seenBy is the "
                + "source every record is filed under unless the record sets its own: one of "
                + "AuditLog, SyncDiff, Client, Discord, Manual or Modbot, and Manual when nothing "
                + "says otherwise. The format is documented at "
                + "docs.modbot.co/self-hosting/importing-old-data.")
            .Produces<ImportView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status413PayloadTooLarge);

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var imports = await db.Imports.AsNoTracking()
                    .OrderByDescending(i => i.CreatedAt)
                    .Take(ListLength)
                    .Select(i => new Import
                    {
                        Id = i.Id,
                        Source = i.Source,
                        FileName = i.FileName,
                        DryRun = i.DryRun,
                        Dedup = i.Dedup,
                        SeenBy = i.SeenBy,
                        Status = i.Status,
                        Received = i.Received,
                        Imported = i.Imported,
                        Skipped = i.Skipped,
                        AlreadyKnown = i.AlreadyKnown,
                        Rejected = i.Rejected,
                        Rejections = i.Rejections,
                        Error = i.Error,
                        StartedByUserId = i.StartedByUserId,
                        StartedByName = i.StartedByName,
                        CreatedAt = i.CreatedAt,
                        StartedAt = i.StartedAt,
                        FinishedAt = i.FinishedAt,
                    })
                    .ToListAsync(ct);

                return Results.Ok(new ImportsResponse(imports.Select(ImportView.Of).ToList()));
            })
            .WithName("ListImports")
            .WithSummary("List imports")
            .WithDescription("The latest fifty imports, newest first.")
            .Produces<ImportsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/{id:guid}", async (
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var import = await db.Imports.AsNoTracking()
                    .Where(i => i.Id == id)
                    .Select(i => new Import
                    {
                        Id = i.Id,
                        Source = i.Source,
                        FileName = i.FileName,
                        DryRun = i.DryRun,
                        Dedup = i.Dedup,
                        SeenBy = i.SeenBy,
                        Status = i.Status,
                        Received = i.Received,
                        Imported = i.Imported,
                        Skipped = i.Skipped,
                        AlreadyKnown = i.AlreadyKnown,
                        Rejected = i.Rejected,
                        Rejections = i.Rejections,
                        Error = i.Error,
                        StartedByUserId = i.StartedByUserId,
                        StartedByName = i.StartedByName,
                        CreatedAt = i.CreatedAt,
                        StartedAt = i.StartedAt,
                        FinishedAt = i.FinishedAt,
                    })
                    .SingleOrDefaultAsync(ct);

                return import is null
                    ? Results.NotFound(new { error = "There is no import with that id." })
                    : Results.Ok(ImportView.Of(import));
            })
            .WithName("GetImport")
            .WithSummary("Get import")
            .WithDescription(
                "One import: its status, counts and rejection reasons. "
                + "Counts move while the import runs, so asking again shows progress.")
            .Produces<ImportView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>
    /// Reads the stream in full, up to the cap. False when there is more than the cap allows:
    /// the cap is enforced against bytes actually seen, not against Content-Length.
    /// </summary>
    /// <summary>
    /// The whole body, or null when it is over <see cref="MaxBytes"/>.
    /// </summary>
    /// <param name="declared">
    /// How long the sender said it would be, or null when nobody said. A hint and not a promise:
    /// it only decides the starting size, so a body that turns out longer still grows and one that
    /// claims to be enormous is still stopped at the cap.
    /// </param>
    /// <remarks>
    /// <para>
    /// Sized from the declared length because the alternative is expensive twice over. A
    /// <see cref="MemoryStream"/> given no capacity doubles its way up -- 256 bytes, 512, and on
    /// to sixty-four megabytes -- allocating a new array and copying everything so far at each
    /// step, and every array past eighty-five kilobytes lands on the large object heap, which is
    /// not compacted. Starting at the right size makes it one allocation.
    /// </para>
    /// <para>
    /// And the buffer is handed back rather than copied out when it came out exactly full, which
    /// is the ordinary case once the size is right. <see cref="MemoryStream.ToArray"/> allocates a
    /// second copy of everything just read, so a sixty-four megabyte import held a hundred and
    /// twenty-eight at the moment it returned -- on the machine least able to spare it, with
    /// PostgreSQL about to take the same sixty-four again. A buffer that is not exactly full still
    /// gets copied, because the caller is handed the array itself and trailing zeroes would become
    /// part of the file.
    /// </para>
    /// </remarks>
    private static async Task<byte[]?> ReadCappedAsync(Stream stream, long? declared, CancellationToken ct)
    {
        var buffer = declared is > 0 and <= MaxBytes
            ? new MemoryStream((int)declared.Value)
            : new MemoryStream();

        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0)
                break;

            if (buffer.Length + read > MaxBytes)
                return null;

            buffer.Write(chunk, 0, read);
        }

        var held = buffer.GetBuffer();
        return held.Length == buffer.Length ? held : buffer.ToArray();
    }
}
