using Modbot.Companion.CloudBackup;
using Modbot.Companion.Instances;
using Modbot.Companion.Journal;
using Modbot.Companion.Overlay;
using Modbot.Companion.Sounds;

namespace Modbot.Companion.Presentation;

/// <summary>
/// Turns what the client read out of VRChat's log into a pop-up and a sound, for the kinds the
/// moderator has ticked.
/// </summary>
/// <remarks>
/// <para><strong>Why it exists.</strong> Before the Notifications card's filters there was nothing
/// to do here: the only card and the only bleep came from the paired server's flagged-join alert
/// and from the companion's own problems, both of which are raised elsewhere. A moderator can now
/// ask for a card or a bleep when anybody arrives, leaves, was already there, changes avatar, or
/// when VRChat's log stops, so something has to offer those to the two surfaces. All five are off
/// by default (notification filters design 2026-09-19 §4).</para>
/// <para><strong>It does not decide.</strong> Whether a card is drawn is
/// <see cref="PopUps.Wanted"/>'s answer and whether a sound plays is the sound's own; this only
/// puts the question, once per observation, so there is one gate per way and no second copy of the
/// rule here.</para>
/// <para><strong>Never the moderator themselves.</strong> Their own arrival and departure are
/// dropped, the same way the voice drops them — they were there. A stopped log is the exception,
/// because that one is about them and is the thing worth knowing.</para>
/// <para><strong>It reads nothing and sends nothing.</strong> It hears observations the engine has
/// already made and hands them to two things that play a sound and draw a card on this PC. No
/// server is told that either happened.</para>
/// </remarks>
public sealed class EventNotifier : IObservationSink
{
    private readonly Func<string?> _moderatorId;
    private readonly Action<PopUp, NotificationKind>? _popUp;
    private readonly Action<NotificationKind, string?>? _sound;

    /// <param name="moderatorId">The moderator's own VRChat id as the log last said, or null while unknown.</param>
    /// <param name="popUp">Where a card goes, or null when there is no notification overlay.</param>
    /// <param name="sound">Where a bleep is asked for, or null when this PC has no sound.</param>
    public EventNotifier(
        Func<string?> moderatorId,
        Action<PopUp, NotificationKind>? popUp = null,
        Action<NotificationKind, string?>? sound = null)
    {
        _moderatorId = moderatorId;
        _popUp = popUp;
        _sound = sound;
    }

    public void Offer(IReadOnlyList<ObservedPresence> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        if (_popUp is null && _sound is null)
            return;

        var moderator = _moderatorId();

        foreach (var observation in observations)
        {
            if (observation.Kind is not PresenceKind.LogStopped
                && moderator is not null
                && string.Equals(observation.SubjectId, moderator, StringComparison.Ordinal))
            {
                continue;
            }

            if (NotificationFilters.KindOf(observation.Kind) is not { } kind)
                continue;

            _popUp?.Invoke(Card(observation, kind), kind);
            _sound?.Invoke(kind, observation.SubjectId);
        }
    }

    /// <summary>
    /// The card one observation becomes.
    /// </summary>
    /// <remarks>
    /// The heading names what happened and the large line names who, which is the shape the
    /// flagged-join card already has. The id is the kind and the person, so the same person
    /// arriving twice restarts one card rather than stacking two.
    /// </remarks>
    public static PopUp Card(ObservedPresence observation, NotificationKind kind)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var who = string.IsNullOrWhiteSpace(observation.DisplayName)
            ? observation.SubjectId
            : observation.DisplayName;

        if (kind is NotificationKind.LogStopped)
        {
            return new PopUp(
                "log-stopped",
                "Modbot",
                SentJournal.Sentence(PresenceKind.LogStopped, who),
                null,
                PopUpTone.Problem);
        }

        return new PopUp(
            $"{NotificationFilters.Word(kind)}:{observation.SubjectId}",
            NotificationFilters.Label(kind),
            who,
            kind is NotificationKind.ChangedAvatar ? observation.AvatarName : null,
            PopUpTone.Plain);
    }
}
