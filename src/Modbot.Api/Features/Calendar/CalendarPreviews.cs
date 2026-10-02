using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Calendar;

namespace Modbot.Api.Features.Calendar;

/// <summary>
/// The event form's Preview: the event as it is filled in, drawn the way each place would show it
/// (calendar design §14.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>One set of rules.</strong> Every part is drawn by the code that sends it: the Discord
/// event and the channel post by the Discord publisher's own builders (through
/// <see cref="ICalendarDiscordPreview"/>), VRChat's by the request the VRChat publisher sends, and
/// the phone calendar by the feed writer. A second copy of those rules in the browser would be a
/// second answer that could disagree with the first.
/// </para>
/// <para>
/// Nothing is saved and nothing is asked of VRChat or Discord. The event is checked and filled in as
/// a save would, except that a piece not filled in yet is let through (<c>Apply</c>'s preview), and
/// its state and current occurrence are worked out as a save would.
/// </para>
/// </remarks>
public static class CalendarPreviews
{
    public static async Task<CalendarPreviewResult> BuildAsync(
        ModbotContext db,
        ICalendarDiscordPreview? discord,
        DateTimeOffset now,
        CalendarPreviewRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Event);

        CalendarEvent? saved = null;

        if (request.EventId is { } id)
        {
            // Read without tracking: everything below changes it, and none of it is saved.
            saved = await db.CalendarEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt == null, ct);
            if (saved is null)
                return CalendarPreviewResult.NotFound;
        }

        var calendarEvent = saved ?? new CalendarEvent { Id = Guid.Empty, CreatedAt = now, UpdatedAt = now };

        if (CalendarEndpoints.Apply(request.Event, calendarEvent, preview: true) is { } problem)
            return new CalendarPreviewResult(null, problem);

        // As a save leaves it: a draft or a finished event starts again, and the current occurrence
        // is worked out from the rule as it is now.
        if (calendarEvent.State is CalendarEventStates.Draft or CalendarEventStates.Finished)
            calendarEvent.State = CalendarEventStates.Scheduled;

        if (calendarEvent.State != CalendarEventStates.Cancelled)
        {
            calendarEvent.OccurrenceStartsAt = null;
            CalendarTimeline.Advance(calendarEvent, now);
        }

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        var world = calendarEvent.WorldId is { } worldId
            ? await db.VRChatWorlds.AsNoTracking().FirstOrDefaultAsync(w => w.WorldId == worldId, ct)
            : null;

        var joinLink = saved is not null && calendarEvent.State == CalendarEventStates.Open
            ? await CalendarEndpoints.JoinLinkAsync(db, saved.Id, ct)
            : null;

        var onVRChat = saved is not null && await db.CalendarEventPlaces.AsNoTracking()
            .AnyAsync(p => p.EventId == saved.Id && p.Place == CalendarPlaces.VRChat && p.ExternalId != null, ct);

        var groupName = string.IsNullOrWhiteSpace(settings?.ManagedGroupName) ? "Modbot" : settings.ManagedGroupName;
        var discordPreview = discord?.Preview(
            calendarEvent, world, new CalendarPreviewContext(settings?.PublicAddress, settings?.ManagedGroupName, joinLink, now));

        // The role mentioned above the card (§3.3.1), by its name in Modbot's copy of the role list.
        // The same rule as the post: never @everyone, whose id is the server's.
        var guildId = settings?.DiscordGuildId?.Trim();
        var roleId = calendarEvent.MentionRoleId?.Trim();

        if (discordPreview is not null && !string.IsNullOrEmpty(roleId) && !string.Equals(roleId, guildId, StringComparison.Ordinal))
        {
            var role = await db.DiscordRoles.AsNoTracking().FirstOrDefaultAsync(r => r.RoleId == roleId && r.GuildId == guildId, ct);

            if (role is not { Everyone: true })
            {
                discordPreview = discordPreview with
                {
                    ChannelPost = discordPreview.ChannelPost with
                    {
                        MentionRole = role?.Name ?? roleId,
                        MentionRoleColour = role?.Color ?? 0,
                    },
                };
            }
        }

        var names = world?.Name is { Length: > 0 } name
            ? new Dictionary<string, string>(StringComparer.Ordinal) { [world.WorldId] = name }
            : new Dictionary<string, string>(StringComparer.Ordinal);

        var entry = CalendarFeedWriter.Entry(calendarEvent, names);

        return new CalendarPreviewResult(
            new CalendarPreviewView(
                discordPreview?.DiscordEvent,
                discordPreview?.ChannelPost,
                VRChat(calendarEvent, onVRChat),
                new CalendarFeedPreviewView(
                    groupName,
                    entry.Title,
                    entry.Notes,
                    entry.Location,
                    calendarEvent.StartsAt,
                    calendarEvent.EndsAt,
                    calendarEvent.TimeZone,
                    entry.Repeat)),
            null);
    }

    /// <summary>
    /// What VRChat's calendar is sent, read back out of the request itself. The words VRChat uses
    /// for a category, a platform or a day come from the request as the SDK writes it, so they are
    /// VRChat's own and not a second list of them.
    /// </summary>
    public static CalendarVRChatPreviewView VRChat(CalendarEvent calendarEvent, bool update)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        string json;
        string title;
        string description;
        DateTime startsAt;
        DateTime endsAt;
        string? imageId;
        List<string>? languages;
        List<string>? tags;
        bool notify;

        if (update)
        {
            var request = CalendarVRChatRequests.Update(calendarEvent);
            (json, title, description, startsAt, endsAt, imageId, languages, tags, notify) = (
                request.ToJson(), request.Title, request.Description, request.StartsAt, request.EndsAt,
                request.ImageId, request.Languages, request.Tags, request.SendCreationNotification);
        }
        else
        {
            var request = CalendarVRChatRequests.Create(calendarEvent);
            (json, title, description, startsAt, endsAt, imageId, languages, tags, notify) = (
                request.ToJson(), request.Title, request.Description, request.StartsAt, request.EndsAt,
                request.ImageId, request.Languages, request.Tags, request.SendCreationNotification);
        }

        var sent = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
        var recurrence = sent["recurrence"] as JsonObject;

        return new CalendarVRChatPreviewView(
            update,
            title,
            description,
            Utc(startsAt),
            Utc(endsAt),
            Text(sent["category"]),
            Text(sent["accessType"]),
            languages ?? [],
            Texts(sent["platforms"]),
            tags ?? [],
            string.IsNullOrWhiteSpace(imageId) ? null : imageId,
            recurrence is null || Text(recurrence["frequency"]) is not { } frequency
                ? null
                : new CalendarVRChatRepeatView(
                    frequency,
                    Texts(recurrence["daysOfWeek"]),
                    Text((recurrence["end"] as JsonObject)?["date"]),
                    Text(recurrence["timezone"]),
                    Number(recurrence["interval"]) ?? 1,
                    Text((recurrence["end"] as JsonObject)?["type"]) == "afterOccurrences"
                        ? Number((recurrence["end"] as JsonObject)?["count"])
                        : null),
            notify,
            sent["featured"] is JsonValue featured && featured.TryGetValue<bool>(out var on) && on);
    }

    private static int? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value, DateTimeKind.Utc));

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static List<string> Texts(JsonNode? node) =>
        node is JsonArray array ? [.. array.Select(Text).OfType<string>()] : [];
}

/// <summary>A preview, or what is wrong with the event, or that the event was not found.</summary>
public sealed record CalendarPreviewResult(CalendarPreviewView? View, string? Problem, bool Missing = false)
{
    public static CalendarPreviewResult NotFound { get; } = new(null, null, Missing: true);
}
