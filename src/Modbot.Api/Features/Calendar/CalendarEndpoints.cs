using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Calendar;
using Modbot.Api.Auth;
using Modbot.Api.Features.GroupPage;
using Modbot.Api.Features.Users;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Net;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.VRChat;
using Modbot.VRChat.Calendar;
using Modbot.VRChat.Files;
using NodaTime;
using NodaTime.Text;

namespace Modbot.Api.Features.Calendar;

/// <summary>
/// The calendar page, its events, and the calendar feed (calendar design).
/// </summary>
/// <remarks>
/// <para>
/// This only stores what a person decides. Publishing to VRChat and Discord and opening instances
/// happen in the calendar's own loops, which read what is saved here -- so a request never waits on
/// VRChat's strict calendar limit, and a change saved three times in a minute is one VRChat write.
/// </para>
/// <para>
/// Every change is a fact, saved in the same transaction as the change itself.
/// </para>
/// </remarks>
public static class CalendarEndpoints
{
    public const int MaxListItems = 20;
    public const int MaxListItemLength = 64;
    public const int MaxOpenMinutesBefore = 120;

    /// <summary>The refusal for a Discord picture over <see cref="CalendarCoverPicture.MaxBytes"/>.</summary>
    public const string CoverTooBig = "The picture is larger than 8 MB.";

    /// <summary>The most staff accounts one event invites, besides the host.</summary>
    public const int MaxInviteStaff = 20;

    /// <summary>What picking an invite list needs: the Lists rule (lists design §6).</summary>
    public const ModbotPermissions ToPickAList = ModbotPermissions.ViewMembers | ModbotPermissions.ViewProfile;

    /// <summary>The longest one occurrence may last. Discord and VRChat both expect an evening, not a week.</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromDays(7);

    /// <summary>The widest range the page may ask occurrences for at once.</summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(62);

    private static readonly string[] AccessTypes = ["members", "plus", "public"];
    private static readonly string[] Regions = ["us", "use", "eu", "jp"];
    private static readonly string[] Visibilities = ["group", "public"];

    private static readonly LocalDateTimePattern LocalPattern = LocalDateTimePattern.CreateWithInvariantCulture("uuuu'-'MM'-'dd'T'HH':'mm");
    private static readonly LocalDateTimePattern LocalPatternSeconds = LocalDateTimePattern.CreateWithInvariantCulture("uuuu'-'MM'-'dd'T'HH':'mm':'ss");

    public static IEndpointRouteBuilder MapCalendar(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Every change made here empties the public feed, so it shows there at once (§6.1).
        var group = PublicCalendarFeedCache.ClearsPublicFeed(app.MapGroup("/api/calendar").WithTags("Calendar"));

        group.MapGet("/", async (
                HttpContext http,
                [FromQuery] DateTimeOffset? from,
                [FromQuery] DateTimeOffset? to,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                // Optional: the bot is wired by the host, not by the API. Without it Discord is not set up.
                [FromServices] IDiscordBotStatus? discordBot,
                CancellationToken ct) =>
            {
                var now = clock.UtcNow;
                var start = from ?? now.AddDays(-7);
                var end = to ?? start.AddDays(42);

                if (end <= start || end - start > MaxRange)
                    return Results.BadRequest(new { error = "Ask for at most 62 days at a time." });

                var events = await db.CalendarEvents.AsNoTracking()
                    .Where(e => e.DeletedAt == null)
                    .OrderBy(e => e.StartsAt)
                    .ToListAsync(ct);

                var views = await ViewsAsync(db, events, start, end, ct);
                var held = ModbotAuth.PermissionsOf(http.User);
                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

                return Results.Ok(new CalendarView(
                    views,
                    ModbotAuth.Allows(held, ModbotPermissions.ManageCalendar),
                    CalendarVRChatRequests.Categories,
                    CalendarVRChatRequests.Platforms,
                    now,
                    ModbotAuth.Allows(held, ModbotPermissions.ViewAnalytics),
                    CalendarReadiness.Of(settings, discordBot),
                    settings?.VRChatPictureUploads ?? false));
            })
            .RequiresFlag(ModbotPermissions.ViewCalendar)
            .WithName("GetCalendar")
            .WithSummary("Get calendar")
            .WithDescription(
                "Every event, with its occurrences in the range, where it is published and how that "
                + "went, and which places are set up: VRChat needs a managed group and a VRChat "
                + "account, Discord a server id and a connected bot.")
            .Produces<CalendarView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/vrchat", async (
                [FromBody] CalendarVRChatReadRequest? body,
                [FromServices] CalendarVRChatReader reader,
                CancellationToken ct) =>
            {
                body ??= new CalendarVRChatReadRequest(null, null, Upcoming: true, Refresh: false);

                CalendarReadResult result;

                if (body.Upcoming)
                {
                    result = await reader.ReadUpcomingAsync(body.Refresh, ct);
                }
                else
                {
                    if (body.From is not { } from || body.To is not { } to || to <= from || to - from > MaxRange)
                        return Results.BadRequest(new { error = "Ask for at most 62 days at a time." });

                    result = await reader.ReadAsync(from, to, body.Refresh, ct);
                }

                return Results.Ok(new CalendarVRChatReadView(
                    result.Outcome switch
                    {
                        CalendarReadOutcome.Read => "read",
                        CalendarReadOutcome.Remembered => "remembered",
                        CalendarReadOutcome.NotConfigured => "notConfigured",
                        CalendarReadOutcome.Waiting => "waiting",
                        _ => "failed",
                    },
                    result.Error));
            })
            .RequiresFlag(ModbotPermissions.ViewCalendar)
            .WithName("ReadVRChatCalendar")
            .WithSummary("Read VRChat's calendar")
            .WithDescription(
                "Brings events made on VRChat, and changes and deletes made there, into Modbot's calendar "
                + "for the months asked for. A month read in the last five minutes is not asked for again "
                + "unless refresh is set. Can take several seconds: VRChat's calendar is read gently.")
            .Produces<CalendarVRChatReadView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/events/{id:guid}", async (
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var calendarEvent = await db.CalendarEvents.AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt == null, ct);

                if (calendarEvent is null)
                    return Results.NotFound();

                var now = clock.UtcNow;
                var views = await ViewsAsync(db, [calendarEvent], now, now.AddDays(42), ct);
                return Results.Ok(views[0]);
            })
            .RequiresFlag(ModbotPermissions.ViewCalendar)
            .WithName("GetCalendarEvent")
            .WithSummary("Get calendar event")
            .WithDescription("One event.")
            .Produces<CalendarEventView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/events", async (
                HttpContext http,
                [FromBody] CalendarEventRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var now = clock.UtcNow;
                var calendarEvent = new CalendarEvent
                {
                    Id = Guid.CreateVersion7(),
                    CreatedAt = now,
                    UpdatedAt = now,
                    CreatedByUserId = ModbotAuth.UserIdOf(http.User),
                };

                if (await RefusedAsync(db, body, calendarEvent, keptMention: null, keptCover: null, ct) is { } problems)
                    return problems;

                if (await CheckInvitesAsync(http, db, body, keptListId: null, kept: [], ct) is { } refused)
                    return refused;

                // Google Calendar starts ticked once it is set up, for an event everyone may see
                // (Google Calendar design decision 6), unless the form says otherwise.
                if (body.PublishToGoogle is null)
                {
                    var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
                    calendarEvent.PublishToGoogle = settings is not null && CalendarGoogle.TicksByDefault(settings, calendarEvent);
                }

                calendarEvent.State = body.Draft ? CalendarEventStates.Draft : CalendarEventStates.Scheduled;

                if (!body.Draft && Place(calendarEvent, now) is { } ended)
                    return Results.BadRequest(new { error = ended });

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.CalendarEvents.Add(calendarEvent);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.PlannedEventCreated, calendarEvent.Id.ToString(), Actor.Of(http), Describe(calendarEvent), ct);

                await transaction.CommitAsync(ct);

                await PickDateAsync(db, facts, clock, calendarEvent, ct);

                var views = await ViewsAsync(db, [calendarEvent], now, now.AddDays(42), ct);
                return Results.Ok(views[0]);
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("CreateCalendarEvent")
            .WithSummary("Add calendar event")
            .WithDescription(
                "Plan an event. A refusal (400) lists everything wrong at once, in problems, and joins "
                + "it in error. When the event goes to VRChat's calendar and the group as Modbot last "
                + "read it says Modbot's VRChat account lacks Manage Group Calendar, that comes first; "
                + "on its own it does not refuse the save.")
            .Produces<CalendarEventView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/events/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] CalendarEventRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var calendarEvent = await db.CalendarEvents.FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt == null, ct);
                if (calendarEvent is null)
                    return Results.NotFound();

                if (calendarEvent.State == CalendarEventStates.Cancelled)
                    return Results.Conflict(new { error = "A cancelled event cannot be changed." });

                if (body.Draft && calendarEvent.State != CalendarEventStates.Draft)
                    return Results.Conflict(new { error = "A published event cannot go back to being a draft." });

                var before = Describe(calendarEvent);
                var now = clock.UtcNow;
                var zoneBefore = CalendarRepeat.ZoneOf(calendarEvent);
                var keptMention = calendarEvent.MentionRoleId;
                var coverBefore = calendarEvent.CoverPictureId;

                // Keeping the list already on the event needs nothing more; picking one does.
                List<Guid> kept = [.. calendarEvent.InviteStaffUserIds];
                if (calendarEvent.InviteHostUserId is { } keptHost)
                    kept.Add(keptHost);

                if (await CheckInvitesAsync(http, db, body, keptListId: calendarEvent.InviteListId, kept, ct) is { } refused)
                    return refused;

                if (await RefusedAsync(db, body, calendarEvent, keptMention, keptCover: coverBefore, ct) is { } problems)
                    return problems;

                // Dates cancelled or changed on their own follow the series to its new times, by the
                // day they fall on (calendar design §2.2).
                CalendarDates.Rematch(calendarEvent, zoneBefore, now);

                if (!body.Draft)
                {
                    // A draft being published, or a finished event given new dates, starts again.
                    if (calendarEvent.State is CalendarEventStates.Draft or CalendarEventStates.Finished)
                        calendarEvent.State = CalendarEventStates.Scheduled;

                    // Moved or shortened: the current occurrence is worked out again from the new rule.
                    calendarEvent.OccurrenceStartsAt = null;

                    if (Place(calendarEvent, now) is { } ended)
                        return Results.BadRequest(new { error = ended });

                    // The places that failed show being sent again at once, not the old failure.
                    await ClearFailuresAsync(db, calendarEvent.Id, now, ct);
                }

                calendarEvent.Version++;
                calendarEvent.UpdatedAt = now;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.PlannedEventChanged,
                    calendarEvent.Id.ToString(),
                    Actor.Of(http),
                    new JsonObject { ["title"] = calendarEvent.Title, ["before"] = before, ["after"] = Describe(calendarEvent) },
                    ct);

                await transaction.CommitAsync(ct);

                // A Discord picture this event no longer uses is not kept for nothing (§15.4).
                if (coverBefore != calendarEvent.CoverPictureId)
                    await DropCoverIfUnusedAsync(db, coverBefore, ct);

                await PickDateAsync(db, facts, clock, calendarEvent, ct);

                var views = await ViewsAsync(db, [calendarEvent], now, now.AddDays(42), ct);
                return Results.Ok(views[0]);
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("UpdateCalendarEvent")
            .WithSummary("Update calendar event")
            .WithDescription(
                "Publishing follows on its own. Several changes close together are sent to VRChat as one. "
                + "A place that failed goes back to waiting and is sent again, even when nothing it is "
                + "sent changed; a VRChat event that got no answer is the exception, and keeps its "
                + "Try again. A refusal (400) lists everything wrong at once, as for a new event.")
            .Produces<CalendarEventView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/events/{id:guid}/open", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] CalendarOpener opener,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                if (ModbotAuth.UserIdOf(http.User) is not { } actor)
                    return Results.Forbid();

                var result = await opener.OpenNowAsync(id, actor, ct);

                // The words are shared with /event open in Discord (CalendarOpenWords).
                var refusal = result.Outcome switch
                {
                    CalendarOpenNowOutcome.NoSuchEvent => Results.NotFound(),
                    var outcome when CalendarOpenWords.Refusal(outcome) is { } words => Results.Conflict(new { error = words }),
                    _ => null,
                };

                if (refusal is not null)
                    return refusal;

                // How it went is on the event: the instance, or VRChat's words under Instance.
                var calendarEvent = await db.CalendarEvents.AsNoTracking().FirstAsync(e => e.Id == id, ct);
                var now = clock.UtcNow;
                var views = await ViewsAsync(db, [calendarEvent], now, now.AddDays(42), ct);
                return Results.Ok(views[0]);
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("OpenCalendarEventNow")
            .WithSummary("Open an event's instance now")
            .WithDescription(
                "Opens the group instance for the event's current or next time, from two hours before "
                + "its start until its end, when none is open. Refused while an earlier attempt has no "
                + "answer yet. One request to VRChat, recorded with who asked; the invites and the "
                + "first-person posts follow as usual. Answers with the event.")
            .Produces<CalendarEventView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/events/{id:guid}/cancel", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] CalendarCancelRequest? body,
                [FromServices] CalendarCancellations cancellations,
                CancellationToken ct) =>
            {
                // The rules and the writes are CalendarCancellations', which /event in Discord calls too.
                var result = await cancellations.CancelEventAsync(id, body?.PostInChannel ?? false, Actor.Of(http), ct);
                return CancelAnswer(result);
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("CancelCalendarEvent")
            .WithSummary("Cancel calendar event")
            .WithDescription(
                "Cancel an event. It is taken off VRChat's calendar and ended in Discord, and its "
                + "channel post is marked cancelled. With postInChannel, a short message that it is "
                + "cancelled is also posted in the event's channel, once. The body may be left out.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/events/{id:guid}/dates", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] CalendarDateRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var calendarEvent = await db.CalendarEvents.FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt == null, ct);
                if (calendarEvent is null)
                    return Results.NotFound();

                var now = clock.UtcNow;
                var planned = body.PlannedStartsAt;

                if (DateProblem(calendarEvent, planned, now) is { } problem)
                    return problem;

                var change = calendarEvent.DateChanges.FirstOrDefault(c => c.PlannedStartsAt == planned);
                if (change is { Cancelled: true })
                    return Results.Conflict(new { error = "That date is cancelled." });

                var was = CalendarRepeat.ForDate(calendarEvent, planned)!.Value;

                if (ApplyDate(body, calendarEvent, was, now) is { } wrong)
                    return Results.BadRequest(new { error = wrong });

                var length = CalendarRepeat.LengthOf(calendarEvent);
                var title = Own(body.Title, calendarEvent.Title);
                var description = Own(body.Description, calendarEvent.Description);
                var keepsTime = body.StartsAt == planned && body.EndsAt == planned + length;

                if (change is null)
                {
                    change = new CalendarDateChange
                    {
                        Id = Guid.CreateVersion7(),
                        EventId = calendarEvent.Id,
                        PlannedStartsAt = planned,
                        CreatedAt = now,
                    };

                    calendarEvent.DateChanges.Add(change);
                }

                change.StartsAt = keepsTime ? null : body.StartsAt;
                change.EndsAt = keepsTime ? null : body.EndsAt;
                change.Title = title;
                change.Description = description;
                change.UpdatedAt = now;

                // A changed date shows being sent, not the failure of what it was before.
                CalendarVRChatPublisher.TryDateAgain(change);
                CalendarGooglePublisher.TryDateAgain(change);
                await ClearFailuresAsync(db, calendarEvent.Id, now, ct);

                // Put back exactly as planned: nothing of its own is left to keep -- unless VRChat
                // was sent the change, which it keeps until it is sent the planned date back; the
                // publisher removes the row once it has been (calendar design §2.2).
                if (CalendarDates.CanForget(change, length))
                    calendarEvent.DateChanges.Remove(change);

                var after = CalendarRepeat.ForDate(calendarEvent, planned)!.Value;

                CalendarCancellations.MovedOn(calendarEvent, now);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.PlannedDateChanged,
                    calendarEvent.Id.ToString(),
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["title"] = calendarEvent.Title,
                        ["date"] = planned.ToString("O", CultureInfo.InvariantCulture),
                        ["before"] = DateFields(calendarEvent, was),
                        ["after"] = DateFields(calendarEvent, after),
                    },
                    ct);

                await transaction.CommitAsync(ct);

                var views = await ViewsAsync(db, [calendarEvent], now, now.AddDays(42), ct);
                return Results.Ok(views[0]);
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("ChangeCalendarDate")
            .WithSummary("Change one date of an event")
            .WithDescription(
                "Moves one date of a repeating event, or gives it its own title or description, leaving "
                + "the other dates as they are. Sending the planned time and the event's own words puts "
                + "the date back as planned.")
            .Produces<CalendarEventView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/events/{id:guid}/dates/cancel", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] CalendarDateCancelRequest body,
                [FromServices] CalendarCancellations cancellations,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var result = await cancellations.CancelDateAsync(id, body.PlannedStartsAt, body.PostInChannel, Actor.Of(http), ct);
                return CancelAnswer(result);
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("CancelCalendarDate")
            .WithSummary("Cancel one date of an event")
            .WithDescription(
                "Cancels one date of a repeating event and leaves the others. That date is taken off "
                + "VRChat's calendar, its Discord event is ended, and the calendar feed leaves it out. "
                + "With postInChannel, a short message that the date is cancelled is posted in the "
                + "event's channel, once.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        // Any failed place, sent again without an edit (calendar design §17.4, added 2026-10-02).
        // Until then only a VRChat create that got no answer and was not on VRChat's calendar had
        // it, at /events/{id}/vrchat/try-again, which is this route with place "vrchat". That one
        // still looks at VRChat's calendar first: VRChat has made events while answering 500.
        group.MapPost("/events/{id:guid}/{place}/try-again", async (
                [FromRoute] Guid id,
                [FromRoute] string place,
                [FromBody] CalendarTryAgainRequest? body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                if (place != CalendarPlaces.VRChat && place != CalendarPlaces.Google && !CalendarDiscordRetry.IsDiscord(place))
                    return Results.NotFound();

                var now = clock.UtcNow;

                // One date of a repeating event whose own VRChat or Google write failed.
                if (body?.PlannedStartsAt is { } planned)
                {
                    if (place != CalendarPlaces.VRChat && place != CalendarPlaces.Google)
                        return Results.BadRequest(new { error = "Only VRChat's calendar and Google Calendar have dates of their own to try again." });

                    var calendarEvent = await db.CalendarEvents.FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt == null, ct);
                    var change = calendarEvent?.DateChanges.FirstOrDefault(c => c.PlannedStartsAt == planned);

                    if (change is null)
                        return Results.NotFound();

                    var triedDate = place == CalendarPlaces.VRChat
                        ? CalendarVRChatPublisher.TryDateAgain(change)
                        : CalendarGooglePublisher.TryDateAgain(change);

                    if (!triedDate)
                        return Results.Conflict(new { error = "There is nothing to try again." });

                    await db.SaveChangesAsync(ct);
                    return Results.NoContent();
                }

                var row = await db.CalendarEventPlaces.FirstOrDefaultAsync(p => p.EventId == id && p.Place == place, ct);

                if (row is null || !await db.CalendarEvents.AnyAsync(e => e.Id == id && e.DeletedAt == null, ct))
                    return Results.NotFound();

                // A place that has not failed -- a second press finds it waiting -- is refused.
                var tried = place switch
                {
                    CalendarPlaces.VRChat => CalendarVRChatPublisher.TryAgain(row, now),
                    CalendarPlaces.Google => CalendarGooglePublisher.TryAgain(row, now),
                    _ => CalendarDiscordRetry.TryAgain(row, now),
                };

                if (!tried)
                    return Results.Conflict(new { error = "There is nothing to try again." });

                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("TryCalendarPlaceAgain")
            .WithSummary("Try a place again")
            .WithDescription(
                "Send a failed place of an event again, as it is, without editing the event: place is "
                + "vrchat, discordEvent, channelPost, cancelPost or googleCalendar, and the place has "
                + "canTryAgain. With plannedStartsAt, one date of a repeating event whose own change to "
                + "VRChat's calendar or Google Calendar failed. A VRChat event that got no answer and was "
                + "not on VRChat's calendar is looked for once more before it is sent, and a Google "
                + "event whose insert got no answer is read back first. Sent on the next pass; the "
                + "place says waiting until then. 409 when there is no failure to try again, as on "
                + "a second press. The body may be left out.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapDelete("/events/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var calendarEvent = await db.CalendarEvents.FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt == null, ct);
                if (calendarEvent is null)
                    return Results.NotFound();

                var now = clock.UtcNow;

                // A delete is a cancel that is also hidden: every place it was published is cleaned up
                // the same way, and the row stays for the facts that point at it.
                if (CalendarEventStates.IsLive(calendarEvent.State))
                {
                    calendarEvent.State = CalendarEventStates.Cancelled;
                    calendarEvent.CancelledAt = now;
                }

                calendarEvent.DeletedAt = now;
                calendarEvent.UpdatedAt = now;
                calendarEvent.Version++;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);
                await facts.RecordAsync(
                    FactType.PlannedEventDeleted, calendarEvent.Id.ToString(), Actor.Of(http),
                    new JsonObject { ["title"] = calendarEvent.Title }, ct);
                await transaction.CommitAsync(ct);

                // Its Discord picture goes with it, unless a copy of the event still uses it (§15.4).
                await DropCoverIfUnusedAsync(db, calendarEvent.CoverPictureId, ct);

                return Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("DeleteCalendarEvent")
            .WithSummary("Delete calendar event")
            .WithDescription("Delete an event. It is taken off everywhere it was published.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/preview", async (
                [FromBody] CalendarPreviewRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                // Optional: the bot is wired by the host, not by the API. Without it there is no
                // Discord preview.
                [FromServices] Modbot.Core.Calendar.ICalendarDiscordPreview? discord,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (body.Event is null)
                    return Results.BadRequest(new { error = "Send the event to preview." });

                var result = await CalendarPreviews.BuildAsync(db, discord, clock.UtcNow, body, ct);

                if (result.Missing)
                    return Results.NotFound();

                return result.View is { } view ? Results.Ok(view) : Results.BadRequest(new { error = result.Problem });
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("PreviewCalendarEvent")
            .WithSummary("Preview calendar event")
            .WithDescription(
                "The event as it is filled in, drawn the way each place would show it: the Discord "
                + "event and the channel post as Discord is sent them, what VRChat's calendar is sent, "
                + "and what a calendar program reads from the calendar feed. Drawn by the code that "
                + "sends each one. Saves nothing and asks VRChat and Discord nothing. A title, "
                + "description, channel or world not filled in yet is let through; anything a save "
                + "would refuse for its shape or length is refused.")
            .Produces<CalendarPreviewView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        // Its own request rather than part of saving the event: the upload waits on its own
        // one-a-minute budget and can be refused, and a save must never wait on VRChat or fail
        // because of it (the class remarks). The person sees the picture's answer on the picture
        // field the moment they choose it, and saving stays the plain JSON it was.
        group.MapPost("/vrchat-picture", async (
                HttpContext http,
                [FromQuery] Guid? eventId,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                // Optional: the VRChat services are wired by the host, not by the API.
                [FromServices] VRChatPictureUploads? uploads,
                CancellationToken ct) =>
            {
                // The operator's switch (Settings, Modbot's VRChat login). Asked before anything is
                // read from the body or sent to VRChat.
                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

                if (settings is not { VRChatPictureUploads: true })
                {
                    return Results.Json(
                        new { error = "Picture uploads are off." },
                        statusCode: StatusCodes.Status409Conflict);
                }

                if (uploads is null)
                {
                    // Only a host built without VRChat lacks the service; a Modbot server always has it,
                    // signed in or not (a missing sign-in is VRChat's answer below).
                    return Results.Json(
                        new { error = "This build of Modbot cannot upload pictures to VRChat." },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                // A picture that says it is too big is refused before a byte of it is read, and
                // whatever the body turns out to be, Kestrel stops reading just past the limit, the
                // way imports do.
                if (http.Request.ContentLength > VRChatPictureUploads.MaxBytes)
                {
                    return Results.Json(
                        new { error = VRChatPictureUploads.TooBig },
                        statusCode: StatusCodes.Status413PayloadTooLarge);
                }

                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                    limit.MaxRequestBodySize = VRChatPictureUploads.MaxBytes + 1;

                CalendarEvent? calendarEvent = null;

                if (eventId is { } id)
                {
                    calendarEvent = await db.CalendarEvents.AsNoTracking()
                        .FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt == null, ct);

                    if (calendarEvent is null)
                        return Results.NotFound();

                    if (calendarEvent.State == CalendarEventStates.Cancelled)
                        return Results.Conflict(new { error = "A cancelled event cannot be changed." });
                }

                var bytes = await ReadPictureAsync(http.Request.Body, http.Request.ContentLength, ct);

                if (VRChatPictureUploads.Problem(bytes) is { } problem)
                {
                    return Results.Json(
                        new { error = problem },
                        statusCode: bytes.Length > VRChatPictureUploads.MaxBytes
                            ? StatusCodes.Status413PayloadTooLarge
                            : StatusCodes.Status400BadRequest);
                }

                var answer = await uploads.UploadAsync(bytes, ct);

                if (!answer.Success)
                {
                    // Never sent again from here (foundation §4.3.1). Choosing the picture again is
                    // the person's decision.
                    var said = answer.IsRateLimited || answer.Kind == VRChatFailureKind.RateLimited
                        ? "VRChat is not taking uploads right now. Try again in a few minutes."
                        : GroupPageAnswers.Said(answer);

                    return Results.Json(new { error = said }, statusCode: GroupPageAnswers.StatusFor(answer));
                }

                if (answer.Value?.Id is not { Length: > 0 } fileId)
                {
                    return Results.Json(
                        new { error = "VRChat did not give the picture an id." },
                        statusCode: StatusCodes.Status502BadGateway);
                }

                await facts.RecordAsync(
                    FactType.PlannedEventPictureUploaded,
                    calendarEvent?.Id.ToString() ?? fileId,
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["fileId"] = fileId,
                        ["eventId"] = calendarEvent?.Id.ToString(),
                        ["title"] = calendarEvent?.Title,
                        ["bytes"] = bytes.Length,
                        ["type"] = VRChatPictureUploads.TypeOf(bytes),
                    },
                    ct);

                return Results.Ok(new CalendarVRChatPictureView(fileId));
            })
            // No declared request body, as with imports: the body is a file, and the reference
            // generator cannot draw a sample of one.
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("UploadCalendarVRChatPicture")
            .WithSummary("Upload VRChat calendar picture")
            .WithDescription(
                "Uploads a picture to VRChat, on the VRChat account Modbot signs in as, for an "
                + "event's entry on VRChat's calendar. The body is the picture itself: a PNG or JPEG "
                + "of at most 10 MB, told apart by its first bytes rather than its Content-Type. "
                + "Answers with the file id VRChat gave it; save that as the event's vrChatImageId. "
                + "eventId names the event when it is already saved, for the audit log. One request "
                + "to VRChat, at most one a minute and never retried; a picture that is too big or "
                + "not a PNG or JPEG is refused before VRChat is asked. Answers 409 \"Picture uploads are off.\" "
                + "until the operator turns uploads on in Settings; VRChat takes the upload only from an account with VRChat+. Modbot keeps none of the bytes.")
            .Produces<CalendarVRChatPictureView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        // The picture cropped in the form for Discord (§15.4): kept by Modbot, because Discord is
        // sent a cover's bytes each time the event is made or changed and the crop exists nowhere
        // else. Its own request, like VRChat's picture, so saving stays plain JSON. A cover nobody
        // saved is deleted by CalendarCoverSweep once it is a day old.
        group.MapPost("/cover", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                if (http.Request.ContentLength > CalendarCoverPicture.MaxBytes)
                {
                    return Results.Json(
                        new { error = CoverTooBig },
                        statusCode: StatusCodes.Status413PayloadTooLarge);
                }

                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                    limit.MaxRequestBodySize = CalendarCoverPicture.MaxBytes + 1;

                var bytes = await ReadPictureAsync(http.Request.Body, http.Request.ContentLength, ct);

                if (bytes.Length > CalendarCoverPicture.MaxBytes)
                {
                    return Results.Json(
                        new { error = CoverTooBig },
                        statusCode: StatusCodes.Status413PayloadTooLarge);
                }

                // The type its bytes have, and one Discord takes.
                var type = Core.Files.PictureFormats.Sniff(bytes);
                if (!Core.Files.PictureFormats.DiscordTakes(type))
                    return Results.BadRequest(new { error = "The picture must be a PNG, JPEG, GIF or WebP." });

                var cover = new CalendarCoverPicture
                {
                    Id = Guid.CreateVersion7(),
                    Bytes = bytes,
                    ContentType = type!,
                    CreatedAt = clock.UtcNow,
                    CreatedByUserId = ModbotAuth.UserIdOf(http.User),
                };

                db.CalendarCoverPictures.Add(cover);
                await db.SaveChangesAsync(ct);

                return Results.Ok(new CalendarCoverView(cover.Id));
            })
            // No declared request body, as with the VRChat picture: the body is a file.
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("UploadCalendarCover")
            .WithSummary("Upload Discord picture")
            .WithDescription(
                "Keeps a picture for an event's Discord cover and channel post. The body is the "
                + "picture itself: a PNG, JPEG, GIF or WebP of at most 8 MB, told apart by its first "
                + "bytes. Answers with its id; save that as the event's coverPictureId. Modbot keeps "
                + "it until the event is deleted or given another; one no event was saved with is "
                + "deleted once it is a day old, within the hour after.")
            .Produces<CalendarCoverView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status413PayloadTooLarge);

        group.MapGet("/covers/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var cover = await db.CalendarCoverPictures.AsNoTracking()
                    .Where(c => c.Id == id)
                    .Select(c => new { c.Bytes, c.ContentType })
                    .FirstOrDefaultAsync(ct);

                if (cover is null)
                    return Results.NotFound();

                // A cover never changes once kept, so its address can be kept as long as it lives.
                http.Response.Headers.CacheControl = "private, max-age=604800, immutable";
                http.Response.Headers["X-Content-Type-Options"] = "nosniff";
                http.Response.Headers.ContentSecurityPolicy = "sandbox";

                return Results.Bytes(cover.Bytes, cover.ContentType);
            })
            .RequiresFlag(ModbotPermissions.ViewCalendar)
            .WithName("GetCalendarCover")
            .WithSummary("Get Discord picture")
            .WithDescription("The picture kept for an event's Discord cover and channel post.")
            .Produces<byte[]>(StatusCodes.Status200OK, "image/png", "image/jpeg", "image/gif", "image/webp")
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        // The picture behind an event's picture link, for the form to crop and turn into a PNG in
        // the browser (§15.2). Fetched by Modbot, kept nowhere (PictureLinkFetch).
        group.MapPost("/picture-link", (
                HttpContext http,
                [FromBody] CalendarPictureLinkRequest body,
                [FromServices] ModbotContext db,
                // Optional: the VRChat side is wired by the host, not by the API.
                [FromServices] Core.Files.IPictures? vrchatPictures,
                CancellationToken ct) => PictureLinkFetch.AnswerAsync(http, body.Url, db, vrchatPictures, ct))
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("FetchCalendarPictureLink")
            .WithSummary("Fetch picture link")
            .WithDescription(
                "Fetches the picture behind an event's picture link and returns its bytes, for the "
                + "form to crop. The link must be https on port 443. Modbot connects only to public "
                + "addresses, checks every redirect the same way and follows at most three, reads a "
                + "page only as far as its og:image or twitter:image, stops at 10 MB and 15 seconds, "
                + "and returns only PNG, JPEG, GIF, WebP, BMP, AVIF or HEIC, told apart by the bytes. "
                + "A VRChat file link is fetched with Modbot's VRChat session. Nothing is kept.")
            .Produces<byte[]>(
                StatusCodes.Status200OK,
                "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp", "image/avif", "image/heic")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status502BadGateway);

        group.MapGet("/worlds", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var worlds = await db.VRChatWorlds.AsNoTracking()
                    .OrderByDescending(w => w.LastSeenAt)
                    .Take(200)
                    .Select(w => new CalendarWorldView(w.WorldId, w.Name, w.ThumbnailImageUrl))
                    .ToListAsync(ct);

                return Results.Ok(worlds);
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("ListCalendarWorlds")
            .WithSummary("List worlds for events")
            .WithDescription(
                "Worlds Modbot knows, most recently seen first, to pick an event's world from.")
            .Produces<IReadOnlyList<CalendarWorldView>>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/discord-duplicates", async (
                [FromServices] ModbotContext db,
                [FromServices] IDiscordServerEvents serverEvents,
                CancellationToken ct) =>
                Results.Ok(await CalendarDiscordDuplicates.BuildAsync(db, serverEvents, ct)))
            .RequiresFlag(ModbotPermissions.ViewCalendar)
            .WithName("ListDiscordEventDuplicates")
            .WithSummary("List possible duplicate Discord events")
            .WithDescription(
                "Events in the Discord server, made by anyone, that look like copies of each other: the same "
                + "title once case, spaces, punctuation, emoji and anything in brackets are left out, starting "
                + $"at most {(int)DiscordEventDuplicates.StartsWithin.TotalMinutes} minutes apart. Says which bot made each copy and which is Modbot's own; a "
                + "person is never named. Discord is asked at most once every five minutes. Changes nothing.")
            .Produces<CalendarDiscordDuplicatesView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/invite-choices", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var staff = await db.Users.AsNoTracking()
                    .Where(u => u.DeletedAt == null && !u.IsDisabled)
                    .OrderBy(u => u.Username)
                    .Select(u => new CalendarStaffChoice(
                        u.Id,
                        u.Username,
                        u.VRChatUserId != null && u.VRChatUserId != "",
                        u.DiscordUserId != null && u.DiscordUserId != ""))
                    .ToListAsync(ct);

                // The names of lists are no secret, but picking one is the Lists rule's to allow.
                var lists = ModbotAuth.Allows(ModbotAuth.PermissionsOf(http.User), ToPickAList)
                    ? await db.SavedLists.AsNoTracking()
                        .Where(l => l.DeletedAt == null)
                        .OrderBy(l => l.Name)
                        .Select(l => new CalendarListChoice(l.Id, l.Name))
                        .ToListAsync(ct)
                    : null;

                return Results.Ok(new CalendarInviteChoicesView(staff, lists));
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("ListCalendarInviteChoices")
            .WithSummary("List who an event can invite")
            .WithDescription(
                "The staff accounts an event can invite as its host and staff, and the saved lists it can "
                + "invite. Lists are null without See members and See profiles.")
            .Produces<CalendarInviteChoicesView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/feed", async (
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                CancellationToken ct) =>
            {
                var feed = await db.CalendarFeeds.AsNoTracking().FirstOrDefaultAsync(f => f.Id == 1, ct);
                var token = feed is null ? null : protector.Unprotect(feed.TokenEncrypted);

                return Results.Ok(await FeedViewAsync(db, token, ct));
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("GetCalendarFeed")
            .WithSummary("Get calendar feed link")
            .WithDescription("The calendar feed's link, or nulls when none has been made.")
            .Produces<CalendarFeedView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/feed", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                var feed = await db.CalendarFeeds.FirstOrDefaultAsync(f => f.Id == 1, ct);
                var replaced = feed is not null;

                if (feed is null)
                {
                    feed = new CalendarFeed { Id = 1 };
                    db.CalendarFeeds.Add(feed);
                }

                feed.TokenHash = HashToken(token);
                feed.TokenEncrypted = protector.Protect(token);
                feed.CreatedAt = clock.UtcNow;

                await db.SaveChangesAsync(ct);
                await facts.RecordAsync(
                    FactType.CalendarFeedRegenerated, "calendar-feed", Actor.Of(http),
                    new JsonObject { ["replaced"] = replaced }, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(await FeedViewAsync(db, token, ct));
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("RegenerateCalendarFeed")
            .WithSummary("Replace calendar feed link")
            .WithDescription("Make a new calendar feed link. The old one stops working at once.")
            .Produces<CalendarFeedView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/feed/{token}.ics", async (
                [FromRoute] string token,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
                    return Results.NotFound();

                var hash = HashToken(token);
                if (!await db.CalendarFeeds.AsNoTracking().AnyAsync(f => f.TokenHash == hash, ct))
                    return Results.NotFound();

                var now = clock.UtcNow;
                var events = await FeedEventsAsync(db, now, publicOnly: false, ct);
                var names = await FeedWorldNamesAsync(db, events, ct);

                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

                var body = CalendarFeedWriter.Write(FeedName(settings), events, names, now, settings?.PublicAddress);
                return Results.Text(body, "text/calendar; charset=utf-8", Encoding.UTF8);
            })
            .AllowAnonymous()
            .WithName("GetCalendarFeedFile")
            .WithSummary("Get calendar feed file")
            .WithDescription(
                "The calendar feed as iCalendar. No sign-in: the token in the address is the key. Holds "
                + "every scheduled and open event, and finished and cancelled ones for 30 days after.")
            .Produces<string>(StatusCodes.Status200OK, "text/calendar")
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/join/{id:guid}", async (
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var link = await JoinLinkAsync(db, id, ct);
                return link is null ? Results.NotFound() : Results.Redirect(link);
            })
            .AllowAnonymous()
            .WithName("JoinCalendarEvent")
            .WithSummary("Join an event")
            .WithDescription(
                "Sends a person on to the open instance of an event, for a Discord event's location.")
            .Produces(StatusCodes.Status302Found)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>
    /// The picture in a request body, read until it runs one byte past
    /// <see cref="VRChatPictureUploads.MaxBytes"/> and no further: enough to know it is too big
    /// without holding all of it. A body that says its length (a browser's file always does, and the
    /// caller has already refused one over the limit) is read straight into an array of that size,
    /// with no second copy.
    /// </summary>
    internal static async Task<byte[]> ReadPictureAsync(Stream body, long? length, CancellationToken ct)
    {
        const int OverTheLimit = (int)VRChatPictureUploads.MaxBytes + 1;

        if (length is { } size and <= VRChatPictureUploads.MaxBytes)
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

            // The body ended early: what arrived is the picture.
            if (filled < whole.Length)
                Array.Resize(ref whole, filled);

            return whole;
        }

        // No length (a chunked body): grow as it arrives.
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];

        while (buffer.Length < OverTheLimit)
        {
            var wanted = (int)Math.Min(chunk.Length, OverTheLimit - buffer.Length);
            var read = await body.ReadAsync(chunk.AsMemory(0, wanted), ct);
            if (read == 0)
                break;

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The events a feed lists at <paramref name="now"/>: live ones, and finished or cancelled ones
    /// for a while after (calendar design §6). The database narrows it to those states, and the
    /// writer's own rule decides. With <paramref name="publicOnly"/>, only the events the public feed
    /// may list (§6.1).
    /// </summary>
    internal static async Task<List<CalendarEvent>> FeedEventsAsync(
        ModbotContext db, DateTimeOffset now, bool publicOnly, CancellationToken ct)
    {
        var since = now - CalendarFeedWriter.KeepEndedFor;

        // A finished event's last date can only have ended inside the window when: a one-off
        // ends in it; a repeat's last day is no earlier than the window's start, less the
        // longest an event may last and a day for time zones; or one date was moved into it.
        // Only those are loaded, so the feed does not read every event a group ever ran.
        var lastDayFrom = DateOnly.FromDateTime((since - MaxLength - TimeSpan.FromDays(1)).UtcDateTime);

        var query = db.CalendarEvents.AsNoTracking()
            .Where(e => e.DeletedAt == null
                && (e.State == CalendarEventStates.Scheduled
                    || e.State == CalendarEventStates.Open
                    || (e.State == CalendarEventStates.Finished
                        && ((e.Repeat == CalendarRepeats.None && e.EndsAt >= since)
                            || (e.Repeat != CalendarRepeats.None && (e.RepeatUntil == null || e.RepeatUntil >= lastDayFrom || e.RepeatTimes != null))
                            || e.DateChanges.Any(c => c.EndsAt >= since)))
                    || (e.State == CalendarEventStates.Cancelled && e.CancelledAt >= since)));

        // The same rule as CalendarFeedWriter.IsPublic, so far as the database can say it: the
        // visibility only counts while the event goes to VRChat's calendar. The roles are checked
        // by the writer's rule below.
        if (publicOnly)
            query = query.Where(e => e.PublishToVRChat && e.Visibility == "public");

        var candidates = await query.ToListAsync(ct);

        return publicOnly
            ? candidates.Where(e => CalendarFeedWriter.BelongsInPublic(e, now)).ToList()
            : candidates.Where(e => CalendarFeedWriter.Belongs(e, now)).ToList();
    }

    /// <summary>The names Modbot knows for the worlds of <paramref name="events"/>, by world id.</summary>
    internal static async Task<Dictionary<string, string>> FeedWorldNamesAsync(
        ModbotContext db, IReadOnlyList<CalendarEvent> events, CancellationToken ct)
    {
        var worldIds = events.Where(e => e.WorldId != null).Select(e => e.WorldId!).Distinct().ToList();

        return await db.VRChatWorlds.AsNoTracking()
            .Where(w => worldIds.Contains(w.WorldId) && w.Name != null)
            .ToDictionaryAsync(w => w.WorldId, w => w.Name!, StringComparer.Ordinal, ct);
    }

    /// <summary>The calendar's name in a feed: the managed group's, or Modbot.</summary>
    internal static string FeedName(Modbot.Core.Data.Entities.Settings? settings) =>
        string.IsNullOrWhiteSpace(settings?.ManagedGroupName) ? "Modbot" : settings.ManagedGroupName;

    /// <summary>SHA-256 of a feed token, hex. Only the hash is ever matched.</summary>
    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>The join link for an event's open instance, or null when there is none open.</summary>
    public static async Task<string?> JoinLinkAsync(ModbotContext db, Guid eventId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var calendarEvent = await db.CalendarEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventId && e.DeletedAt == null && e.State == CalendarEventStates.Open, ct);

        if (calendarEvent?.OccurrenceStartsAt is not { } occurrence)
            return null;

        var opening = await db.CalendarOpenings.AsNoTracking()
            .FirstOrDefaultAsync(o => o.EventId == eventId && o.OccurrenceStartsAt == occurrence && o.Location != null, ct);

        if (opening?.Location is null)
            return null;

        if (opening.InstanceId is { } instanceId
            && await db.VRChatInstances.AsNoTracking().AnyAsync(i => i.Id == instanceId && i.ClosedAt != null, ct))
        {
            return null;
        }

        return InstanceJoinLink.For(opening.Location);
    }

    private static async Task<CalendarFeedView> FeedViewAsync(ModbotContext db, string? token, CancellationToken ct)
    {
        if (token is null)
            return new CalendarFeedView(null, null);

        var path = $"/api/calendar/feed/{token}.ics";
        var address = await db.Settings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.PublicAddress).FirstOrDefaultAsync(ct);

        return new CalendarFeedView(path, string.IsNullOrWhiteSpace(address) ? null : address.TrimEnd('/') + path);
    }

    /// <summary>
    /// Checks a save and copies it onto the event. Null when it may be saved; otherwise a 400 with
    /// everything wrong at once, in <c>problems</c>, joined in <c>error</c> (calendar design §17.2,
    /// added 2026-10-02).
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the event goes to VRChat's calendar and the group as last read says Modbot's VRChat
    /// account lacks Manage Group Calendar, that comes first: nothing else matters until it is
    /// given. It never refuses a save on its own. The last read may be minutes old, a save never
    /// waits on VRChat to read it again, and the event is still worth saving for its other places;
    /// the VRChat calendar loop reads the group again before it refuses to send (§17.1).
    /// </para>
    /// </remarks>
    /// <param name="keptMention">The role already on the event, before this save changes it.</param>
    /// <param name="keptCover">The Discord picture already on the event, before this save changes it.</param>
    private static async Task<IResult?> RefusedAsync(
        ModbotContext db, CalendarEventRequest body, CalendarEvent target, string? keptMention, Guid? keptCover, CancellationToken ct)
    {
        var problems = ApplyAll(body, target);

        if (await ListProblemAsync(db, body, ct) is { } listProblem)
            problems.Add(listProblem);

        if (await MentionProblemAsync(db, body, keptMention, ct) is { } mentionProblem)
            problems.Add(mentionProblem);

        if (await CoverProblemAsync(db, body, keptCover, ct) is { } coverProblem)
            problems.Add(coverProblem);

        if (problems.Count == 0)
            return null;

        if (body.PublishToVRChat && !body.Draft)
        {
            var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

            if (settings?.ManagedGroupId is { Length: > 0 } groupId && CalendarVRChatChecks.LacksCalendarPermission(settings))
            {
                problems.Insert(0, VRChatGroupPermissions.Sentence(
                    new MissingGroupPermission(VRChatGroupPermissions.ManageCalendar, groupId, null, null)));
            }
        }

        return Results.BadRequest(new { error = string.Join(" ", problems), problems });
    }

    /// <summary>
    /// After a save of the event or of one of its dates: every place that failed goes back to
    /// waiting with its failure cleared, so the event shows it being sent rather than the failure
    /// the save may have fixed, and the loops send it again (calendar design §17.3, added
    /// 2026-10-02). A VRChat create that got no answer keeps its own way (§3.1).
    /// </summary>
    private static async Task ClearFailuresAsync(ModbotContext db, Guid eventId, DateTimeOffset now, CancellationToken ct)
    {
        var failed = await db.CalendarEventPlaces
            .Where(p => p.EventId == eventId && p.State == CalendarPlaceStates.Failed)
            .ToListAsync(ct);

        foreach (var place in failed)
        {
            if (place.Place == CalendarPlaces.VRChat)
                CalendarVRChatPublisher.ClearAfterEdit(place, now);
            else if (place.Place == CalendarPlaces.Google)
                CalendarGooglePublisher.ClearAfterEdit(place, now);
            else
                CalendarDiscordRetry.ClearAfterEdit(place, now);
        }
    }

    /// <summary>What is wrong with the Discord picture an event names, or null (§15.4).</summary>
    private static async Task<string?> CoverProblemAsync(
        ModbotContext db, CalendarEventRequest body, Guid? kept, CancellationToken ct)
    {
        if (body.CoverPictureId is not { } id || id == kept)
            return null;

        return await db.CalendarCoverPictures.AnyAsync(c => c.Id == id, ct)
            ? null
            : "That Discord picture is gone. Choose it again.";
    }

    /// <summary>
    /// Deletes a Discord picture no live event uses any more: its event was deleted or given
    /// another one (§15.4). A copy of an event shares its picture, so the picture stays while any
    /// event still points at it.
    /// </summary>
    private static async Task DropCoverIfUnusedAsync(ModbotContext db, Guid? coverId, CancellationToken ct)
    {
        if (coverId is not { } id)
            return;

        if (await db.CalendarEvents.AnyAsync(e => e.CoverPictureId == id && e.DeletedAt == null, ct))
            return;

        // A post may use the same picture (posts design §2.2): it stays while one does.
        if (await db.Posts.AnyAsync(p => p.PictureId == id, ct))
            return;

        await db.CalendarCoverPictures.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
    }

    /// <summary>What is wrong with the world list an event names, or null.</summary>
    private static async Task<string?> ListProblemAsync(ModbotContext db, CalendarEventRequest body, CancellationToken ct)
    {
        if (body.WorldListId is not { } listId)
            return null;

        if (!await db.WorldLists.AnyAsync(l => l.Id == listId, ct))
            return "That world list does not exist.";

        if (!body.Draft && !await db.WorldListItems.AnyAsync(i => i.ListId == listId, ct))
            return "That world list has no worlds.";

        return null;
    }

    /// <summary>
    /// The role the channel post mentions (calendar design §3.3.1): one in the server in settings
    /// that the bot may mention, and never @everyone. Read from Modbot's copy of the role list, so
    /// it answers while the bot is offline.
    /// </summary>
    /// <param name="kept">
    /// The role already on the event. Keeping it is allowed even when the bot may no longer mention
    /// it, so an unrelated edit is not refused for a change made in Discord; @everyone never is.
    /// </param>
    public static async Task<string?> MentionProblemAsync(
        ModbotContext db, CalendarEventRequest body, string? kept, CancellationToken ct)
    {
        var roleId = body.MentionRoleId?.Trim();
        if (string.IsNullOrEmpty(roleId))
            return null;

        var guildId = (await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordGuildId)
            .FirstOrDefaultAsync(ct))?.Trim();

        if (string.Equals(roleId, guildId, StringComparison.Ordinal))
            return "The post cannot mention @everyone.";

        var role = string.IsNullOrEmpty(guildId)
            ? null
            : await db.DiscordRoles.AsNoTracking().FirstOrDefaultAsync(r => r.RoleId == roleId && r.GuildId == guildId, ct);

        if (role is { Everyone: true })
            return "The post cannot mention @everyone.";

        if (string.Equals(roleId, kept, StringComparison.Ordinal))
            return null;

        if (role is null || role.RemovedAt is not null)
            return "That role is not in the Discord server.";

        var server = await db.DiscordServers.AsNoTracking().FirstOrDefaultAsync(s => s.GuildId == guildId, ct);

        return DiscordRole.BotCanMention(role, server) ? null : "The bot may not mention that role.";
    }

    /// <summary>
    /// Picks the current date's world straight after a save, so the page shows it at once rather than
    /// after the scheduler's next pass (world lists design §5). Modbot picks it, not the person who
    /// saved: the scheduler would have picked the same way a moment later.
    /// </summary>
    internal static async Task PickDateAsync(
        ModbotContext db, AccountFacts facts, IModbotClock clock, CalendarEvent calendarEvent, CancellationToken ct)
    {
        if (!WorldPicker.NeedsDatePick(calendarEvent))
            return;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var result = await new WorldPicker(db, clock).PickForDateAsync(calendarEvent, by: null, ct);
        await db.SaveChangesAsync(ct);

        if (result is { Outcome: WorldPickOutcome.Picked })
        {
            await facts.RecordAsync(
                FactType.CalendarWorldPicked,
                calendarEvent.Id.ToString(),
                actor: null,
                await WorldPicker.FactDataAsync(db, calendarEvent, result, again: false, ct),
                ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Checks who the event invites against what exists and what the caller may pick (calendar
    /// auto-invite design §9). Null when it is fine.
    /// </summary>
    /// <param name="keptListId">The list already on the event: keeping it needs no more than editing the event.</param>
    /// <param name="kept">
    /// The staff accounts already on the event. Not checked again, so an account disabled since does
    /// not stop an unrelated edit; the invites leave a disabled account out anyway.
    /// </param>
    internal static async Task<IResult?> CheckInvitesAsync(
        HttpContext http,
        ModbotContext db,
        CalendarEventRequest body,
        Guid? keptListId,
        IReadOnlyCollection<Guid> kept,
        CancellationToken ct)
    {
        var accounts = new List<Guid>();

        if (body.InviteHostUserId is { } host)
            accounts.Add(host);

        accounts.AddRange(body.InviteStaffUserIds ?? []);
        accounts = [.. accounts.Distinct().Where(id => !kept.Contains(id))];

        if (accounts.Count > 0)
        {
            var found = await db.Users.AsNoTracking()
                .CountAsync(u => accounts.Contains(u.Id) && u.DeletedAt == null && !u.IsDisabled, ct);

            if (found != accounts.Count)
                return Results.BadRequest(new { error = "That staff account does not exist." });
        }

        if (body.InviteListId is not { } listId || listId == keptListId)
            return null;

        if (!ModbotAuth.Allows(ModbotAuth.PermissionsOf(http.User), ToPickAList))
        {
            return Results.Json(
                new { error = "Picking a list needs See members and See profiles." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var exists = await db.SavedLists.AsNoTracking().AnyAsync(l => l.Id == listId && l.DeletedAt == null, ct);
        return exists ? null : Results.BadRequest(new { error = "That list does not exist." });
    }

    /// <summary>Works out the event's state and current occurrence; says so when nothing is left of it.</summary>
    private static string? Place(CalendarEvent calendarEvent, DateTimeOffset now) =>
        CalendarTimeline.Advance(calendarEvent, now) == CalendarStep.Finished
            ? "That event has already ended."
            : null;

    /// <summary>Why one date of this event cannot be cancelled or changed now, or null when it can.</summary>
    private static IResult? DateProblem(CalendarEvent calendarEvent, DateTimeOffset planned, DateTimeOffset now) =>
        CalendarCancellations.DateProblem(calendarEvent, planned, now) is { } problem ? Refusal(problem.Status, problem.Error) : null;

    /// <summary>A cancel's result as the endpoints have always answered it: 204, 404, or 400 or 409 with the words.</summary>
    private static IResult CancelAnswer(CalendarCancelResult result) => result.Status switch
    {
        CalendarCancelStatus.Done or CalendarCancelStatus.AlreadyCancelled => Results.NoContent(),
        CalendarCancelStatus.NotFound => Results.NotFound(),
        _ => Refusal(result.HttpStatus, result.Error ?? string.Empty),
    };

    private static IResult Refusal(int status, string error) => status == StatusCodes.Status409Conflict
        ? Results.Conflict(new { error })
        : Results.BadRequest(new { error });

    /// <summary>Checks a change to one date. Returns what is wrong, or null.</summary>
    private static string? ApplyDate(CalendarDateRequest body, CalendarEvent calendarEvent, CalendarOccurrence was, DateTimeOffset now)
    {
        if (body.EndsAt <= body.StartsAt)
            return "The end must be after the start.";

        if (body.EndsAt - body.StartsAt > MaxLength)
            return "An event can last at most 7 days.";

        if (body.EndsAt <= now)
            return "That time has already passed.";

        // A date that has opened has started its Discord event, which Discord cannot move; its end
        // can still change.
        if (CalendarRepeat.OpensAt(calendarEvent, was) <= now && body.StartsAt != was.StartsAt)
            return "That date has already started.";

        if (body.Title?.Trim() is { Length: > CalendarEvent.MaxTitleLength })
            return $"The title is longer than {CalendarEvent.MaxTitleLength} characters.";

        if (body.Description?.Trim() is { Length: > CalendarEvent.MaxDescriptionLength })
            return $"The description is longer than {CalendarEvent.MaxDescriptionLength} characters.";

        // Two dates starting together could not be told apart by the places that name a date by
        // its start: the instance opened for it, and the page.
        var clash = CalendarRepeat.Between(calendarEvent, body.StartsAt - TimeSpan.FromTicks(1), body.StartsAt + TimeSpan.FromTicks(1))
            .Any(o => o.StartsAt == body.StartsAt && o.PlannedStartsAt != was.PlannedStartsAt);

        if (clash)
            return "Another date of this event starts then.";

        // Modbot keeps dates in order however far one moves, but whether VRChat takes one of its
        // dates moved past another is not known (calendar design §10). An event on VRChat's
        // calendar keeps each date between its neighbours, by their planned starts.
        if (calendarEvent.PublishToVRChat && PastANeighbour(calendarEvent, was, body.StartsAt) is { } past)
            return past;

        return null;
    }

    /// <summary>What is wrong when a date would start at or past the date before or after it, or null.</summary>
    private static string? PastANeighbour(CalendarEvent calendarEvent, CalendarOccurrence was, DateTimeOffset startsAt)
    {
        var planned = was.PlannedStartsAt;
        var length = CalendarRepeat.LengthOf(calendarEvent);

        // The planned dates either side: the one just before, and the one just after.
        DateTimeOffset? before = null;
        DateTimeOffset? after = null;

        foreach (var date in CalendarRepeat.PlannedBetween(calendarEvent, planned - TimeSpan.FromDays(400) - length, planned + TimeSpan.FromDays(400)))
        {
            if (date.StartsAt < planned)
                before = date.StartsAt;
            else if (date.StartsAt > planned)
            {
                after = date.StartsAt;
                break;
            }
        }

        if (after is { } next && startsAt >= next)
            return "Can't move a date past the next date on VRChat.";

        if (before is { } previous && startsAt <= previous)
            return "Can't move a date past the date before it on VRChat.";

        return null;
    }

    /// <summary>A date's own words: null when empty or the same as the event's.</summary>
    private static string? Own(string? text, string eventText)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, eventText, StringComparison.Ordinal) ? null : trimmed;
    }

    private static JsonObject DateFields(CalendarEvent calendarEvent, CalendarOccurrence date) => new()
    {
        ["startsAt"] = date.StartsAt.ToString("O", CultureInfo.InvariantCulture),
        ["endsAt"] = date.EndsAt.ToString("O", CultureInfo.InvariantCulture),
        ["title"] = CalendarRepeat.TitleOf(calendarEvent, date),
        ["description"] = CalendarRepeat.DescriptionOf(calendarEvent, date),
    };

    /// <summary>Checks a request and copies it onto the event. Returns the first thing wrong, or null.</summary>
    /// <param name="preview">
    /// For the form's preview: a piece not filled in yet -- the title, the description VRChat needs,
    /// the channel, the world to open -- is let through, so the rest can still be drawn. Everything
    /// that would be refused for its shape or its length is still refused.
    /// </param>
    public static string? Apply(CalendarEventRequest body, CalendarEvent target, bool preview = false) =>
        ApplyAll(body, target, preview) is [var first, ..] ? first : null;

    /// <summary>
    /// Checks a request and copies it onto the event when nothing is wrong. Returns everything that
    /// is wrong, in the form's order, so a save is refused once with all of it (calendar design
    /// §17.2, added 2026-10-02); before, it stopped at the first, and a second save found the next.
    /// </summary>
    /// <remarks>
    /// A check that needs an earlier one to have passed -- the end against a start that is not a
    /// time -- is left out while that one fails, rather than said twice.
    /// </remarks>
    /// <param name="preview">As for <see cref="Apply"/>.</param>
    public static List<string> ApplyAll(CalendarEventRequest body, CalendarEvent target, bool preview = false)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(target);

        var problems = new List<string>();

        var title = body.Title?.Trim() ?? string.Empty;
        if (title.Length == 0 && !preview)
            problems.Add(CalendarVRChatChecks.NoTitle);

        if (title.Length > CalendarEvent.MaxTitleLength)
            problems.Add($"The title is longer than {CalendarEvent.MaxTitleLength} characters.");

        var description = body.Description?.Trim() ?? string.Empty;
        if (description.Length > CalendarEvent.MaxDescriptionLength)
            problems.Add($"The description is longer than {CalendarEvent.MaxDescriptionLength} characters.");

        // VRChat refuses a calendar event with no description (400, seen 2026-09-25). A draft is
        // never sent, so it may stay empty until it is published.
        if (body.PublishToVRChat && !body.Draft && description.Length == 0 && !preview)
            problems.Add(CalendarVRChatChecks.NoDescription);

        var zone = CalendarRepeat.FindZone(body.TimeZone);
        if (zone is null)
            problems.Add("That time zone is not known.");

        var startLocal = ParseLocal(body.StartsAt);
        if (startLocal is null)
            problems.Add("The start is not a date and time.");

        var endLocal = ParseLocal(body.EndsAt);
        if (endLocal is null)
            problems.Add("The end is not a date and time.");

        DateTimeOffset starts = default;
        DateTimeOffset ends = default;

        if (zone is not null && startLocal is { } startAt && endLocal is { } endAt)
        {
            starts = zone.AtLeniently(startAt).ToInstant().ToDateTimeOffset();
            ends = zone.AtLeniently(endAt).ToInstant().ToDateTimeOffset();

            if (ends <= starts)
                problems.Add("The end must be after the start.");
            else if (ends - starts > MaxLength)
                problems.Add("An event can last at most 7 days.");
        }

        var repeat = string.IsNullOrWhiteSpace(body.Repeat) ? CalendarRepeats.None : body.Repeat.Trim();
        if (!CalendarRepeats.All.Contains(repeat))
            problems.Add("Repeat must be none, daily, weekly or monthly.");

        var days = (body.RepeatDays ?? []).Select(d => d.Trim().ToUpperInvariant()).Distinct().ToList();
        if (days.Any(d => !CalendarRepeat.IsDayName(d)))
            problems.Add("Repeat days must be MO, TU, WE, TH, FR, SA or SU.");

        DateOnly? until = null;
        if (!string.IsNullOrWhiteSpace(body.RepeatUntil))
        {
            if (!DateOnly.TryParseExact(body.RepeatUntil.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                problems.Add("The last date is not a date.");
            else if (startLocal is { } firstDay && parsed < DateOnly.FromDateTime(firstDay.ToDateTimeUnspecified()))
                problems.Add("The last date is before the start.");
            else
                until = parsed;
        }

        // Only a repeating event has them; a one-off ignores whatever the form still held.
        var every = repeat == CalendarRepeats.None ? 1 : body.RepeatEvery ?? 1;
        if (every is < 1 or > CalendarRepeats.MaxEvery)
            problems.Add($"Repeat every must be between 1 and {CalendarRepeats.MaxEvery}.");

        var times = repeat == CalendarRepeats.None ? null : body.RepeatTimes;
        if (times is < 1 or > CalendarRepeats.MaxTimes)
            problems.Add($"The number of times must be between 1 and {CalendarRepeats.MaxTimes}.");

        // One end or the other, as iCalendar's UNTIL and COUNT and VRChat's own end are.
        if (until is not null && times is not null)
            problems.Add("Pick a last date or a number of times, not both.");

        var access = body.AccessType?.Trim() ?? "members";
        if (!AccessTypes.Contains(access))
            problems.Add("Who can join must be members, plus or public.");

        var region = body.Region?.Trim() ?? "us";
        if (!Regions.Contains(region))
            problems.Add("The region must be us, use, eu or jp.");

        var visibility = body.Visibility?.Trim() ?? "group";
        if (!Visibilities.Contains(visibility))
            problems.Add("Visibility must be group or public.");

        var category = string.IsNullOrWhiteSpace(body.Category) ? "hangout" : body.Category.Trim();
        if (!CalendarVRChatRequests.Categories.Contains(category))
            problems.Add("That is not one of VRChat's categories.");

        var platforms = (body.Platforms ?? []).Select(p => p.Trim()).Distinct().ToList();
        if (platforms.Any(p => !CalendarVRChatRequests.Platforms.Contains(p)))
            problems.Add("That is not one of VRChat's platforms.");

        if (List(body.Languages, "languages") is { } languageProblem)
            problems.Add(languageProblem);

        if (List(body.Tags, "tags") is { } tagProblem)
            problems.Add(tagProblem);

        var imageUrl = string.IsNullOrWhiteSpace(body.ImageUrl) ? null : body.ImageUrl.Trim();
        if (imageUrl is not null)
        {
            if (imageUrl.Length > 2048
                || !Uri.TryCreate(imageUrl, UriKind.Absolute, out var picture)
                || picture.Scheme != Uri.UriSchemeHttps)
            {
                problems.Add("The picture link must start with https://.");
            }
            else if (PublicAddresses.IsBlockedHost(picture.Host))
            {
                problems.Add("The picture link points at a private address.");
            }
        }

        // Whatever was pasted, a link from VRChat's site or the id itself, the id inside it is what
        // is kept (§15.1). An id the event already holds is kept as it is, so an id VRChat issued in
        // some older form never stops an edit (foundation §3.1.1).
        //
        // Refused only while the event goes to VRChat: the box is hidden otherwise, and a problem in
        // a box nobody can see could not be fixed (§17.2). Off, what was typed is kept as it is, as
        // before, and judged once VRChat is turned on. On, the save refuses exactly what sending
        // would: text with no file id in it, and, for an id the event already holds, what
        // CalendarVRChatChecks.PictureIdProblem refuses before VRChat is asked -- so nothing a save
        // lets through is then refused by the publisher's own check.
        var typedPicture = string.IsNullOrWhiteSpace(body.VRChatImageId) ? null : body.VRChatImageId.Trim();
        var foundPicture = typedPicture is null || typedPicture == target.VRChatImageId
            ? typedPicture
            : Core.Files.VRChatFileIds.Find(typedPicture);

        if (body.PublishToVRChat && typedPicture is not null)
        {
            if (foundPicture is null)
                problems.Add(Core.Files.VRChatFileIds.NotFound);
            else if (CalendarVRChatChecks.PictureIdProblem(foundPicture) is { } pictureIdProblem)
                problems.Add(pictureIdProblem);
        }

        var vrchatImageId = foundPicture ?? typedPicture;

        var worldId = string.IsNullOrWhiteSpace(body.WorldId) ? null : body.WorldId.Trim();
        var channelId = string.IsNullOrWhiteSpace(body.ChannelId) ? null : body.ChannelId.Trim();

        if (body.PostToChannel && channelId is null && !preview)
            problems.Add("Pick a channel to post to.");

        if (body.AutoOpen && worldId is null && body.WorldListId is null && !preview)
            problems.Add("Pick a world to open the instance in.");

        var openBefore = body.OpenMinutesBefore ?? 10;
        if (openBefore is < 0 or > MaxOpenMinutesBefore)
            problems.Add($"The instance can open between 0 and {MaxOpenMinutesBefore} minutes early.");

        // The post goes in the channel post's channel; without one there is nowhere to say it.
        if (body.AnnounceFirstJoinInDiscord && (!body.PostToChannel || channelId is null) && !preview)
            problems.Add("Posting when the first person joins needs a channel post.");

        var staff = (body.InviteStaffUserIds ?? []).Distinct().ToList();
        if (staff.Count > MaxInviteStaff)
            problems.Add($"At most {MaxInviteStaff} staff.");

        if (problems.Count > 0)
            return problems;

        target.Title = title;
        target.Description = description;
        target.StartsAt = starts;
        target.EndsAt = ends;
        target.TimeZone = zone!.Id;
        target.Repeat = repeat;
        target.RepeatEvery = every;
        target.RepeatUntil = repeat == CalendarRepeats.None ? null : until;
        target.RepeatTimes = times;

        // A world picked from a list stays the current date's world through an edit; another list
        // means a new pick (world lists design §5), made once the event is saved.
        if (body.WorldListId is { } listId)
        {
            if (target.WorldListId != listId)
            {
                target.WorldId = null;
                target.WorldPickedFor = null;
            }

            target.WorldListId = listId;
        }
        else
        {
            target.WorldListId = null;
            target.WorldPickedFor = null;
            target.WorldId = worldId;
        }

        target.AccessType = access;
        target.Region = region;
        target.ImageUrl = imageUrl;
        target.VRChatImageId = vrchatImageId;
        target.CoverPictureId = body.CoverPictureId;
        target.Category = category;
        target.Languages = Clean(body.Languages);
        target.Platforms = platforms;
        target.Tags = Clean(body.Tags);
        target.Visibility = visibility;
        target.NotifyMembers = body.NotifyMembers;
        target.Featured = body.Featured ?? target.Featured;
        target.PublishToVRChat = body.PublishToVRChat;
        target.PublishToDiscord = body.PublishToDiscord;
        target.PublishToGoogle = body.PublishToGoogle ?? target.PublishToGoogle;
        target.PostToChannel = body.PostToChannel;
        target.ChannelId = channelId;
        target.MentionRoleId = string.IsNullOrWhiteSpace(body.MentionRoleId) ? null : body.MentionRoleId.Trim();
        target.AutoOpen = body.AutoOpen;
        target.OpenMinutesBefore = openBefore;
        target.InviteHostUserId = body.InviteHostUserId;
        target.InviteStaffUserIds = staff;
        target.InviteListId = body.InviteListId;
        target.AnnounceFirstJoinInDiscord = body.AnnounceFirstJoinInDiscord;
        target.AnnounceFirstJoinInVRChat = body.AnnounceFirstJoinInVRChat;

        // The start's own day is always one of a weekly event's days, so every place agrees on the
        // first occurrence.
        if (repeat == CalendarRepeats.Weekly)
        {
            var first = CalendarRepeat.DayName(startLocal!.Value.DayOfWeek);
            if (!days.Contains(first))
                days.Add(first);

            target.RepeatDays = [.. CalendarRepeats.Days.Where(days.Contains)];
        }
        else
        {
            target.RepeatDays = [];
        }

        return problems;
    }

    private static string? List(IReadOnlyList<string>? values, string what)
    {
        var cleaned = Clean(values);

        if (cleaned.Count > MaxListItems)
            return $"At most {MaxListItems} {what}.";

        return cleaned.Any(v => v.Length > MaxListItemLength)
            ? $"Each of the {what} can be at most {MaxListItemLength} characters."
            : null;
    }

    private static List<string> Clean(IReadOnlyList<string>? values) =>
        [.. (values ?? []).Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal)];

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

    private static JsonObject Describe(CalendarEvent e) => CalendarEventFields.Of(e);

    internal static async Task<List<CalendarEventView>> ViewsAsync(
        ModbotContext db, IReadOnlyList<CalendarEvent> events, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var ids = events.Select(e => e.Id).ToList();
        var worldIds = events.Where(e => e.WorldId != null).Select(e => e.WorldId!).Distinct().ToList();

        var places = await db.CalendarEventPlaces.AsNoTracking()
            .Where(p => ids.Contains(p.EventId))
            .ToListAsync(ct);

        // Read only when a place was refused for a missing VRChat group permission, for the group id
        // and the account's roles that refusal names.
        var settings = places.Any(p => p.MissingGroupPermission != null)
            ? await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct)
            : null;

        var openings = await db.CalendarOpenings.AsNoTracking()
            .Where(o => ids.Contains(o.EventId))
            .ToListAsync(ct);

        var instanceIds = openings.Where(o => o.InstanceId != null).Select(o => o.InstanceId!.Value).ToList();
        var closedInstances = await db.VRChatInstances.AsNoTracking()
            .Where(i => instanceIds.Contains(i.Id) && i.ClosedAt != null)
            .Select(i => i.Id)
            .ToListAsync(ct);

        var worlds = await db.VRChatWorlds.AsNoTracking()
            .Where(w => worldIds.Contains(w.WorldId))
            .ToDictionaryAsync(w => w.WorldId, StringComparer.Ordinal, ct);

        var listIds = events.Where(e => e.WorldListId != null).Select(e => e.WorldListId!.Value).Distinct().ToList();
        var listNames = listIds.Count == 0
            ? []
            : await db.WorldLists.AsNoTracking()
                .Where(l => listIds.Contains(l.Id))
                .ToDictionaryAsync(l => l.Id, l => l.Name, ct);

        var filledLists = listIds.Count == 0
            ? []
            : await db.WorldListItems.AsNoTracking()
                .Where(i => listIds.Contains(i.ListId))
                .Select(i => i.ListId)
                .Distinct()
                .ToListAsync(ct);
        var inviteListIds = events.Where(e => e.InviteListId != null).Select(e => e.InviteListId!.Value).Distinct().ToList();
        var inviteListNames = inviteListIds.Count == 0
            ? []
            : await db.SavedLists.AsNoTracking()
                .Where(l => inviteListIds.Contains(l.Id) && l.DeletedAt == null)
                .ToDictionaryAsync(l => l.Id, l => l.Name, ct);

        // Only the states, counted per time: who was invited is the audit log's to show.
        var inviteStates = await db.CalendarInvites.AsNoTracking()
            .Where(i => ids.Contains(i.EventId))
            .Select(i => new { i.EventId, i.OccurrenceStartsAt, i.State })
            .ToListAsync(ct);

        return [.. events.Select(e =>
        {
            var zone = CalendarRepeat.ZoneOf(e);
            var world = e.WorldId is { } w && worlds.TryGetValue(w, out var found) ? found : null;
            var length = e.EndsAt - e.StartsAt;

            var opening = e.OccurrenceStartsAt is { } current
                ? openings.FirstOrDefault(o => o.EventId == e.Id && o.OccurrenceStartsAt == current)
                : null;

            var closed = opening?.InstanceId is { } instance && closedInstances.Contains(instance);

            CalendarInvitesView? invites = null;
            if (opening?.InvitesQueuedAt is not null)
            {
                var counts = CalendarInviteCounts.From(inviteStates
                    .Where(i => i.EventId == e.Id && i.OccurrenceStartsAt == opening.OccurrenceStartsAt)
                    .Select(i => i.State));

                invites = new CalendarInvitesView(
                    counts.Total, counts.Invited, counts.VRChat, counts.Discord, counts.CouldNotReach,
                    counts.NoWay, counts.Waiting, counts.Stopped, counts.Skipped, counts.NotAsked);
            }

            var occurrences = e.State == CalendarEventStates.Cancelled
                ? []
                : CalendarRepeat.Between(e, from, to).Take(100).Select(OccurrenceView).ToList();

            var cancelledDates = e.State == CalendarEventStates.Cancelled
                ? []
                : CalendarRepeat.CancelledBetween(e, from, to).Take(100).Select(OccurrenceView).ToList();

            // The date the event is dealing with keeps its own end when it was moved on its own.
            var currentDate = e.OccurrenceStartsAt is not null ? CalendarRepeat.Current(e) : (CalendarOccurrence?)null;

            return new CalendarEventView(
                e.Id,
                e.Title,
                e.Description,
                e.StartsAt,
                e.EndsAt,
                LocalText(e.StartsAt, zone),
                LocalText(e.EndsAt, zone),
                e.TimeZone,
                e.Repeat,
                e.RepeatDays,
                e.RepeatUntil?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                e.WorldId,
                world?.Name,
                world?.ThumbnailImageUrl,
                e.AccessType,
                e.Region,
                e.ImageUrl,
                e.VRChatImageId,
                e.Category,
                e.Languages,
                e.Platforms,
                e.Tags,
                e.Visibility,
                e.NotifyMembers,
                e.PublishToVRChat,
                e.PublishToDiscord,
                e.PostToChannel,
                e.ChannelId,
                e.AutoOpen,
                e.OpenMinutesBefore,
                e.State,
                e.OccurrenceStartsAt,
                currentDate?.EndsAt ?? e.OccurrenceStartsAt + length,
                e.Version,
                e.CreatedAt,
                e.UpdatedAt,
                e.MadeOnVRChat,
                [.. places
                    .Where(p => p.EventId == e.Id)
                    .OrderBy(p => p.Place, StringComparer.Ordinal)
                    .Select(p =>
                    {
                        // Found before sending: nothing of VRChat's own to quote.
                        var problems = p.State == CalendarPlaceStates.Failed ? p.Problems : null;

                        return new CalendarPlaceView(
                            p.Place,
                            p.State,
                            p.Error,
                            p.ErrorAt,
                            p.UpdatedAt,
                            p.MissingGroupPermission is { } permission && settings?.ManagedGroupId is { Length: > 0 } groupId
                                ? new MissingGroupPermission(permission, groupId, VRChatGroupPermissions.RoleNames(settings), problems is null ? p.Error : null)
                                : null,
                            p.State == CalendarPlaceStates.Failed,
                            problems,
                            p.Place == CalendarPlaces.Google && p.State == CalendarPlaceStates.Published ? p.GoogleLink : null);
                    })],
                opening is null
                    ? null
                    : new CalendarOpeningView(
                        opening.OccurrenceStartsAt,
                        opening.AttemptedAt,
                        opening.InstanceId,
                        closed ? null : InstanceJoinLink.For(opening.Location),
                        closed,
                        opening.Error,
                        opening.FirstJoinDiscordPostError,
                        opening.FirstJoinVRChatPostError,
                        opening.Checking,
                        opening.Location is not null),
                occurrences,
                e.CancelledAt,
                e.WorldListId,
                e.WorldListId is { } list ? listNames.GetValueOrDefault(list) : null,
                e.WorldListId is { } emptyCheck && !filledLists.Contains(emptyCheck),
                cancelledDates,
                e.InviteHostUserId,
                e.InviteStaffUserIds,
                e.InviteListId,
                e.InviteListId is { } listId && inviteListNames.TryGetValue(listId, out var listName) ? listName : null,
                e.AnnounceFirstJoinInDiscord,
                invites,
                e.AnnounceFirstJoinInVRChat,
                e.MentionRoleId,
                CalendarRepeat.EveryOf(e),
                e.RepeatTimes,
                e.Featured,
                e.CoverPictureId,
                e.PublishToGoogle,
                e.VRChatRoleIds is { Count: > 0 } roles ? roles : null);
        })];
    }

    private static CalendarOccurrenceView OccurrenceView(CalendarOccurrence o) => new(
        o.StartsAt,
        o.EndsAt,
        o.PlannedStartsAt,
        o.Change?.Title,
        o.Change?.Description,
        o.Change?.VRChatError,
        o.Change?.GoogleError);

    private static string LocalText(DateTimeOffset at, DateTimeZone zone) =>
        LocalPattern.Format(Instant.FromDateTimeOffset(at).InZone(zone).LocalDateTime);
}
