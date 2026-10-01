using System.Globalization;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using VRChat.API.Model;
using CalendarEvent = Modbot.Core.Data.Entities.CalendarEvent;

namespace Modbot.VRChat.Calendar;

/// <summary>
/// Turns an event into VRChat's calendar request bodies, and says when that body has changed.
/// </summary>
/// <remarks>
/// A repeating event is one VRChat series with VRChat's own recurrence, not one VRChat event per
/// occurrence (calendar design §3.1). The start sent is the occurrence Modbot is dealing with, so a
/// series whose first date has passed is not sent as starting in the past -- at the time the repeat
/// gives it, even when that date was moved on its own: the series is the repeat, and the move is
/// sent to that one date afterwards (§2.2).
/// </remarks>
public static class CalendarVRChatRequests
{
    /// <summary>
    /// A hash of everything a create or update sends except the occurrence it starts from, so a
    /// repeating event moving on to its next occurrence costs no write.
    /// </summary>
    public static string Fingerprint(CalendarEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);

        return CalendarFingerprint.Of(
            e.Title, e.Description, e.StartsAt, e.EndsAt, e.TimeZone, e.Repeat, e.RepeatDays, e.RepeatUntil?.ToString("O", CultureInfo.InvariantCulture),
            e.Category, e.Languages, e.Platforms, e.Tags, e.Visibility, e.VRChatImageId, e.NotifyMembers);
    }

    public static CreateCalendarEventRequest Create(CalendarEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var (starts, ends) = Times(e);

        return new CreateCalendarEventRequest(
            accessType: e.Visibility == "public" ? CalendarEventAccess.Public : CalendarEventAccess.Group,
            category: Category(e.Category),
            closeInstanceAfterEndMinutes: e.VRChatCloseInstanceAfterEndMinutes ?? 0,
            description: e.Description ?? string.Empty,
            endsAt: ends,
            featured: e.VRChatFeatured ?? false,
            guestEarlyJoinMinutes: e.VRChatGuestEarlyJoinMinutes ?? 0,
            hostEarlyJoinMinutes: e.VRChatHostEarlyJoinMinutes ?? 0,
            imageId: string.IsNullOrWhiteSpace(e.VRChatImageId) ? null! : e.VRChatImageId,
            isDraft: false,
            languages: e.Languages.Count > 0 ? [.. e.Languages] : null!,
            platforms: e.Platforms.Count > 0 ? [.. e.Platforms.Select(Platform)] : null!,
            recurrence: Recurrence(e)!,
            roleIds: e.VRChatRoleIds is { Count: > 0 } createRoles ? [.. createRoles] : null!,
            sendCreationNotification: e.NotifyMembers,
            startsAt: starts,
            tags: e.Tags.Count > 0 ? [.. e.Tags] : null!,
            title: e.Title,
            usesInstanceOverflow: e.VRChatUsesInstanceOverflow ?? false);
    }

    /// <remarks>
    /// The SDK always sends <c>featured</c> and <c>usesInstanceOverflow</c>, as false when not given
    /// (checked 2026-09-27), and leaves out the minutes when they are 0 and the roles when null. So
    /// what VRChat said for an event read from its calendar is sent back as it was, and an edit made
    /// in Modbot does not switch those settings off.
    /// </remarks>
    public static UpdateCalendarEventRequest Update(CalendarEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var (starts, ends) = Times(e);

        return new UpdateCalendarEventRequest(
            // The update body takes the category as text rather than the enum; the stored word is
            // already VRChat's own.
            category: Categories.Contains(e.Category) ? e.Category : "hangout",
            closeInstanceAfterEndMinutes: e.VRChatCloseInstanceAfterEndMinutes ?? 0,
            description: e.Description ?? string.Empty,
            endsAt: ends,
            featured: e.VRChatFeatured ?? false,
            guestEarlyJoinMinutes: e.VRChatGuestEarlyJoinMinutes ?? 0,
            hostEarlyJoinMinutes: e.VRChatHostEarlyJoinMinutes ?? 0,
            imageId: string.IsNullOrWhiteSpace(e.VRChatImageId) ? null! : e.VRChatImageId,
            isDraft: false,
            languages: [.. e.Languages],
            platforms: [.. e.Platforms],
            recurrence: Recurrence(e)!,
            roleIds: e.VRChatRoleIds is { } roles ? [.. roles] : null!,
            sendCreationNotification: false,
            startsAt: starts,
            tags: [.. e.Tags],
            title: e.Title,
            usesInstanceOverflow: e.VRChatUsesInstanceOverflow ?? false);
    }

    /// <summary>
    /// A hash of what one date's own change sends: cancelled or not, its times and its words. The
    /// event's title and description are in it for a date that uses them, so an edit to the whole
    /// series reaches a date VRChat holds apart from it.
    /// </summary>
    public static string DateFingerprint(CalendarEvent e, CalendarDateChange change)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(change);

        if (change.Cancelled)
            return CalendarFingerprint.Of("date", change.PlannedStartsAt, "cancelled");

        var date = CalendarRepeat.Changed(change, CalendarRepeat.LengthOf(e));

        return CalendarFingerprint.Of(
            "date", change.PlannedStartsAt, date.StartsAt, date.EndsAt,
            CalendarRepeat.TitleOf(e, date), CalendarRepeat.DescriptionOf(e, date));
    }

    /// <summary>
    /// The update for one date of the series, sent to that date's own id: the series' settings with
    /// the date's times and words, and no repeat, since it is one date.
    /// </summary>
    public static UpdateCalendarEventRequest UpdateDate(CalendarEvent e, CalendarDateChange change)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(change);

        var date = CalendarRepeat.Changed(change, CalendarRepeat.LengthOf(e));
        var request = Update(e);

        request.StartsAt = date.StartsAt.UtcDateTime;
        request.EndsAt = date.EndsAt.UtcDateTime;
        request.Title = CalendarRepeat.TitleOf(e, date);
        request.Description = CalendarRepeat.DescriptionOf(e, date) ?? string.Empty;
        request.Recurrence = null!;

        return request;
    }

    /// <summary>Where the series sent to VRChat starts: the planned start of the date Modbot is dealing with.</summary>
    public static DateTimeOffset SeriesStartsAt(CalendarEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.OccurrenceStartsAt is not null ? CalendarRepeat.Current(e).PlannedStartsAt : e.StartsAt;
    }

    private static (DateTime Starts, DateTime Ends) Times(CalendarEvent e)
    {
        var length = CalendarRepeat.LengthOf(e);
        var starts = SeriesStartsAt(e);
        return (starts.UtcDateTime, (starts + length).UtcDateTime);
    }

    private static CalendarEventRecurrence? Recurrence(CalendarEvent e)
    {
        var frequency = e.Repeat switch
        {
            CalendarRepeats.Daily => CalendarEventFrequency.Daily,
            CalendarRepeats.Weekly => CalendarEventFrequency.Weekly,
            CalendarRepeats.Monthly => CalendarEventFrequency.Monthly,
            _ => (CalendarEventFrequency?)null,
        };

        if (frequency is null)
            return null;

        var zone = CalendarRepeat.ZoneOf(e);

        var days = e.Repeat == CalendarRepeats.Weekly
            ? CalendarRepeat.WeeklyDays(e, zone).Select(d => Enum.Parse<CalendarDayOfWeek>(CalendarRepeat.DayName(d))).ToList()
            : null;

        // VRChat asks for the end date "without timezone or offset": the last moment of the last
        // day, as wall-clock time in the event's zone.
        var end = e.RepeatUntil is { } until
            ? new CalendarEventRecurrenceEnd(
                date: until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T23:59:59",
                type: CalendarEventRecurrenceEndType.AfterDate)
            : null;

        return new CalendarEventRecurrence(
            daysOfWeek: days!,
            end: end!,
            frequency: frequency.Value,
            interval: 1,
            timezone: zone.Id);
    }

    private static CalendarEventCategory Category(string category) => category switch
    {
        "arts" => CalendarEventCategory.Arts,
        "avatars" => CalendarEventCategory.Avatars,
        "dance" => CalendarEventCategory.Dance,
        "education" => CalendarEventCategory.Education,
        "exploration" => CalendarEventCategory.Exploration,
        "film_media" => CalendarEventCategory.FilmMedia,
        "gaming" => CalendarEventCategory.Gaming,
        "music" => CalendarEventCategory.Music,
        "other" => CalendarEventCategory.Other,
        "performance" => CalendarEventCategory.Performance,
        "roleplaying" => CalendarEventCategory.Roleplaying,
        "wellness" => CalendarEventCategory.Wellness,
        _ => CalendarEventCategory.Hangout,
    };

    private static CalendarEventPlatform Platform(string platform) => platform switch
    {
        "android" => CalendarEventPlatform.Android,
        "ios" => CalendarEventPlatform.Ios,
        _ => CalendarEventPlatform.Standalonewindows,
    };

    /// <summary>VRChat's category words, for the form and for checking a request.</summary>
    public static readonly IReadOnlyList<string> Categories =
        ["arts", "avatars", "dance", "education", "exploration", "film_media", "gaming", "hangout", "music", "other", "performance", "roleplaying", "wellness"];

    /// <summary>VRChat's platform words.</summary>
    public static readonly IReadOnlyList<string> Platforms = ["standalonewindows", "android", "ios"];
}
