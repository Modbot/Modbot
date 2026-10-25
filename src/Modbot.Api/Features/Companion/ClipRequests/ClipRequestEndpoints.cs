using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Companion.Devices;
using Modbot.Api.Features.Evidence;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Upload;
using Modbot.Shared.Clips;

namespace Modbot.Api.Features.Companion.ClipRequests;

/// <summary>
/// How a companion answers a clip request: the file itself, or a word when it has none.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One answer, from the device that was asked, inside 15 minutes.</strong>
/// <see cref="ClipRequestStore.OpenAsync"/> says whether the request is still open for this device
/// and every refusal is the same 404, so a wrong device, a wrong id, an answer already taken and a
/// request past its time cannot be told apart. A refused or failed upload leaves the request as it
/// was: the companion may send again while the 15 minutes last.
/// </para>
/// <para>
/// <strong>The once-only gate comes before anything is attached.</strong> The file is stored first,
/// because the request has to name the stored file it was answered with; then
/// <see cref="ClipRequestStore.AnswerAsync"/> closes the request in one conditional update. Only the
/// upload that wins it puts the clip on a case file or marks it as a clip. An upload that loses a
/// race with another for the same request leaves a stored file that nothing refers to, and is
/// answered like any other refusal.
/// </para>
/// </remarks>
public static class ClipRequestEndpoints
{
    public const string NotFound = "clip_request_not_found";

    public const string Invalid = "clip_answer_invalid";

    public static async Task<IResult> UploadAsync(
        int apiVersion,
        Guid requestId,
        HttpContext context,
        DeviceAuthenticator authenticator,
        [FromServices] ClipRequestStore store,
        ModbotContext database,
        [FromServices] EvidenceUploadService uploads,
        [FromServices] EvidenceAttachments attachments,
        [FromServices] EvidenceOptions options,
        IModbotClock clock,
        CancellationToken ct)
    {
        if (!CompanionApiVersion.IsSupported(apiVersion))
            return CompanionApiErrors.VersionUnsupported(apiVersion);

        var authentication = await authenticator.AuthenticateAsync(context, ct);
        if (!authentication.Succeeded)
            return authentication.Failure!;

        var device = authentication.Device!;

        // Kestrel's default body limit is far below the evidence cap, so it is raised here, for this
        // request, to the cap the operator configured and no further.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = options.MaxFileBytes;

        var request = await store.OpenAsync(requestId, device.Id, clock.UtcNow, ct);
        if (request is null)
            return Refused();

        var owner = await database.Users.AsNoTracking()
            .Where(u => u.Id == device.IssuedToUserId)
            .Select(u => new { u.Id, u.Username, u.VRChatUserId })
            .FirstOrDefaultAsync(ct);
        if (owner is null)
            return Refused();

        var caseFile = request.Action == ClipAction.Ban
            ? await BansCaseFileAsync(database, request, ct)
            : null;

        CommitResult stored;
        try
        {
            var ticket = await uploads.BeginAsync(
                new BeginUploadRequest(
                    FileName: $"clip-{requestId:N}.mp4",
                    DeclaredLength: context.Request.ContentLength,
                    ReportId: caseFile?.Id.ToString(),
                    UploaderId: owner.Username),
                ct);

            await uploads.ReceiveAsync(ticket.UploadId, context.Request.Body, context.Request.ContentLength, ct);
            stored = await uploads.CommitAsync(ticket.UploadId, null, caseFile?.Id.ToString(), ct);
        }
        catch (Exception e) when (e is EvidenceTooLargeException
            or EvidenceRejectedException
            or EvidenceStoreUnavailableException
            or EvidenceStagingNotFoundException)
        {
            return Failure(e);
        }

        // The gate. Another upload, an answer, or the end of the 15 minutes may have closed the
        // request while this one was being stored; then nothing is attached and the answer is a 404.
        var now = clock.UtcNow;
        if (!await store.AnswerAsync(requestId, device.Id, ClipStatus.Attached, stored.Hash.Hex, now, ct))
            return Refused();

        var clip = new SavedClip(
            Id: 0,
            Hash: stored.Hash.Hex,
            Bytes: stored.ByteSize,
            WorldId: request.WorldId ?? string.Empty,
            InstanceId: request.InstanceId ?? string.Empty,
            SavedAt: now,
            SavedById: owner.VRChatUserId ?? string.Empty,
            SavedByUserId: owner.Id,
            SavedBy: owner.Username,
            DeviceId: device.Id);

        if (caseFile is not null)
            await attachments.AttachAsync(caseFile, stored, new Actor(owner.Id, owner.Username), ct, clip);
        else
            await attachments.KeepAsClipAsync(stored, clip, ct);

        return Results.NoContent();
    }

    public static async Task<IResult> AnswerAsync(
        int apiVersion,
        Guid requestId,
        ClipRequestAnswer? body,
        HttpContext context,
        DeviceAuthenticator authenticator,
        [FromServices] ClipRequestStore store,
        IModbotClock clock,
        CancellationToken ct)
    {
        if (!CompanionApiVersion.IsSupported(apiVersion))
            return CompanionApiErrors.VersionUnsupported(apiVersion);

        var authentication = await authenticator.AuthenticateAsync(context, ct);
        if (!authentication.Succeeded)
            return authentication.Failure!;

        var outcome = body?.Outcome switch
        {
            ClipRequestWire.ClipsOff => ClipStatus.ClipsOff,
            ClipRequestWire.CouldNotCut => ClipStatus.CouldNotCut,
            ClipRequestWire.UploadFailed => ClipStatus.UploadFailed,
            _ => (ClipStatus?)null,
        };

        if (outcome is not { } status)
            return Results.BadRequest(new CompanionError(Invalid, "That is not an answer a companion can give."));

        return await store.AnswerAsync(requestId, authentication.Device!.Id, status, null, clock.UtcNow, ct)
            ? Results.NoContent()
            : Refused();
    }

    /// <summary>
    /// The ban's own case file: not withdrawn, for the banned person, and either written for this
    /// ban's fact or for a ban at the moment of the action. None when it is not written yet.
    /// </summary>
    private static Task<CaseFile?> BansCaseFileAsync(ModbotContext database, ClipRequest request, CancellationToken ct)
    {
        var from = request.ActionAt - LinkedActions.Window;
        var to = request.ActionAt + LinkedActions.Window;

        return database.CaseFiles
            .Where(c => c.WithdrawnAt == null
                && c.UserId == request.SubjectId
                && (c.BanFactId == request.FactId || (c.BannedAt >= from && c.BannedAt <= to)))
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct)!;
    }

    private static IResult Refused()
        => Results.Json(
            new CompanionError(NotFound, "There is no such clip request to answer."),
            statusCode: StatusCodes.Status404NotFound);

    private static IResult Failure(Exception e) => e switch
    {
        EvidenceTooLargeException tooLarge => Results.Json(
            new CompanionError("clip_too_large", tooLarge.Message),
            statusCode: StatusCodes.Status413PayloadTooLarge),

        EvidenceRejectedException rejected => Results.Json(
            new CompanionError("clip_rejected", rejected.Message),
            statusCode: StatusCodes.Status415UnsupportedMediaType),

        _ => Results.Json(
            new CompanionError("evidence_store_unavailable", e.Message),
            statusCode: StatusCodes.Status503ServiceUnavailable),
    };
}
