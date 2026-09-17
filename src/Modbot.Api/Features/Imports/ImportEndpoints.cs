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
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPost("/", async (
                HttpContext http,
                [FromQuery] string? source,
                [FromQuery] bool? dryRun,
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
                var dry = dryRun ?? false;
                var name = fileName;

                if (http.Request.HasFormContentType)
                {
                    var form = await http.Request.ReadFormAsync(ct);
                    var file = form.Files.Count > 0 ? form.Files[0] : null;

                    if (file is null)
                        return Results.BadRequest(new { error = "The form has no file." });

                    label ??= form["source"].ToString();
                    name ??= form["fileName"].ToString();
                    if (string.IsNullOrEmpty(name))
                        name = file.FileName;

                    if (dryRun is null && bool.TryParse(form["dryRun"].ToString(), out var formDry))
                        dry = formDry;

                    await using var stream = file.OpenReadStream();
                    body = await ReadCappedAsync(stream, ct);
                }
                else
                {
                    body = await ReadCappedAsync(http.Request.Body, ct);
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

                name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
                if (name is { Length: > Import.MaxFileNameLength })
                    name = name[..Import.MaxFileNameLength];

                var import = new Import
                {
                    Source = label,
                    FileName = name,
                    DryRun = dry,
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
            .WithSummary("Upload old data and start importing it")
            .WithDescription(
                "The body is the file: a JSON array of records, or one record per line. Send it "
                + "as the body with Content-Type application/json and the source as a query "
                + "parameter, or as a multipart form with a file part and a source field. At most "
                + "64 MB. The import runs in the background; ask GET /api/imports/{id} how it is "
                + "going. dryRun=true reads and counts without writing anything. The format is "
                + "documented at docs.modbot.co/self-hosting/importing-old-data.")
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
                        Status = i.Status,
                        Received = i.Received,
                        Imported = i.Imported,
                        Skipped = i.Skipped,
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
            .WithSummary("The latest fifty imports, newest first")
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
                        Status = i.Status,
                        Received = i.Received,
                        Imported = i.Imported,
                        Skipped = i.Skipped,
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
            .WithSummary("One import: its status, counts and rejection reasons")
            .WithDescription("Counts move while the import runs, so asking again shows progress.")
            .Produces<ImportView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>
    /// Reads the stream in full, up to the cap. False when there is more than the cap allows:
    /// the cap is enforced against bytes actually seen, not against Content-Length.
    /// </summary>
    private static async Task<byte[]?> ReadCappedAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();

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

        return buffer.ToArray();
    }
}
