using System.Text;
using System.Text.Json;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using CalendarEvent = Modbot.Core.Data.Entities.CalendarEvent;
using VRChatEvent = VRChat.API.Model.CalendarEvent;

namespace Modbot.VRChat.Calendar;

/// <summary>
/// Finds the event a create made on VRChat's calendar when VRChat did not say so -- a 500, or no
/// answer at all -- and takes it as that Modbot event's own (calendar design §3.1).
/// </summary>
/// <remarks>
/// <para>
/// Seen three times on 2026-10-01: VRChat answered a create with <c>500 Application error</c> and
/// made the event anyway. The read of VRChat's calendar a couple of minutes later then took VRChat's
/// copy in as a second Modbot event, and that one made its own Discord event.
/// </para>
/// <para>
/// <strong>VRChat changes the title it is sent.</strong> On the same day it dropped an en dash and
/// turned "." into a look-alike dot, so titles are compared by their letters and digits only.
/// </para>
/// <para>
/// <strong>What was sent, not what the event says now.</strong> A title or time fixed while the
/// copy is looked for is not on VRChat's copy, so the create's own title, times and repeat are kept
/// on the place (<see cref="Remember"/>, <see cref="AsSent"/>), and the fix goes out as an update
/// once the copy is found (review of 2026-10-01).
/// </para>
/// <para>
/// A copy must also have the same times, and have been made no earlier than the create was sent
/// (less <see cref="ClockSlack"/>, as VRChat's clock is not Modbot's). The group is the managed
/// group's, as only its calendar is read.
/// </para>
/// </remarks>
public static class CalendarVRChatMatch
{
    /// <summary>How far VRChat's clock may be behind Modbot's for a copy to still count as made after the create.</summary>
    public static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan SameTime = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A title with only its letters and digits, in lower case, after look-alike characters are
    /// folded to their plain form. A title with none of those is kept as it is, trimmed.
    /// </summary>
    public static string PlainTitle(string? title)
    {
        var text = (title ?? string.Empty).Trim();
        var folded = text.Normalize(NormalizationForm.FormKC);
        var plain = new StringBuilder(folded.Length);

        foreach (var rune in folded.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
                plain.Append(Rune.ToLowerInvariant(rune).ToString());
        }

        return plain.Length > 0 ? plain.ToString() : text;
    }

    /// <summary>Whether two titles are the same once VRChat's changes to the text are set aside.</summary>
    public static bool SameTitle(string? a, string? b) =>
        string.Equals(PlainTitle(a), PlainTitle(b), StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="row"/>, from a read of the group's calendar, is what a create of
    /// <paramref name="calendarEvent"/> sent at <paramref name="sentAt"/> made.
    /// </summary>
    /// <remarks>A date of a repeating event counts when it is one of the event's own times.</remarks>
    public static bool IsCopyOf(VRChatEvent row, CalendarEvent calendarEvent, DateTimeOffset sentAt)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(calendarEvent);

        if (row.IsDraft || row.DeletedAt is not null || string.IsNullOrEmpty(row.Id))
            return false;

        // VRChat's own time it was made, when it gave one.
        if (row.CreatedAt != default && AsUtc(row.CreatedAt) < sentAt - ClockSlack)
            return false;

        if (!SameTitle(row.Title, calendarEvent.Title))
            return false;

        var starts = AsUtc(row.StartsAt);
        var ends = AsUtc(row.EndsAt);

        if (ends <= starts && row.DurationInMs > 0)
            ends = starts + TimeSpan.FromMilliseconds(row.DurationInMs);

        if (Far(ends - starts, calendarEvent.EndsAt - calendarEvent.StartsAt))
            return false;

        if (calendarEvent.Repeat == CalendarRepeats.None)
            return !Far(starts, calendarEvent.StartsAt);

        return CalendarRepeat.Between(calendarEvent, starts - SameTime, starts + SameTime)
            .Any(o => !Far(o.StartsAt, starts));
    }

    /// <summary>
    /// What a create of <paramref name="calendarEvent"/> sends, as far as finding its copy needs:
    /// kept on the place as <see cref="CalendarEventPlace.CreateSent"/> when the create gets no answer.
    /// </summary>
    public static string Remember(CalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        return JsonSerializer.Serialize(new SentCreate(
            calendarEvent.Title,
            calendarEvent.StartsAt,
            calendarEvent.EndsAt,
            calendarEvent.OccurrenceStartsAt,
            calendarEvent.TimeZone,
            calendarEvent.Repeat,
            [.. calendarEvent.RepeatDays],
            calendarEvent.RepeatUntil,
            calendarEvent.RepeatEvery,
            calendarEvent.RepeatTimes));
    }

    /// <summary>
    /// The event as the create with no answer sent it, for finding its copy: an edit made since is
    /// not what VRChat holds. The event as it is now when nothing was kept (a place from before
    /// 2026-10-01).
    /// </summary>
    public static CalendarEvent AsSent(CalendarEventPlace place, CalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(place);
        ArgumentNullException.ThrowIfNull(calendarEvent);

        SentCreate? sent = null;

        if (place.CreateSent is { Length: > 0 } json)
        {
            try
            {
                sent = JsonSerializer.Deserialize<SentCreate>(json);
            }
            catch (JsonException)
            {
                sent = null;
            }
        }

        if (sent is null)
            return calendarEvent;

        return new CalendarEvent
        {
            Id = calendarEvent.Id,
            Title = sent.Title,
            StartsAt = sent.StartsAt,
            EndsAt = sent.EndsAt,
            OccurrenceStartsAt = sent.OccurrenceStartsAt,
            TimeZone = sent.TimeZone,
            Repeat = sent.Repeat,
            RepeatDays = [.. sent.RepeatDays ?? []],
            RepeatUntil = sent.RepeatUntil,
            RepeatEvery = sent.RepeatEvery ?? 1,
            RepeatTimes = sent.RepeatTimes,
        };
    }

    /// <summary>
    /// The copy found, taken as the event's VRChat place: published, with VRChat's id. When the
    /// event was not changed after the create was sent, VRChat has what Modbot sent and nothing goes
    /// out; otherwise the next pass sends the change as an update.
    /// </summary>
    /// <param name="externalId">The id Modbot keeps for it: the series id for a date of a repeating event.</param>
    /// <param name="vrChatUpdatedAt">VRChat's <c>updatedAt</c> for it, so a later read does not take it for a change made there.</param>
    public static void Adopt(
        CalendarEventPlace place,
        CalendarEvent calendarEvent,
        string externalId,
        DateTimeOffset? vrChatUpdatedAt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(place);
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var unchanged = place.ErrorAt is { } sentAt && calendarEvent.UpdatedAt <= sentAt;

        place.ExternalId = externalId;
        place.State = CalendarPlaceStates.Published;
        place.SentFingerprint = unchanged ? CalendarVRChatRequests.Fingerprint(calendarEvent) : null;
        place.VRChatUpdatedAt = vrChatUpdatedAt;
        place.FailedFingerprint = null;
        place.Error = null;
        place.ErrorAt = null;
        place.MissingGroupPermission = null;
        place.CreateSent = null;
        place.UpdatedAt = now;
    }

    /// <summary>What a create sent, as <see cref="Remember"/> keeps it.</summary>
    private sealed record SentCreate(
        string Title,
        DateTimeOffset StartsAt,
        DateTimeOffset EndsAt,
        DateTimeOffset? OccurrenceStartsAt,
        string TimeZone,
        string Repeat,
        List<string>? RepeatDays,
        DateOnly? RepeatUntil,
        int? RepeatEvery = null,
        int? RepeatTimes = null);

    private static bool Far(DateTimeOffset a, DateTimeOffset b) => (a - b).Duration() >= SameTime;

    private static bool Far(TimeSpan a, TimeSpan b) => (a - b).Duration() >= SameTime;

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(value.Kind switch
        {
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => value,
        }, TimeSpan.Zero);
}
