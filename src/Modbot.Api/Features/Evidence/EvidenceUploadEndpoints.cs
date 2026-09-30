using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Api.Features.Cases;
using Modbot.Api.Features.Users;
using Modbot.Core.Data.Entities;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Upload;

namespace Modbot.Api.Features.Evidence;

/// <summary>
/// The three phases of design §9.1: begin, transfer, commit — the same on every backend.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing is attached to a report until commit completes.</strong> A moderator who closes
/// the tab mid-upload leaves a staging object and nothing else; there is never half a piece of
/// evidence on a case file.
/// </para>
/// <para>
/// <strong>No multipart form encoding.</strong> The metadata goes in phase 1 as JSON and the bytes
/// go in phase 2 as a raw body. Multipart parsing on a hundred-megabyte request is a parser, a
/// buffer-to-disk default and a class of bug in exchange for nothing — and it would not work for
/// the presigned path at all, which would leave two transfer paths instead of one.
/// </para>
/// <para>
/// <strong>The body limit is raised on the transfer endpoint only.</strong> A hundred-megabyte
/// body limit on the JSON API is a denial-of-service surface for no benefit, so it is lifted per
/// request, to the cap in force, and nowhere else.
/// </para>
/// </remarks>
public static class EvidenceUploadEndpoints
{
    internal static IEndpointRouteBuilder MapEvidenceUploads(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/evidence/uploads")
            .WithTags("Evidence")
            .RequireAuthorization();

        group.MapPost("/", async (
                HttpContext http,
                EvidenceBeginRequest body,
                [FromServices] EvidenceUploadService uploads,
                [FromServices] EvidenceAttachments attachments,
                CancellationToken ct) =>
            {
                var uploader = ModbotAuthActor(http);

                try
                {
                    string? canonicalCase = null;

                    // An upload begun for a case file is checked against the same rule as its
                    // commit, before anything is measured: the size checks below say what the case
                    // file holds, and that must not be told to somebody who may not change it.
                    if (body.ReportId is { Length: > 0 } reportId)
                    {
                        if (CaseFileEndpoints.CallerOf(http) is not { } caller)
                            return Results.Forbid();

                        // The case file's own spelling of its id goes on, not what was typed, so a
                        // padded or capitalised id cannot pass here and then fail at commit.
                        canonicalCase = (await attachments.RequireEditableAsync(reportId.Trim(), caller, ct)).Id.ToString();
                    }

                    var ticket = await uploads.BeginAsync(
                        new BeginUploadRequest(
                            body.FileName,
                            body.ContentType,
                            body.Length,
                            canonicalCase,
                            uploader),
                        ct);

                    return Results.Ok(new EvidenceUploadTicketView(
                        ticket.UploadId.Value,
                        ticket.MaxBytes,
                        ticket.AcceptedTypes,
                        ticket.PresignedTarget?.ToString() ?? $"/api/evidence/uploads/{ticket.UploadId.Value}",
                        ticket.PresignedTarget is not null));
                }
                catch (Exception e) when (IsExpected(e))
                {
                    return Failure(e);
                }
            })
            .RequiresFlag(ModbotPermissions.UploadEvidence)
            .WithName("BeginEvidenceUpload")
            .WithSummary("Start an upload")
            .WithDescription(
                "Phase 1: reserve an upload and find out where the bytes go. "
                + "Returns the cap in force, the accepted formats so the client can filter before a "
                + "byte moves, and a target. On a store that can be written to directly the target "
                + "is a presigned PUT straight to the bucket; otherwise it is Modbot's own transfer "
                + "endpoint. That is a capability difference, not a failure.")
            .Produces<EvidenceUploadTicketView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPut("/{uploadId}", async (
                HttpContext http,
                string uploadId,
                [FromServices] EvidenceUploadService uploads,
                [FromServices] EvidenceOptions options,
                CancellationToken ct) =>
            {
                if (!EvidenceUploadId.TryParse(uploadId, out var id))
                    return Results.NotFound(new { error = "That is not an upload id." });

                // Kestrel's default body limit is far below the evidence cap, so it is raised here
                // — on this endpoint, for this request, to the number the operator configured.
                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                    limit.MaxRequestBodySize = options.MaxFileBytes;

                try
                {
                    var staged = await uploads.ReceiveAsync(
                        id, http.Request.Body, http.Request.ContentLength, ct);

                    return Results.Ok(new EvidenceStagedView(staged.Hash.Hex, staged.ByteSize));
                }
                catch (Exception e) when (IsExpected(e))
                {
                    return Failure(e);
                }
            })
            .RequiresFlag(ModbotPermissions.UploadEvidence)
            .WithSummary("Send the bytes")
            .WithName("TransferEvidence")
            .WithDescription(
                "Phase 2: send the bytes, as a raw body. "
                + "The body is the file and nothing else — no form encoding. The cap is enforced "
                + "against bytes actually seen rather than against Content-Length, because "
                + "Content-Length is a claim and a chunked body makes none. Retrying the same "
                + "upload id overwrites its staging object rather than accumulating a second one.")
            .Produces<EvidenceStagedView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/{uploadId}/commit", async (
                HttpContext http,
                string uploadId,
                EvidenceCommitRequest body,
                [FromServices] EvidenceUploadService uploads,
                [FromServices] IEvidenceUploadRegistry registry,
                [FromServices] EvidenceAttachments attachments,
                CancellationToken ct) =>
            {
                if (!EvidenceUploadId.TryParse(uploadId, out var id))
                    return Results.NotFound(new { error = "That is not an upload id." });

                if (CaseFileEndpoints.CallerOf(http) is not { } caller || Actor.Of(http) is not { } actor)
                    return Results.Forbid();

                EvidenceHash? expected = null;
                if (body.ExpectedHash is { Length: > 0 } claimed)
                {
                    if (!EvidenceHash.TryParse(claimed, out var parsed))
                    {
                        return Results.BadRequest(new
                        {
                            error = "expectedHash must be 64 lowercase hex characters, or absent.",
                        });
                    }

                    expected = parsed;
                }

                try
                {
                    // The case file is checked before anything is promoted or recorded: it has to
                    // exist, not be withdrawn, and be the caller's to change. A file with no case
                    // file to go on is refused rather than stored and forgotten. An upload that is
                    // not in flight falls through to the pipeline, which says so.
                    var begunFor = (await registry.FindAsync(id, ct))?.ReportId;
                    var caseFile = await attachments.RequireEditableAsync(
                        body.ReportId is { Length: > 0 } ? body.ReportId.Trim() : begunFor,
                        caller,
                        ct);

                    var result = await uploads.CommitAsync(id, expected, caseFile.Id.ToString(), ct);

                    // Put on the case file only now that the bytes are safe in the store, in one
                    // step with the fact that says who did it. Retrying a commit that already
                    // worked puts on nothing twice.
                    await attachments.AttachAsync(caseFile, result, actor, ct);

                    // The outcome — whether these bytes were already in the store — is deliberately
                    // not returned. Telling a moderator "you have already uploaded this file" tells
                    // them something about a case file they may have no right to see.
                    return Results.Ok(new EvidenceCommitResponse(
                        result.Hash.Hex, result.ByteSize, result.ContentType));
                }
                catch (Exception e) when (IsExpected(e))
                {
                    return Failure(e);
                }
            })
            .RequiresFlag(ModbotPermissions.UploadEvidence)
            .WithName("CommitEvidenceUpload")
            .WithSummary("Finish the upload")
            .WithDescription(
                "Phase 3: hash it, decide what it is, and attach it. "
                + "Modbot reads the staged bytes back, hashes them, decides the content type from "
                + "the bytes themselves — never from the filename and never from what the client "
                + "claimed — checks the size that was actually stored against the per-file limit "
                + "and the per-case-file and whole-install totals, promotes the object to its "
                + "content-addressed key, writes the metadata and puts the file on the case file. "
                + "SVG and HTML are refused outright. Idempotent on the upload id. "
                + "reportId names the case file to attach to when the upload was begun without "
                + "one, so a file can be sent before its case file is written; naming a different "
                + "case file from the one the upload was begun for is refused with 409. The case "
                + "file must exist (404), must not be withdrawn (409), and must be the caller's own "
                + "or the caller must be allowed to ban (403); with no case file at all the commit "
                + "is refused with 400. Recorded as a fact against your account.")
            .Produces<EvidenceCommitResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static string? ModbotAuthActor(HttpContext http) => http.User.Identity?.Name;

    /// <summary>
    /// Turns the pipeline's exceptions into answers a moderator can act on.
    /// </summary>
    /// <remarks>
    /// The store being unavailable is a 503 rather than a 500, and it says so in words: accepting
    /// an upload into a store that has just demonstrated it loses everything is worse than refusing
    /// it, and an operator who sees "server error" goes looking in the wrong place.
    /// </remarks>
    private static IResult Failure(Exception e) => e switch
    {
        CaseFileRefused refused => CaseFileEndpoints.Refusal(refused),

        EvidenceTooLargeException tooLarge => Results.Json(
            new { error = tooLarge.Message },
            statusCode: StatusCodes.Status413PayloadTooLarge),

        EvidenceRejectedException rejected => Results.Json(
            new { error = rejected.Message },
            statusCode: StatusCodes.Status415UnsupportedMediaType),

        EvidenceStagingNotFoundException missing => Results.NotFound(new { error = missing.Message }),

        EvidenceReportMismatchException mismatch => Results.Conflict(new { error = mismatch.Message }),

        EvidenceStoreUnavailableException unavailable => Results.Json(
            new { error = unavailable.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable),

        // Unreachable: the catch filters admit only the six above. Kept total so that adding a
        // seventh cannot silently become a 500 with no message.
        _ => Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status500InternalServerError),
    };

    private static bool IsExpected(Exception e)
        => e is CaseFileRefused
            or EvidenceTooLargeException
            or EvidenceRejectedException
            or EvidenceStagingNotFoundException
            or EvidenceReportMismatchException
            or EvidenceStoreUnavailableException;
}
