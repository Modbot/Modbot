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

        List<object?> parts =
        [
            e.Title, e.Description, e.StartsAt, e.EndsAt, e.TimeZone, e.Repeat, e.RepeatDays, e.RepeatUntil?.ToString("O", CultureInfo.InvariantCulture),
            e.Category, e.Languages, e.Platforms, e.Tags, e.Visibility, e.VRChatImageId, e.NotifyMembers,
        ];

        // Added 2026-10-02, and only when not what every event sent before then, so an event that
        // uses none of them keeps the fingerprint it was published with and is not written again.
        if (e.Repeat != CalendarRepeats.None && CalendarRepeat.EveryOf(e) > 1)
            parts.AddRange(["every", CalendarRepeat.EveryOf(e)]);

        if (e.Repeat != CalendarRepeats.None && e.RepeatTimes is { } times)
            parts.AddRange(["times", times]);

        if (e.Featured)
            parts.Add("featured");

        return CalendarFingerprint.Of([.. parts]);
    }

    /// <summary>
    /// How long after VRChat refuses Featured the calendar leaves it out of what it sends, before
    /// asking once more in case the account may feature by then.
    /// </summary>
    public static readonly TimeSpan FeaturedRefusalMemory = TimeSpan.FromDays(7);

    /// <summary>
    /// Whether Modbot's account is thought able to feature an event: VRChat has not refused it for
    /// that lately (calendar repeats and VRChat settings design §4).
    /// </summary>
    public static bool CanFeature(Settings? settings, DateTimeOffset now) =>
        settings?.VRChatFeaturedRefusedAt is not { } refused || now - refused >= FeaturedRefusalMemory;

    /// <summary>
    /// What an event says about Featured in a create or an update: true or false, or null to leave
    /// the field out.
    /// </summary>
    /// <remarks>
    /// Left out for an event made on VRChat whose Featured is still what VRChat reported: a moderator
    /// has not changed it, and sending it back refused an event VRChat itself holds as featured
    /// ("18+ Hangout", 2026-10-09). Left out too when Featured is wanted but the account was refused
    /// for it lately, so the rest of the edit goes through.
    /// </remarks>
    public static bool? FeaturedToSend(CalendarEvent e, bool canFeature = true)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.MadeOnVRChat && e.VRChatFeatured == e.Featured)
            return null;

        return e.Featured && !canFeature ? null : e.Featured;
    }

    /// <summary>
    /// Whether VRChat's answer to a write refuses Featured itself ("You do not have permission to
    /// make a featured event", seen 2026-10-09 as a 403), as opposed to anything else about the
    /// write.
    /// </summary>
    public static bool IsFeaturedRefusal(int status, string? body, string? error)
    {
        // Never a 429: a rate limit is not a refusal of Featured, and is never sent again here.
        if (status is < 400 or >= 500 or 429)
            return false;

        var said = VRChatRefusal.MessageOf(body) ?? error ?? body;
        return said?.Contains("featured", StringComparison.OrdinalIgnoreCase) == true;
    }

    public static CreateCalendarEventRequest Create(CalendarEvent e, bool canFeature = true)
    {
        ArgumentNullException.ThrowIfNull(e);
        var (starts, ends) = Times(e);

        return new CreateCalendarEventRequest(
            accessType: e.Visibility == "public" ? CalendarEventAccess.Public : CalendarEventAccess.Group,
            category: Category(e.Category),
            closeInstanceAfterEndMinutes: e.VRChatCloseInstanceAfterEndMinutes ?? 0,
            description: e.Description ?? string.Empty,
            endsAt: ends,
            featured: FeaturedToSend(e, canFeature) ?? false,
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
    /// in Modbot does not switch those settings off. Featured is the form's own since 2026-10-02,
    /// and is sent as the form has it, except where <see cref="FeaturedToSend"/> leaves it out.
    /// </remarks>
    public static CalendarUpdateBody Update(CalendarEvent e, bool canFeature = true)
    {
        ArgumentNullException.ThrowIfNull(e);
        var (starts, ends) = Times(e);

        return new CalendarUpdateBody
        {
            // Who sees it, as the create sent it: the SDK's update model has no access type, and an
            // update without one was refused as a change of it (see CalendarUpdateBody).
            AccessType = AccessWord(e),

            // The update body takes the category as text rather than the enum; the stored word is
            // already VRChat's own.
            Category = Categories.Contains(e.Category) ? e.Category : "hangout",
            CloseInstanceAfterEndMinutes = e.VRChatCloseInstanceAfterEndMinutes ?? 0,
            Description = e.Description ?? string.Empty,
            EndsAt = ends,
            Featured = FeaturedToSend(e, canFeature),
            GuestEarlyJoinMinutes = e.VRChatGuestEarlyJoinMinutes ?? 0,
            HostEarlyJoinMinutes = e.VRChatHostEarlyJoinMinutes ?? 0,
            ImageId = string.IsNullOrWhiteSpace(e.VRChatImageId) ? null! : e.VRChatImageId,
            Languages = [.. e.Languages],
            Platforms = [.. e.Platforms],
            Recurrence = Recurrence(e)!,
            RoleIds = e.VRChatRoleIds is { } roles ? [.. roles] : null!,
            SendCreationNotification = false,
            StartsAt = starts,
            Tags = [.. e.Tags],
            Title = e.Title,
            UsesInstanceOverflow = e.VRChatUsesInstanceOverflow ?? false,
        };
    }

    /// <summary>VRChat's word for who sees the event: the same in a create and an update.</summary>
    public static string AccessWord(CalendarEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.Visibility == "public" ? "public" : "group";
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
    public static CalendarUpdateBody UpdateDate(CalendarEvent e, CalendarDateChange change, bool canFeature = true)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(change);

        var date = CalendarRepeat.Changed(change, CalendarRepeat.LengthOf(e));
        var request = Update(e, canFeature);

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

        return new CalendarEventRecurrence(
            daysOfWeek: days!,
            end: End(e)!,
            frequency: frequency.Value,
            interval: CalendarRepeat.EveryOf(e),
            timezone: zone.Id);
    }

    /// <summary>
    /// When the series VRChat is sent stops: after a date, after a number of times, or never (null).
    /// </summary>
    /// <remarks>
    /// VRChat asks for the end date "without timezone or offset": the last moment of the last day,
    /// as wall-clock time in the event's zone. A number of times is VRChat's own
    /// <c>afterOccurrences</c>, counted from where the series sent starts (<see cref="SeriesStartsAt"/>):
    /// the dates before it are not in what VRChat is sent, so they come off the number.
    /// </remarks>
    private static CalendarEventRecurrenceEnd? End(CalendarEvent e)
    {
        if (e.RepeatUntil is { } until)
        {
            return new CalendarEventRecurrenceEnd(
                date: until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T23:59:59",
                type: CalendarEventRecurrenceEndType.AfterDate);
        }

        if (e.RepeatTimes is { } times)
        {
            var left = Math.Clamp(times, 1, CalendarRepeats.MaxTimes) - CalendarRepeat.PlannedBefore(e, SeriesStartsAt(e));

            return new CalendarEventRecurrenceEnd(
                count: Math.Max(left, 1),
                type: CalendarEventRecurrenceEndType.AfterOccurrences);
        }

        return null;
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
