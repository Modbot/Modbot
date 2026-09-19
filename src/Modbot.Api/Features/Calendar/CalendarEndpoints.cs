using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Net;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.VRChat.Calendar;
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

        var group = app.MapGroup("/api/calendar").WithTags("Calendar");

        group.MapGet("/", async (
                HttpContext http,
                [FromQuery] DateTimeOffset? from,
                [FromQuery] DateTimeOffset? to,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
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

                return Results.Ok(new CalendarView(
                    views,
                    ModbotAuth.Allows(ModbotAuth.PermissionsOf(http.User), ModbotPermissions.ManageCalendar),
                    CalendarVRChatRequests.Categories,
                    CalendarVRChatRequests.Platforms,
                    now));
            })
            .RequiresFlag(ModbotPermissions.ViewCalendar)
            .WithName("GetCalendar")
            .WithSummary("Get calendar")
            .WithDescription(
                "Every event, with its occurrences in the range, where it is published and how that "
                + "went.")
            .Produces<CalendarView>()
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

                if (Apply(body, calendarEvent) is { } problem)
                    return Results.BadRequest(new { error = problem });

                calendarEvent.State = body.Draft ? CalendarEventStates.Draft : CalendarEventStates.Scheduled;

                if (!body.Draft && Place(calendarEvent, now) is { } ended)
                    return Results.BadRequest(new { error = ended });

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.CalendarEvents.Add(calendarEvent);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.PlannedEventCreated, calendarEvent.Id.ToString(), Actor.Of(http), Describe(calendarEvent), ct);

                await transaction.CommitAsync(ct);

                var views = await ViewsAsync(db, [calendarEvent], now, now.AddDays(42), ct);
                return Results.Ok(views[0]);
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("CreateCalendarEvent")
            .WithSummary("Add calendar event")
            .WithDescription("Plan an event.")
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

                if (Apply(body, calendarEvent) is { } problem)
                    return Results.BadRequest(new { error = problem });

                if (!body.Draft)
                {
                    // A draft being published, or a finished event given new dates, starts again.
                    if (calendarEvent.State is CalendarEventStates.Draft or CalendarEventStates.Finished)
                        calendarEvent.State = CalendarEventStates.Scheduled;

                    // Moved or shortened: the current occurrence is worked out again from the new rule.
                    calendarEvent.OccurrenceStartsAt = null;

                    if (Place(calendarEvent, now) is { } ended)
                        return Results.BadRequest(new { error = ended });
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

                var views = await ViewsAsync(db, [calendarEvent], now, now.AddDays(42), ct);
                return Results.Ok(views[0]);
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("UpdateCalendarEvent")
            .WithSummary("Update calendar event")
            .WithDescription("Publishing follows on its own. Several changes close together are sent to VRChat as one.")
            .Produces<CalendarEventView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/events/{id:guid}/cancel", async (
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

                if (calendarEvent.State == CalendarEventStates.Cancelled)
                    return Results.NoContent();

                var now = clock.UtcNow;
                calendarEvent.State = CalendarEventStates.Cancelled;
                calendarEvent.CancelledAt = now;
                calendarEvent.UpdatedAt = now;
                calendarEvent.Version++;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);
                await facts.RecordAsync(
                    FactType.PlannedEventCancelled, calendarEvent.Id.ToString(), Actor.Of(http),
                    new JsonObject { ["title"] = calendarEvent.Title }, ct);
                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("CancelCalendarEvent")
            .WithSummary("Cancel calendar event")
            .WithDescription("Cancel an event. It is taken off VRChat's calendar and ended in Discord.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

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

                return Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("DeleteCalendarEvent")
            .WithSummary("Delete calendar event")
            .WithDescription("Delete an event. It is taken off everywhere it was published.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

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

                var events = await db.CalendarEvents.AsNoTracking()
                    .Where(e => e.DeletedAt == null
                        && (e.State == CalendarEventStates.Scheduled || e.State == CalendarEventStates.Open))
                    .ToListAsync(ct);

                var worldIds = events.Where(e => e.WorldId != null).Select(e => e.WorldId!).Distinct().ToList();
                var names = await db.VRChatWorlds.AsNoTracking()
                    .Where(w => worldIds.Contains(w.WorldId) && w.Name != null)
                    .ToDictionaryAsync(w => w.WorldId, w => w.Name!, StringComparer.Ordinal, ct);

                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
                var name = string.IsNullOrWhiteSpace(settings?.ManagedGroupName) ? "Modbot" : settings.ManagedGroupName;

                var body = CalendarFeedWriter.Write(name, events, names, clock.UtcNow);
                return Results.Text(body, "text/calendar; charset=utf-8", Encoding.UTF8);
            })
            .AllowAnonymous()
            .WithName("GetCalendarFeedFile")
            .WithSummary("Get calendar feed file")
            .WithDescription(
                "The calendar feed as iCalendar. No sign-in: the token in the address is the key.")
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

    /// <summary>Works out the event's state and current occurrence; says so when nothing is left of it.</summary>
    private static string? Place(CalendarEvent calendarEvent, DateTimeOffset now) =>
        CalendarTimeline.Advance(calendarEvent, now) == CalendarStep.Finished
            ? "That event has already ended."
            : null;

    /// <summary>Checks a request and copies it onto the event. Returns what is wrong, or null.</summary>
    public static string? Apply(CalendarEventRequest body, CalendarEvent target)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(target);

        var title = body.Title?.Trim() ?? string.Empty;
        if (title.Length == 0)
            return "An event needs a title.";

        if (title.Length > CalendarEvent.MaxTitleLength)
            return $"The title is longer than {CalendarEvent.MaxTitleLength} characters.";

        var description = body.Description?.Trim() ?? string.Empty;
        if (description.Length > CalendarEvent.MaxDescriptionLength)
            return $"The description is longer than {CalendarEvent.MaxDescriptionLength} characters.";

        if (CalendarRepeat.FindZone(body.TimeZone) is not { } zone)
            return "That time zone is not known.";

        if (ParseLocal(body.StartsAt) is not { } startLocal)
            return "The start is not a date and time.";

        if (ParseLocal(body.EndsAt) is not { } endLocal)
            return "The end is not a date and time.";

        var starts = zone.AtLeniently(startLocal).ToInstant().ToDateTimeOffset();
        var ends = zone.AtLeniently(endLocal).ToInstant().ToDateTimeOffset();

        if (ends <= starts)
            return "The end must be after the start.";

        if (ends - starts > MaxLength)
            return "An event can last at most 7 days.";

        var repeat = string.IsNullOrWhiteSpace(body.Repeat) ? CalendarRepeats.None : body.Repeat.Trim();
        if (!CalendarRepeats.All.Contains(repeat))
            return "Repeat must be none, daily, weekly or monthly.";

        var days = (body.RepeatDays ?? []).Select(d => d.Trim().ToUpperInvariant()).Distinct().ToList();
        if (days.Any(d => !CalendarRepeat.IsDayName(d)))
            return "Repeat days must be MO, TU, WE, TH, FR, SA or SU.";

        DateOnly? until = null;
        if (!string.IsNullOrWhiteSpace(body.RepeatUntil))
        {
            if (!DateOnly.TryParseExact(body.RepeatUntil.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return "The last date is not a date.";

            if (parsed < DateOnly.FromDateTime(startLocal.ToDateTimeUnspecified()))
                return "The last date is before the start.";

            until = parsed;
        }

        var access = body.AccessType?.Trim() ?? "members";
        if (!AccessTypes.Contains(access))
            return "Who can join must be members, plus or public.";

        var region = body.Region?.Trim() ?? "us";
        if (!Regions.Contains(region))
            return "The region must be us, use, eu or jp.";

        var visibility = body.Visibility?.Trim() ?? "group";
        if (!Visibilities.Contains(visibility))
            return "Visibility must be group or public.";

        var category = string.IsNullOrWhiteSpace(body.Category) ? "hangout" : body.Category.Trim();
        if (!CalendarVRChatRequests.Categories.Contains(category))
            return "That is not one of VRChat's categories.";

        var platforms = (body.Platforms ?? []).Select(p => p.Trim()).Distinct().ToList();
        if (platforms.Any(p => !CalendarVRChatRequests.Platforms.Contains(p)))
            return "That is not one of VRChat's platforms.";

        if (List(body.Languages, "languages") is { } languageProblem)
            return languageProblem;

        if (List(body.Tags, "tags") is { } tagProblem)
            return tagProblem;

        var imageUrl = string.IsNullOrWhiteSpace(body.ImageUrl) ? null : body.ImageUrl.Trim();
        if (imageUrl is not null)
        {
            if (imageUrl.Length > 2048
                || !Uri.TryCreate(imageUrl, UriKind.Absolute, out var picture)
                || picture.Scheme != Uri.UriSchemeHttps)
            {
                return "The picture link must start with https://.";
            }

            if (PublicAddresses.IsBlockedHost(picture.Host))
                return "The picture link points at a private address.";
        }

        var worldId = string.IsNullOrWhiteSpace(body.WorldId) ? null : body.WorldId.Trim();
        var channelId = string.IsNullOrWhiteSpace(body.ChannelId) ? null : body.ChannelId.Trim();

        if (body.PostToChannel && channelId is null)
            return "Pick a channel to post to.";

        if (body.AutoOpen && worldId is null)
            return "Pick a world to open the instance in.";

        var openBefore = body.OpenMinutesBefore ?? 10;
        if (openBefore is < 0 or > MaxOpenMinutesBefore)
            return $"The instance can open between 0 and {MaxOpenMinutesBefore} minutes early.";

        target.Title = title;
        target.Description = description;
        target.StartsAt = starts;
        target.EndsAt = ends;
        target.TimeZone = zone.Id;
        target.Repeat = repeat;
        target.RepeatUntil = repeat == CalendarRepeats.None ? null : until;
        target.WorldId = worldId;
        target.AccessType = access;
        target.Region = region;
        target.ImageUrl = imageUrl;
        target.VRChatImageId = string.IsNullOrWhiteSpace(body.VRChatImageId) ? null : body.VRChatImageId.Trim();
        target.Category = category;
        target.Languages = Clean(body.Languages);
        target.Platforms = platforms;
        target.Tags = Clean(body.Tags);
        target.Visibility = visibility;
        target.NotifyMembers = body.NotifyMembers;
        target.PublishToVRChat = body.PublishToVRChat;
        target.PublishToDiscord = body.PublishToDiscord;
        target.PostToChannel = body.PostToChannel;
        target.ChannelId = channelId;
        target.AutoOpen = body.AutoOpen;
        target.OpenMinutesBefore = openBefore;

        // The start's own day is always one of a weekly event's days, so every place agrees on the
        // first occurrence.
        if (repeat == CalendarRepeats.Weekly)
        {
            var first = CalendarRepeat.DayName(startLocal.DayOfWeek);
            if (!days.Contains(first))
                days.Add(first);

            target.RepeatDays = [.. CalendarRepeats.Days.Where(days.Contains)];
        }
        else
        {
            target.RepeatDays = [];
        }

        return null;
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

    private static JsonObject Describe(CalendarEvent e) => new()
    {
        ["title"] = e.Title,
        ["startsAt"] = e.StartsAt.ToString("O", CultureInfo.InvariantCulture),
        ["endsAt"] = e.EndsAt.ToString("O", CultureInfo.InvariantCulture),
        ["timeZone"] = e.TimeZone,
        ["repeat"] = e.Repeat,
        ["repeatDays"] = string.Join(",", e.RepeatDays),
        ["repeatUntil"] = e.RepeatUntil?.ToString("O", CultureInfo.InvariantCulture),
        ["worldId"] = e.WorldId,
        ["accessType"] = e.AccessType,
        ["region"] = e.Region,
        ["state"] = e.State,
        ["publishToVRChat"] = e.PublishToVRChat,
        ["publishToDiscord"] = e.PublishToDiscord,
        ["postToChannel"] = e.PostToChannel,
        ["channelId"] = e.ChannelId,
        ["autoOpen"] = e.AutoOpen,
        ["openMinutesBefore"] = e.OpenMinutesBefore,
    };

    internal static async Task<List<CalendarEventView>> ViewsAsync(
        ModbotContext db, IReadOnlyList<CalendarEvent> events, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var ids = events.Select(e => e.Id).ToList();
        var worldIds = events.Where(e => e.WorldId != null).Select(e => e.WorldId!).Distinct().ToList();

        var places = await db.CalendarEventPlaces.AsNoTracking()
            .Where(p => ids.Contains(p.EventId))
            .ToListAsync(ct);

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

        return [.. events.Select(e =>
        {
            var zone = CalendarRepeat.ZoneOf(e);
            var world = e.WorldId is { } w && worlds.TryGetValue(w, out var found) ? found : null;
            var length = e.EndsAt - e.StartsAt;

            var opening = e.OccurrenceStartsAt is { } current
                ? openings.FirstOrDefault(o => o.EventId == e.Id && o.OccurrenceStartsAt == current)
                : null;

            var closed = opening?.InstanceId is { } instance && closedInstances.Contains(instance);

            var occurrences = e.State == CalendarEventStates.Cancelled
                ? []
                : CalendarRepeat.Between(e, from, to).Take(100).Select(o => new CalendarOccurrenceView(o.StartsAt, o.EndsAt)).ToList();

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
                e.OccurrenceStartsAt + length,
                e.Version,
                e.CreatedAt,
                e.UpdatedAt,
                [.. places
                    .Where(p => p.EventId == e.Id)
                    .OrderBy(p => p.Place, StringComparer.Ordinal)
                    .Select(p => new CalendarPlaceView(p.Place, p.State, p.Error, p.ErrorAt, p.UpdatedAt))],
                opening is null
                    ? null
                    : new CalendarOpeningView(
                        opening.OccurrenceStartsAt,
                        opening.AttemptedAt,
                        opening.InstanceId,
                        closed ? null : InstanceJoinLink.For(opening.Location),
                        closed,
                        opening.Error),
                occurrences);
        })];
    }

    private static string LocalText(DateTimeOffset at, DateTimeZone zone) =>
        LocalPattern.Format(Instant.FromDateTimeOffset(at).InZone(zone).LocalDateTime);
}
