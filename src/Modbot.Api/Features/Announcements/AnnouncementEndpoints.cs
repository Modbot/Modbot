using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Api.Conventions;
using Modbot.Core.Announcements;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.VRChat;
using Modbot.VRChat.Announcements;
using NodaTime;
using NodaTime.Text;

namespace Modbot.Api.Features.Announcements;

/// <summary>What the Live page's Announce form sends.</summary>
/// <param name="InstanceId">Modbot's own id for the instance, as the Live page lists it.</param>
/// <param name="When"><c>now</c> or <c>later</c>.</param>
/// <param name="SendAt">For <c>later</c>: the date and time, <c>yyyy-MM-ddTHH:mm</c>, in <paramref name="TimeZone"/>.</param>
/// <param name="TimeZone">An IANA zone, such as <c>Europe/London</c>. UTC when left out.</param>
public sealed record AnnouncementRequest(
    Guid InstanceId,
    string? Title,
    string? Message,
    string? When = null,
    string? SendAt = null,
    string? TimeZone = null);

/// <summary>One message to everyone in an instance, and how it went.</summary>
/// <param name="State"><c>scheduled</c>, <c>sending</c>, <c>sent</c>, <c>refused</c>, <c>failed</c> or <c>cancelled</c>.</param>
/// <param name="Status">VRChat's HTTP status, when it answered.</param>
/// <param name="Error">VRChat's own words for a refusal, or Modbot's for a failure.</param>
/// <param name="MissingGroupPermission">Set when Modbot's VRChat account lacks the group permission it needs.</param>
public sealed record AnnouncementView(
    Guid Id,
    Guid InstanceId,
    string Title,
    string Message,
    string State,
    DateTimeOffset SendAt,
    string TimeZone,
    DateTimeOffset? SentAt,
    int? Status,
    string? Error,
    MissingGroupPermission? MissingGroupPermission,
    DateTimeOffset CreatedAt);

/// <param name="CanSend">Whether the caller holds Announce in instances.</param>
public sealed record AnnouncementList(IReadOnlyList<AnnouncementView> Announcements, bool CanSend);

/// <summary>
/// Messages to everyone in one of the group's open instances: the pop-up VRChat shows in the
/// instance. Sent now from the Live page, or at a time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Send now asks VRChat in the request</strong>, so the person sees at once whether it
/// went: the row is saved first, then one request, never retried. <strong>Later only saves the
/// row</strong>; the calendar's loop sends it within about fifteen seconds of its time, once, or
/// marks it failed when it is an hour late or the instance has closed.
/// </para>
/// <para>
/// Modbot's VRChat account needs Create Instance Announcement in the group. When the last group
/// read says it lacks it, the request is turned down before anything is saved or sent.
/// </para>
/// </remarks>
public static class AnnouncementEndpoints
{
    public const string WhenNow = "now";
    public const string WhenLater = "later";

    /// <summary>The most rows the list returns.</summary>
    public const int ListSize = 200;

    private static readonly LocalDateTimePattern LocalPattern = LocalDateTimePattern.CreateWithInvariantCulture("uuuu'-'MM'-'dd'T'HH':'mm");
    private static readonly LocalDateTimePattern LocalPatternSeconds = LocalDateTimePattern.CreateWithInvariantCulture("uuuu'-'MM'-'dd'T'HH':'mm':'ss");

    public static IEndpointRouteBuilder MapAnnouncements(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/announcements").WithTags("Announcements").RequireAuthorization();

        group.MapGet("/", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

                // Only for the instances still open: the Live page shows those, and the record of
                // the rest is in the audit log.
                var rows = await db.VRChatAnnouncements.AsNoTracking()
                    .Where(a => db.VRChatInstances.Any(i => i.Id == a.InstanceId && i.ClosedAt == null))
                    .OrderByDescending(a => a.CreatedAt)
                    .Take(ListSize)
                    .ToListAsync(ct);

                return Results.Ok(new AnnouncementList(
                    rows.Select(a => ViewOf(a, settings)).ToList(),
                    ModbotAuth.Allows(ModbotAuth.PermissionsOf(http.User), ModbotPermissions.AnnounceInInstances)));
            })
            .RequiresFlag(ModbotPermissions.ViewLiveInstances)
            .WithName("ListAnnouncements")
            .WithSummary("List instance announcements")
            .WithDescription(
                "The messages sent or scheduled to the group's instances that are still open, newest "
                + "first, at most 200, each with how it went. Read from Modbot's own tables; nothing "
                + "here calls VRChat.")
            .Produces<AnnouncementList>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/", async (
                HttpContext http,
                [FromBody] AnnouncementRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] AnnouncementSender? sender,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (ModbotAuth.UserIdOf(http.User) is not { } actor)
                    return Results.Forbid();

                if (sender is null || facts is null || partitions is null)
                    return Problems.Of(StatusCodes.Status503ServiceUnavailable, "This deployment is not set up to act in VRChat.", Problems.NotSetUp);

                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
                if (settings?.ManagedGroupId is not { Length: > 0 } groupId)
                    return Problems.Of(StatusCodes.Status409Conflict, "No VRChat group is set up yet.", Problems.NoGroup);

                var now = clock.UtcNow;
                var title = AnnouncementRules.Tidy(body.Title);
                var message = AnnouncementRules.Tidy(body.Message);
                var problems = AnnouncementRules.Problems(title, message);

                var instance = await db.VRChatInstances.AsNoTracking()
                    .FirstOrDefaultAsync(i => i.Id == body.InstanceId, ct);

                if (instance is null || instance.GroupId != groupId || !instance.SeenInGroupList)
                    problems.Add("That instance is not one of the group's.");
                else if (instance.ClosedAt is not null)
                    problems.Add(AnnouncementRules.InstanceClosed);
                else if (!InstanceAnnounce.CanBePath(instance.Location))
                    problems.Add("That instance has no location Modbot can send to.");

                var zone = CalendarRepeat.FindZone(string.IsNullOrWhiteSpace(body.TimeZone) ? "UTC" : body.TimeZone.Trim());
                if (zone is null)
                    problems.Add("That time zone is not known.");

                var when = body.When?.Trim().ToLowerInvariant();
                DateTimeOffset sendAt = now;

                if (when == WhenLater)
                {
                    var local = ParseLocal(body.SendAt);

                    if (local is null)
                    {
                        problems.Add("Pick a date and time.");
                    }
                    else if (zone is not null)
                    {
                        sendAt = zone.AtLeniently(local.Value).ToInstant().ToDateTimeOffset();

                        if (sendAt < now - AnnouncementRules.PastGrace)
                            problems.Add("That time has passed.");
                    }
                }
                else if (when != WhenNow)
                {
                    problems.Add("Pick when it goes.");
                }

                if (problems.Count > 0)
                    return Results.BadRequest(new { error = string.Join(" ", problems), problems });

                // Said plainly, and nothing saved or sent, when the account is known to lack it.
                if (VRChatGroupPermissions.Holds(settings.VRChatAccountPermissions, VRChatGroupPermissions.CreateInstanceAnnouncement) == false)
                {
                    var missing = new MissingGroupPermission(
                        VRChatGroupPermissions.CreateInstanceAnnouncement, groupId, VRChatGroupPermissions.RoleNames(settings), null);

                    return Problems.Of(
                        StatusCodes.Status409Conflict,
                        VRChatGroupPermissions.Sentence(missing),
                        Problems.NotPossible,
                        new { missingGroupPermission = missing });
                }

                var announcement = new VRChatAnnouncement
                {
                    Id = Guid.CreateVersion7(),
                    InstanceId = instance!.Id,
                    Location = instance.Location,
                    GroupId = groupId,
                    Title = title,
                    Message = message,
                    State = VRChatAnnouncementStates.Scheduled,
                    SendAt = sendAt,
                    TimeZone = zone!.Id,
                    CreatedByUserId = actor,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                if (when == WhenNow)
                {
                    await sender.SendNowAsync(announcement, actor, ct);
                    return Results.Ok(ViewOf(announcement, settings));
                }

                await using (var transaction = await db.Database.BeginTransactionAsync(ct))
                {
                    db.VRChatAnnouncements.Add(announcement);
                    await db.SaveChangesAsync(ct);

                    var data = AnnouncementSender.Payload(announcement);
                    data["sendAt"] = announcement.SendAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                    data["timeZone"] = announcement.TimeZone;

                    await WriteFactAsync(facts, partitions, FactType.GroupAnnouncementScheduled, announcement, actor, now, data, ct);
                    await transaction.CommitAsync(ct);
                }

                return Results.Ok(ViewOf(announcement, settings));
            })
            .RequiresFlag(ModbotPermissions.ViewLiveInstances | ModbotPermissions.AnnounceInInstances)
            .WithName("CreateAnnouncement")
            .WithSummary("Send instance announcement")
            .WithDescription(
                "Sends a message to everyone in one of the group's open instances: the pop-up VRChat "
                + "shows in the instance. `title` (at most 100 characters) and `message` (at most 500) "
                + "are required, as plain text on one line: line breaks become spaces and other control "
                + "characters are dropped. `when` is `now`, which asks VRChat in this request and "
                + "answers with how it went (`sent`, `refused` with VRChat's status and words, or "
                + "`failed`), or `later` with `sendAt` in `timeZone`, which Modbot sends once at that "
                + "time. Never retried. Modbot's VRChat account needs Create Instance Announcement in "
                + "the group; when the last group read says it lacks it, this answers 409 with "
                + "`missingGroupPermission` and nothing is saved. The rate limit is a placeholder "
                + "nobody has measured: one request every ten seconds.")
            .Produces<AnnouncementView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/{id:guid}/cancel", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
            {
                if (ModbotAuth.UserIdOf(http.User) is not { } actor)
                    return Results.Forbid();

                if (facts is null || partitions is null)
                    return Problems.Of(StatusCodes.Status503ServiceUnavailable, "This deployment is not set up to act in VRChat.", Problems.NotSetUp);

                var announcement = await db.VRChatAnnouncements.FirstOrDefaultAsync(a => a.Id == id, ct);
                if (announcement is null)
                    return Results.NotFound(new { error = "That announcement does not exist." });

                if (AnnouncementRules.CannotCancel(announcement) is { } cannot)
                    return Results.Conflict(new { error = cannot });

                var now = clock.UtcNow;
                announcement.State = VRChatAnnouncementStates.Cancelled;
                announcement.UpdatedAt = now;
                announcement.Version++;

                await using (var transaction = await db.Database.BeginTransactionAsync(ct))
                {
                    try
                    {
                        await db.SaveChangesAsync(ct);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        // The loop claimed it in the meantime.
                        return Results.Conflict(new { error = "It is being sent." });
                    }

                    await WriteFactAsync(
                        facts, partitions, FactType.GroupAnnouncementCancelled, announcement, actor, now,
                        AnnouncementSender.Payload(announcement), ct);
                    await transaction.CommitAsync(ct);
                }

                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
                return Results.Ok(ViewOf(announcement, settings));
            })
            .RequiresFlag(ModbotPermissions.ViewLiveInstances | ModbotPermissions.AnnounceInInstances)
            .WithName("CancelAnnouncement")
            .WithSummary("Cancel instance announcement")
            .WithDescription(
                "Calls off a scheduled announcement before it goes. Refused (409) once it is being "
                + "sent or has finished.")
            .Produces<AnnouncementView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    internal static AnnouncementView ViewOf(VRChatAnnouncement a, Core.Data.Entities.Settings? settings)
    {
        var missing = a.MissingPermission is { } permission
            ? new MissingGroupPermission(permission, a.GroupId, VRChatGroupPermissions.RoleNames(settings), a.Error)
            : null;

        return new AnnouncementView(
            a.Id, a.InstanceId, a.Title, a.Message, a.State, a.SendAt, a.TimeZone, a.SentAt,
            a.StatusCode, a.Error, missing, a.CreatedAt);
    }

    private static async Task WriteFactAsync(
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        string type,
        VRChatAnnouncement announcement,
        Guid actor,
        DateTimeOffset now,
        System.Text.Json.Nodes.JsonObject data,
        CancellationToken ct)
    {
        await partitions.EnsureForAsync(now, ct);

        await facts.WriteAsync(
            new FactRecord
            {
                Type = type,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = announcement.GroupId,
                ActorPlatform = FactPlatform.Modbot,
                ActorId = actor.ToString(),
                Source = FactSource.Manual,
                Data = data,
            },
            ct);
    }

    private static LocalDateTime? ParseLocal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        var result = LocalPattern.Parse(trimmed);
        if (!result.Success)
            result = LocalPatternSeconds.Parse(trimmed);

        return result.Success ? result.Value : null;
    }
}
