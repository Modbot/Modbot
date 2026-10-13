using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.VRChat;
using Modbot.VRChat.Files;
using Modbot.VRChat.GroupPage;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.GroupPage;

/// <summary>One picture waiting for staff, from the queue on the Gallery page.</summary>
/// <param name="AuthorId">The member's Discord id.</param>
/// <param name="MessageUrl">Opens the member's message in Discord.</param>
/// <param name="PictureUrl">Where the picture itself is served from, on this server.</param>
public sealed record GallerySubmissionRow(
    Guid Id,
    string AuthorId,
    string AuthorName,
    string FileName,
    string MessageUrl,
    DateTimeOffset SubmittedAt,
    string PictureUrl);

/// <summary>The pictures members posted in Discord that nobody has dealt with yet, oldest first.</summary>
public sealed record GallerySubmissionList(IReadOnlyList<GallerySubmissionRow> Submissions);

/// <summary>The gallery staff chose for a picture.</summary>
public sealed record GallerySubmissionAdd(string GalleryId);

/// <summary>
/// The queue of pictures members posted in Discord for the group gallery: listing it, showing a
/// picture, adding one to a gallery on VRChat and declining one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Adding is two requests to VRChat, once, on a press.</strong> The picture is uploaded
/// (<see cref="VRChatPictureUploads"/>, one a minute) and then added to the chosen gallery
/// (<see cref="GroupGalleries.AddAsync"/>, sharing removal's lane). Neither is retried (foundation
/// 4.3.1). The upload's file id is kept as soon as VRChat answers, so pressing again after the
/// second request failed does not upload the picture a second time.
/// </para>
/// <para>
/// It follows the same switches as every other upload: Settings → VRChat → picture uploads must be
/// on, and an account known to have no VRChat+ is not asked. Nothing is recorded in the audit log
/// unless VRChat accepted the picture. A decline asks VRChat nothing and tells nobody; the picture
/// is deleted from Modbot's database with it.
/// </para>
/// </remarks>
public static class GroupGallerySubmissionEndpoints
{
    /// <summary>The emoji Modbot puts on a member's message once their picture is in the gallery.</summary>
    public const string AddedReaction = "✅";

    /// <summary>
    /// The pictures being added right now, so a second press on the same picture is turned away
    /// instead of uploading it twice. One server, so memory is the right place.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, byte> Working = new();

    public static IEndpointRouteBuilder MapGroupGallerySubmissions(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/group/gallery/submissions").WithTags("Group page").RequireAuthorization();

        group.MapGet("/", async ([FromServices] ModbotContext db, CancellationToken ct) =>
            {
                var rows = await db.GallerySubmissions.AsNoTracking()
                    .Where(s => s.Status == GallerySubmissionStatus.Waiting)
                    .OrderBy(s => s.SubmittedAt)
                    .Select(s => new
                    {
                        s.Id,
                        s.AuthorDiscordId,
                        s.AuthorName,
                        s.FileName,
                        s.MessageUrl,
                        s.SubmittedAt,
                    })
                    .ToListAsync(ct);

                return Results.Ok(new GallerySubmissionList(
                    rows.Select(r => new GallerySubmissionRow(
                            r.Id, r.AuthorDiscordId, r.AuthorName, r.FileName, r.MessageUrl, r.SubmittedAt,
                            $"/api/group/gallery/submissions/{r.Id}/picture"))
                        .ToList()));
            })
            .RequiresFlag(ModbotPermissions.ViewAnalytics)
            .WithName("GetGroupGallerySubmissions")
            .WithSummary("List gallery pictures waiting for staff")
            .WithDescription(
                "The pictures members posted in the Discord gallery channel that nobody has added or "
                + "declined yet, oldest first. Read from Modbot's own database; nothing is asked of "
                + "VRChat or Discord.")
            .Produces<GallerySubmissionList>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/{id:guid}/picture", async (Guid id, [FromServices] ModbotContext db, CancellationToken ct) =>
            {
                var picture = await db.GallerySubmissions.AsNoTracking()
                    .Where(s => s.Id == id && s.Status == GallerySubmissionStatus.Waiting)
                    .Select(s => new { s.Bytes, s.ContentType })
                    .FirstOrDefaultAsync(ct);

                return picture is null ? Results.NotFound() : Results.File(picture.Bytes, picture.ContentType);
            })
            .RequiresFlag(ModbotPermissions.ViewAnalytics)
            .WithName("GetGroupGallerySubmissionPicture")
            .WithSummary("Get a waiting gallery picture")
            .WithDescription("The picture itself, as a PNG or JPEG. Only while it is waiting: once it is added or declined the copy is deleted.")
            .Produces(StatusCodes.Status200OK, contentType: "image/png")
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/add", async (
                Guid id,
                HttpContext http,
                [FromBody] GallerySubmissionAdd body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupGalleries? galleries,
                [FromServices] VRChatPictureUploads? uploads,
                [FromServices] IDiscordMessageReactions reactions,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (ModbotAuth.UserIdOf(http.User) is not { } actor)
                    return Results.Forbid();

                if (galleries is null || uploads is null || facts is null || partitions is null)
                    return GroupPageAnswers.NotSetUp();

                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

                if (settings?.ManagedGroupId is not { Length: > 0 } groupId)
                    return GroupPageAnswers.NoGroup();

                if (string.IsNullOrWhiteSpace(body.GalleryId))
                    return GroupPageAnswers.Invalid("Choose a gallery.");

                if (!Working.TryAdd(id, 0))
                    return Conflict("That picture is already being added.");

                try
                {
                    var submission = await db.GallerySubmissions.FirstOrDefaultAsync(s => s.Id == id, ct);

                    if (submission is null)
                        return Results.NotFound();

                    if (submission.Status != GallerySubmissionStatus.Waiting)
                        return Conflict("Somebody has already dealt with that picture.");

                    // Uploaded already by an earlier press that VRChat then refused at the gallery:
                    // the file is there, so only the gallery is asked.
                    if (submission.VRChatFileId is null)
                    {
                        if (!settings.VRChatPictureUploads)
                            return Conflict("Picture uploads are off.");

                        if (settings.VRChatHasPlus == false)
                            return Conflict(VRChatPictureUploads.NeedsPlus);

                        if (VRChatPictureUploads.Problem(submission.Bytes) is { } problem)
                            return GroupPageAnswers.Invalid(problem);

                        var uploaded = await uploads.UploadAsync(submission.Bytes, ct);

                        if (!uploaded.Success)
                        {
                            // Never sent again from here (foundation 4.3.1): pressing again is a person's decision.
                            var said = uploaded.IsRateLimited || uploaded.Kind == VRChatFailureKind.RateLimited
                                ? "VRChat is not taking uploads right now. Try again in a few minutes."
                                : GroupPageAnswers.Said(uploaded);

                            return Results.Json(new { error = said }, statusCode: GroupPageAnswers.StatusFor(uploaded));
                        }

                        if (uploaded.Value?.Id is not { Length: > 0 } fileId)
                        {
                            return Results.Json(
                                new { error = "VRChat did not give the picture an id." },
                                statusCode: StatusCodes.Status502BadGateway);
                        }

                        submission.VRChatFileId = fileId;
                        await db.SaveChangesAsync(ct);
                    }

                    var added = await galleries.AddAsync(groupId, body.GalleryId, submission.VRChatFileId!, ct);

                    if (!added.Success)
                        return GroupPageAnswers.Refused(added, "AddGroupGalleryImage", groupId, settings);

                    var gallery = GroupGallerySnapshot.Parse(settings.ManagedGroupGalleries)
                        .FirstOrDefault(g => string.Equals(g.Id, body.GalleryId, StringComparison.Ordinal));

                    var now = clock.UtcNow;

                    await using (var transaction = await db.Database.BeginTransactionAsync(ct))
                    {
                        submission.Status = GallerySubmissionStatus.Added;
                        submission.GalleryId = body.GalleryId;
                        submission.VRChatImageId = added.Value?.Id;
                        submission.DecidedAt = now;
                        submission.DecidedByUserId = actor;
                        submission.Bytes = [];

                        await GroupPageAnswers.WriteFactAsync(
                            facts, partitions, FactType.GroupGalleryImageAdded, groupId, actor, now,
                            new JsonObject
                            {
                                ["galleryId"] = body.GalleryId,
                                ["galleryName"] = gallery?.Name,
                                ["imageId"] = added.Value?.Id,
                                ["submittedBy"] = submission.AuthorDiscordId,
                                ["submittedByName"] = submission.AuthorName,
                            },
                            ct);

                        await db.SaveChangesAsync(ct);
                        await transaction.CommitAsync(ct);
                    }

                    // A courtesy to the member; the picture is in the gallery whether or not it lands.
                    await reactions.AddAsync(submission.DiscordChannelId, submission.DiscordMessageId, AddedReaction, ct);

                    return Results.NoContent();
                }
                finally
                {
                    Working.TryRemove(id, out _);
                }
            })
            .RequiresFlag(ModbotPermissions.ManageGroupGallery)
            .WithName("AddGroupGallerySubmission")
            .WithSummary("Add a waiting picture to a gallery")
            .WithDescription(
                "Upload a waiting picture to VRChat and add it to one of the group's galleries. "
                + "`galleryId` chooses the gallery. Two requests to VRChat, never retried; if the "
                + "gallery refuses, the upload is kept so pressing again does not upload twice. "
                + "Needs picture uploads switched on in Settings and an account with VRChat+. Once "
                + "VRChat accepts, the audit log records who added it, the copy in Modbot is "
                + "deleted and the member's Discord message gets a tick.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/{id:guid}/decline", async (
                Guid id,
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                if (ModbotAuth.UserIdOf(http.User) is not { } actor)
                    return Results.Forbid();

                if (Working.ContainsKey(id))
                    return Conflict("That picture is being added right now.");

                var submission = await db.GallerySubmissions.FirstOrDefaultAsync(s => s.Id == id, ct);

                if (submission is null)
                    return Results.NotFound();

                if (submission.Status != GallerySubmissionStatus.Waiting)
                    return Conflict("Somebody has already dealt with that picture.");

                submission.Status = GallerySubmissionStatus.Declined;
                submission.DecidedAt = clock.UtcNow;
                submission.DecidedByUserId = actor;
                submission.Bytes = [];

                await db.SaveChangesAsync(ct);

                return Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.ManageGroupGallery)
            .WithName("DeclineGroupGallerySubmission")
            .WithSummary("Decline a waiting picture")
            .WithDescription("Take a waiting picture out of the queue. Nothing is sent to VRChat and the member is told nothing; Modbot's copy of the picture is deleted.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    private static IResult Conflict(string message)
        => Results.Json(new { error = message }, statusCode: StatusCodes.Status409Conflict);
}
