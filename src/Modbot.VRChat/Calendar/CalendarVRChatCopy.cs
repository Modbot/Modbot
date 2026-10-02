using System.Globalization;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using VRChat.API.Model;
using CalendarEvent = Modbot.Core.Data.Entities.CalendarEvent;
using VRChatEvent = VRChat.API.Model.CalendarEvent;

namespace Modbot.VRChat.Calendar;

/// <summary>
/// Copies what VRChat's calendar says about an event onto a Modbot event (calendar design §12):
/// the fields both have, and VRChat's own settings the form does not show.
/// </summary>
/// <remarks>
/// <para>
/// Only VRChat's fields are touched. The world, the instance's access and region, the Discord
/// places and opening the instance are Modbot's alone, so a change read from VRChat never undoes
/// what a moderator set there.
/// </para>
/// <para>
/// A repeating event is copied from VRChat's <em>series</em>, which carries the rule; the dates in
/// the month list carry none.
/// </para>
/// </remarks>
public static class CalendarVRChatCopy
{
    /// <summary>
    /// Why an event on VRChat cannot be kept as a Modbot event, or null when it can. Modbot's repeat
    /// rule has no yearly repeat, and goes up to every <see cref="CalendarRepeats.MaxEvery"/> days,
    /// weeks or months and <see cref="CalendarRepeats.MaxTimes"/> times (calendar design §2).
    /// </summary>
    public static string? CannotKeep(VRChatEvent source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Recurrence is not { } rule)
            return null;

        if (rule.Frequency == CalendarEventFrequency.Yearly)
            return "repeats every year";

        if (rule.Interval > CalendarRepeats.MaxEvery)
            return $"repeats every {rule.Interval.ToString(CultureInfo.InvariantCulture)} {Unit(rule.Frequency)}";

        return rule.End is { Type: CalendarEventRecurrenceEndType.AfterOccurrences, Count: > CalendarRepeats.MaxTimes } end
            ? $"repeats {end.Count.ToString(CultureInfo.InvariantCulture)} times"
            : null;
    }

    /// <summary>Copies <paramref name="source"/> onto <paramref name="target"/>.</summary>
    public static void Onto(CalendarEvent target, VRChatEvent source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        target.Title = Cut((source.Title ?? string.Empty).Trim(), CalendarEvent.MaxTitleLength);
        target.Description = Cut((source.Description ?? string.Empty).Trim(), CalendarEvent.MaxDescriptionLength);

        var starts = AsUtc(source.StartsAt);
        var ends = AsUtc(source.EndsAt);

        if (ends <= starts && source.DurationInMs > 0)
            ends = starts + TimeSpan.FromMilliseconds(source.DurationInMs);

        target.StartsAt = starts;
        target.EndsAt = ends;

        var rule = source.Recurrence;

        // A one-off event on VRChat has no zone of its own: the one Modbot already had is kept, so a
        // moderator's form keeps showing the times the way they were typed.
        if (rule is not null && CalendarRepeat.FindZone(rule.Timezone) is { } ruleZone)
            target.TimeZone = ruleZone.Id;

        target.Repeat = rule?.Frequency switch
        {
            CalendarEventFrequency.Daily => CalendarRepeats.Daily,
            CalendarEventFrequency.Weekly => CalendarRepeats.Weekly,
            CalendarEventFrequency.Monthly => CalendarRepeats.Monthly,
            _ => CalendarRepeats.None,
        };

        var zone = CalendarRepeat.ZoneOf(target);

        if (target.Repeat == CalendarRepeats.Weekly)
        {
            // The first start's own day is always one of them, as the form makes it.
            var days = (rule?.DaysOfWeek ?? []).Select(d => d.ToString()).Where(CalendarRepeat.IsDayName).ToHashSet(StringComparer.Ordinal);
            days.Add(CalendarRepeat.DayName(CalendarRepeat.FirstLocal(target, zone).DayOfWeek));
            target.RepeatDays = [.. CalendarRepeats.Days.Where(days.Contains)];
        }
        else
        {
            target.RepeatDays = [];
        }

        target.RepeatEvery = target.Repeat == CalendarRepeats.None || rule is null ? 1 : Math.Clamp(rule.Interval, 1, CalendarRepeats.MaxEvery);

        // VRChat's "after N times" is kept as a number of times, counted from the series' start as
        // VRChat has it, which is the start copied above.
        var end = target.Repeat == CalendarRepeats.None ? null : rule?.End;

        target.RepeatTimes = end is { Type: CalendarEventRecurrenceEndType.AfterOccurrences, Count: > 0 }
            ? Math.Min(end.Count, CalendarRepeats.MaxTimes)
            : null;

        target.RepeatUntil = target.RepeatTimes is null ? LastDate(end) : null;

        target.Category = CategoryWord(source.Category);
        target.Languages = Clean(source.Languages);
        target.Platforms = [.. (source.Platforms ?? []).Select(PlatformWord).Distinct(StringComparer.Ordinal)];
        target.Tags = Clean(source.Tags);
        target.Visibility = source.AccessType == CalendarEventAccess.Public ? "public" : "group";
        target.VRChatImageId = string.IsNullOrWhiteSpace(source.ImageId) ? null : source.ImageId;

        // The picture a Discord post uses is the moderator's own choice on an event made in Modbot;
        // on one made on VRChat it is VRChat's, so the pages have one to show.
        if (target.MadeOnVRChat)
            target.ImageUrl = IsWebPicture(source.ImageUrl) ? source.ImageUrl : null;

        target.Featured = source.Featured;
        target.VRChatHostEarlyJoinMinutes = source.HostEarlyJoinMinutes;
        target.VRChatGuestEarlyJoinMinutes = source.GuestEarlyJoinMinutes;
        target.VRChatCloseInstanceAfterEndMinutes = source.CloseInstanceAfterEndMinutes;
        target.VRChatRoleIds = source.RoleIds is null ? null : [.. source.RoleIds];
        target.VRChatUsesInstanceOverflow = source.UsesInstanceOverflow;
    }

    /// <summary>VRChat's <c>updatedAt</c>, or null when it gave none.</summary>
    public static DateTimeOffset? UpdatedAt(VRChatEvent? source) =>
        source is null || source.UpdatedAt == default ? null : AsUtc(source.UpdatedAt);

    /// <summary>
    /// The last date the rule may start on, for VRChat's "after a date": that day's last moment as
    /// wall-clock time in the zone. Null for any other end.
    /// </summary>
    private static DateOnly? LastDate(CalendarEventRecurrenceEnd? end)
    {
        if (end is not { Type: CalendarEventRecurrenceEndType.AfterDate })
            return null;

        var text = end.Date?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length < 10)
            return null;

        return DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static string Unit(CalendarEventFrequency frequency) => frequency switch
    {
        CalendarEventFrequency.Daily => "days",
        CalendarEventFrequency.Weekly => "weeks",
        CalendarEventFrequency.Monthly => "months",
        _ => "years",
    };

    /// <summary>VRChat's category word, the same words Modbot stores.</summary>
    public static string CategoryWord(CalendarEventCategory category) => category switch
    {
        CalendarEventCategory.Arts => "arts",
        CalendarEventCategory.Avatars => "avatars",
        CalendarEventCategory.Dance => "dance",
        CalendarEventCategory.Education => "education",
        CalendarEventCategory.Exploration => "exploration",
        CalendarEventCategory.FilmMedia => "film_media",
        CalendarEventCategory.Gaming => "gaming",
        CalendarEventCategory.Music => "music",
        CalendarEventCategory.Other => "other",
        CalendarEventCategory.Performance => "performance",
        CalendarEventCategory.Roleplaying => "roleplaying",
        CalendarEventCategory.Wellness => "wellness",
        _ => "hangout",
    };

    private static string PlatformWord(CalendarEventPlatform platform) => platform switch
    {
        CalendarEventPlatform.Android => "android",
        CalendarEventPlatform.Ios => "ios",
        _ => "standalonewindows",
    };

    private static List<string> Clean(IEnumerable<string>? values) =>
        [.. (values ?? []).Select(v => v?.Trim() ?? string.Empty).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal)];

    private static bool IsWebPicture(string? url) =>
        url is { Length: > 0 and <= 2048 }
        && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttps;

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(value.Kind switch
        {
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => value,
        }, TimeSpan.Zero);
}
