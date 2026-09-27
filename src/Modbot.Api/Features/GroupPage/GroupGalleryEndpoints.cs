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
using Modbot.Core.Time;
using Modbot.VRChat.GroupPage;
using Modbot.VRChat.Sync;
using VRChat.API.Model;

namespace Modbot.Api.Features.GroupPage;

/// <summary>
/// The group's galleries on VRChat, from the VRChat page's Gallery tab: the galleries, one
/// gallery's images, and removing an image.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One request per look, never polled.</strong> The galleries themselves come from the
/// group poll Modbot already makes, so listing them costs nothing; one request reads the chosen
/// gallery's images when the tab opens, when another gallery is chosen, per page and per
/// Refresh. A group with no galleries sends nothing at all.
/// </para>
/// <para>
/// <strong>A removal is one request, and nothing is recorded unless VRChat accepted it.</strong>
/// The ids travel in the body (foundation §3.1.1). Adding an image is not here: it needs VRChat's
/// file upload, which Modbot does not do yet.
/// </para>
/// </remarks>
public static class GroupGalleryEndpoints
{
    public static IEndpointRouteBuilder MapGroupGallery(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/group/gallery").WithTags("Group page").RequireAuthorization();

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupGalleries? vrchat,
                [FromQuery] string? galleryId,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                CancellationToken ct) =>
            {
                if (vrchat is null)
                    return GroupPageAnswers.NotSetUp();

                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

                if (settings?.ManagedGroupId is not { Length: > 0 } groupId)
                    return GroupPageAnswers.NoGroup();

                var galleries = GroupGallerySnapshot.Parse(settings.ManagedGroupGalleries)
                    .Select(g => new GroupGalleryChoice(g.Id, g.Name, g.Description, g.MembersOnly))
                    .ToList();

                var number = Math.Max(1, page ?? 1);
                var size = Math.Clamp(pageSize ?? GroupPageRules.GalleryPageSize, 1, GroupGalleries.MaxPageSize);

                // The one asked for, or the first. A gallery the poll has not seen is still
                // asked about: VRChat is the judge of whether it exists, not the stored list.
                var chosen = !string.IsNullOrWhiteSpace(galleryId) ? galleryId : galleries.FirstOrDefault()?.Id;

                if (chosen is null)
                    return Results.Ok(new GroupGalleryPage(galleries, null, [], number, size, false, clock.UtcNow));

                var answer = await vrchat.ListAsync(groupId, chosen, size, (number - 1) * size, ct);

                if (!answer.Success)
                    return GroupPageAnswers.Refused(answer, "GetGroupGalleryImages", groupId, settings);

                var images = answer.Value ?? [];
                var names = await SubmitterNamesAsync(db, images, ct);

                return Results.Ok(new GroupGalleryPage(
                    galleries,
                    chosen,
                    images
                        .Select(i => new GroupGalleryImageRow(
                            i.Id,
                            string.IsNullOrWhiteSpace(i.ImageUrl) ? null : i.ImageUrl,
                            i.Approved,
                            string.IsNullOrWhiteSpace(i.SubmittedByUserId) ? null : i.SubmittedByUserId,
                            i.SubmittedByUserId is { } by && names.TryGetValue(by, out var name) ? name : null,
                            i.CreatedAt == default ? null : AuditLogEntryMapper.ReadTimestamp(i.CreatedAt)))
                        .OrderByDescending(i => i.CreatedAt ?? DateTimeOffset.MinValue)
                        .ToList(),
                    number,
                    size,
                    images.Count >= size,
                    clock.UtcNow));
            })
            .RequiresFlag(ModbotPermissions.ViewAnalytics)
            .WithName("GetGroupGallery")
            .WithSummary("List group gallery images")
            .WithDescription(
                "The group's galleries, and one page of a gallery's images read from VRChat when "
                + "you ask, newest first. `galleryId` chooses the gallery; left out, the first one. "
                + "The galleries come from the group poll Modbot already makes, so only the images "
                + "cost a request, and a group with no galleries costs none. `page` and `pageSize` "
                + "page the images (at most 100 a page); `hasMore` is true when the page came back "
                + "full. Images still waiting for approval are included, with `approved` false.")
            .Produces<GroupGalleryPage>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/remove", async (
                HttpContext http,
                [FromBody] GroupGalleryImageRemove body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] GroupGalleries? vrchat,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (ModbotAuth.UserIdOf(http.User) is not { } actor)
                    return Results.Forbid();

                if (vrchat is null || facts is null || partitions is null)
                    return GroupPageAnswers.NotSetUp();

                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

                if (settings?.ManagedGroupId is not { Length: > 0 } groupId)
                    return GroupPageAnswers.NoGroup();

                if (string.IsNullOrWhiteSpace(body.GalleryId) || string.IsNullOrWhiteSpace(body.ImageId))
                    return GroupPageAnswers.Invalid("Say which image to remove, and from which gallery.");

                var answer = await vrchat.RemoveAsync(groupId, body.GalleryId, body.ImageId, ct);

                if (!answer.Success)
                {
                    return answer.StatusCode == StatusCodes.Status404NotFound
                        ? Results.Json(
                            new { error = "That image is no longer there. Somebody may have removed it in VRChat." },
                            statusCode: StatusCodes.Status404NotFound)
                        : GroupPageAnswers.Refused(answer, "DeleteGroupGalleryImage", groupId, settings);
                }

                var gallery = GroupGallerySnapshot.Parse(settings.ManagedGroupGalleries)
                    .FirstOrDefault(g => string.Equals(g.Id, body.GalleryId, StringComparison.Ordinal));

                var now = clock.UtcNow;

                await using (var transaction = await db.Database.BeginTransactionAsync(ct))
                {
                    await GroupPageAnswers.WriteFactAsync(
                        facts, partitions, FactType.GroupGalleryImageRemoved, groupId, actor, now,
                        new JsonObject
                        {
                            ["galleryId"] = body.GalleryId,
                            ["galleryName"] = gallery?.Name,
                            ["imageId"] = body.ImageId,
                            ["submittedBy"] = string.IsNullOrWhiteSpace(body.SubmittedById) ? null : body.SubmittedById,
                        },
                        ct);

                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                }

                return Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.ManageGroupGallery)
            .WithName("RemoveGroupGalleryImage")
            .WithSummary("Remove group gallery image")
            .WithDescription(
                "Remove one image from one of the group's galleries on VRChat. `galleryId` and "
                + "`imageId` name it; `submittedById` is kept in the audit log, as the page showed "
                + "it. One request, never retried. An image VRChat no longer has answers 404. Once "
                + "VRChat accepts, the audit log records who removed it.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    /// <summary>
    /// Display names for the people who submitted the images, from the profiles Modbot has
    /// already read. One read of Modbot's own table, never a request to VRChat per person.
    /// </summary>
    private static async Task<Dictionary<string, string>> SubmitterNamesAsync(
        ModbotContext db, IReadOnlyList<GroupGalleryImage> images, CancellationToken ct)
    {
        var ids = images
            .Select(i => i.SubmittedByUserId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ids.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        return await db.VRChatUsers.AsNoTracking()
            .Where(u => ids.Contains(u.UserId) && u.DisplayName != null)
            .ToDictionaryAsync(u => u.UserId, u => u.DisplayName!, StringComparer.Ordinal, ct);
    }
}
