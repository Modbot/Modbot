using Modbot.Core.Data.Entities;

namespace Modbot.Core.Calendar;

/// <summary>
/// Sending a failed Discord place again: the Discord event, the channel post and the cancel post
/// (calendar design §17.4, added 2026-10-02).
/// </summary>
/// <remarks>
/// <para>
/// The calendar's Discord loop does not send a refusal that will not change on its own -- the bot
/// lacking Manage Events, a channel gone -- again until what it would send changes. Fixing the
/// cause changes nothing about the event, so until 2026-10-02 the only way to send it again was to
/// edit something Discord shows. Now a moderator's Try again, or any save of the event, puts the
/// place back to waiting with nothing of the failure left, and the next pass sends it.
/// </para>
/// <para>
/// Here rather than in the Discord loop because the API, which has no Discord project, changes the
/// row; the loop reads the row as it finds it.
/// </para>
/// </remarks>
public static class CalendarDiscordRetry
{
    /// <summary>
    /// What a place says when Discord refused for a missing permission. The gateway writes it, and
    /// <see cref="ClearAfterManageEventsGranted"/> finds the places it is on; Discord's own answer
    /// may follow it.
    /// </summary>
    public const string NeedsManageEvents = "The bot may not manage server events; it needs Manage Events.";

    /// <summary>
    /// A moderator's Try again on a failed Discord place. False, changing nothing, for a place
    /// that has not failed, which is also what a second press finds.
    /// </summary>
    public static bool TryAgain(CalendarEventPlace place, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(place);

        if (!IsDiscord(place.Place) || place.State != CalendarPlaceStates.Failed)
            return false;

        Clear(place, now);
        return true;
    }

    /// <summary>
    /// An edit of the event clears an old failure of its Discord event and channel post at once,
    /// so it shows being sent, not the failure the edit may have fixed. The cancel post is about a
    /// cancel and is left alone: a cancelled event is not edited.
    /// </summary>
    public static bool ClearAfterEdit(CalendarEventPlace place, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(place);

        if (place.Place is not (CalendarPlaces.DiscordEvent or CalendarPlaces.ChannelPost)
            || place.State != CalendarPlaceStates.Failed)
        {
            return false;
        }

        Clear(place, now);
        return true;
    }

    /// <summary>
    /// The bot's role got Manage Events after a place failed for the lack of it. Fixing a role
    /// changes nothing about the event, so without this the refusal stayed until someone edited the
    /// event or pressed Try again (added 2026-10-09, after a tester granted the permission and the
    /// event still said it was missing). False, changing nothing, for any other place or failure.
    /// </summary>
    public static bool ClearAfterManageEventsGranted(CalendarEventPlace place, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(place);

        if (!IsDiscord(place.Place)
            || place.State != CalendarPlaceStates.Failed
            || place.Error is not { } error
            || !error.StartsWith(NeedsManageEvents, StringComparison.Ordinal))
        {
            return false;
        }

        Clear(place, now);
        return true;
    }

    /// <summary>Whether a place is one the Discord loop sends.</summary>
    public static bool IsDiscord(string place) =>
        place is CalendarPlaces.DiscordEvent or CalendarPlaces.ChannelPost or CalendarPlaces.CancelPost;

    private static void Clear(CalendarEventPlace place, DateTimeOffset now)
    {
        place.State = CalendarPlaceStates.Waiting;
        place.FailedFingerprint = null;
        place.Error = null;
        place.ErrorAt = null;
        place.UpdatedAt = now;
    }
}
