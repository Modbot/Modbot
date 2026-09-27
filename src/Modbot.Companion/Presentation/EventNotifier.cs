using Modbot.Companion.CloudBackup;
using Modbot.Companion.Instances;
using Modbot.Companion.Journal;
using Modbot.Companion.Overlay;
using Modbot.Companion.Sounds;
using Modbot.Core.Time;
using Modbot.Core.Users;

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
    /// <summary>
    /// How long a join waits for the person's trust rank before it is told without one.
    /// </summary>
    /// <remarks>
    /// The rank comes from the paired server, which only hears of the person when this client
    /// reports them: two seconds after the join, then the server's answer. Five seconds covers that
    /// with room over, and is still soon enough to be about somebody who has just walked in.
    /// </remarks>
    public static readonly TimeSpan RankWait = TimeSpan.FromSeconds(5);

    private readonly Func<string?> _moderatorId;
    private readonly Action<PopUp, NotificationKind>? _popUp;
    private readonly Action<NotificationKind, string?>? _sound;
    private readonly Func<string, TrustRank?>? _rankOf;
    private readonly IModbotClock? _clock;
    private readonly Func<bool>? _rankComing;
    private readonly List<Held> _held = [];
    private readonly Lock _gate = new();

    /// <param name="moderatorId">The moderator's own VRChat id as the log last said, or null while unknown.</param>
    /// <param name="popUp">Where a card goes, or null when there is no notification overlay.</param>
    /// <param name="sound">Where a bleep is asked for, or null when this PC has no sound.</param>
    /// <param name="rankOf">
    /// A person's trust rank as the paired server last said it, or null while it has not. Read from
    /// what the client already holds.
    /// </param>
    /// <param name="clock">
    /// What a held join is timed against. Null tells every join at once, as before there was a rank
    /// to wait for.
    /// </param>
    /// <param name="rankComing">
    /// Whether a rank can arrive at all: true while a paired server covers the instance the
    /// moderator is in. A join in an instance nobody's server watches is told at once.
    /// </param>
    public EventNotifier(
        Func<string?> moderatorId,
        Action<PopUp, NotificationKind>? popUp = null,
        Action<NotificationKind, string?>? sound = null,
        Func<string, TrustRank?>? rankOf = null,
        IModbotClock? clock = null,
        Func<bool>? rankComing = null)
    {
        _moderatorId = moderatorId;
        _popUp = popUp;
        _sound = sound;
        _rankOf = rankOf;
        _clock = clock;
        _rankComing = rankComing;
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

            var rank = kind is NotificationKind.Joined ? _rankOf?.Invoke(observation.SubjectId) : null;

            // A join whose rank is not known yet, in an instance a paired server covers, waits
            // for it: the card and its sound go together, as soon as the rank is in or the wait is
            // up. Everything else is told at once.
            if (kind is NotificationKind.Joined && rank is null && _clock is { } clock && _rankComing?.Invoke() == true)
            {
                lock (_gate)
                    _held.Add(new Held(observation, clock.UtcNow + RankWait));

                continue;
            }

            Tell(observation, kind, rank);
        }
    }

    /// <summary>
    /// Tells the joins that were waiting for a rank: each one whose rank has come in, and each one
    /// that has waited long enough. Called on a timer; does nothing when nothing is waiting.
    /// </summary>
    public void TellWaiting()
    {
        if (_clock is null)
            return;

        List<(ObservedPresence Observation, TrustRank? Rank)> ready = [];

        lock (_gate)
        {
            if (_held.Count == 0)
                return;

            var now = _clock.UtcNow;
            for (var i = 0; i < _held.Count; i++)
            {
                var held = _held[i];
                var rank = _rankOf?.Invoke(held.Observation.SubjectId);
                if (rank is null && now < held.Until)
                    continue;

                ready.Add((held.Observation, rank));
                _held.RemoveAt(i--);
            }
        }

        foreach (var (observation, rank) in ready)
            Tell(observation, NotificationKind.Joined, rank);
    }

    /// <summary>How many joins are waiting for a rank right now.</summary>
    public int Waiting
    {
        get
        {
            lock (_gate)
                return _held.Count;
        }
    }

    private void Tell(ObservedPresence observation, NotificationKind kind, TrustRank? rank)
    {
        _popUp?.Invoke(Card(observation, kind, rank), kind);
        _sound?.Invoke(kind, observation.SubjectId);
    }

    private sealed record Held(ObservedPresence Observation, DateTimeOffset Until);

    /// <summary>
    /// The card one observation becomes.
    /// </summary>
    /// <remarks>
    /// The heading names what happened and the large line names who, which is the shape the
    /// flagged-join card already has. The id is the kind and the person, so the same person
    /// arriving twice restarts one card rather than stacking two. A join card's small line is
    /// the person's trust rank, in VRChat's own words, when it is known.
    /// </remarks>
    public static PopUp Card(ObservedPresence observation, NotificationKind kind, TrustRank? rank = null)
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

        var detail = kind switch
        {
            NotificationKind.ChangedAvatar => observation.AvatarName,
            NotificationKind.Joined when rank is { } known => TrustRanks.Name(known),
            _ => null,
        };

        return new PopUp(
            $"{NotificationFilters.Word(kind)}:{observation.SubjectId}",
            NotificationFilters.Label(kind),
            who,
            detail,
            PopUpTone.Plain);
    }

    /// <summary>
    /// Fills in the trust rank on join cards that are up without one, once the server has said it.
    /// </summary>
    /// <remarks>
    /// A join card goes up the moment VRChat's log says somebody arrived, which is before any
    /// server has heard of them: this client reports the join, and the server's roster and live
    /// events carry the rank a couple of seconds later. So the card is filled in where it stands,
    /// keeping its time, rather than waiting to go up at all. Called on the overlay's own tick.
    /// </remarks>
    public static void AddRanks(PopUps popUps, Func<string, TrustRank?> rankOf)
    {
        ArgumentNullException.ThrowIfNull(popUps);
        ArgumentNullException.ThrowIfNull(rankOf);

        var prefix = $"{NotificationFilters.Word(NotificationKind.Joined)}:";

        popUps.Amend(card =>
            card.Detail is null
            && card.Id.StartsWith(prefix, StringComparison.Ordinal)
            && rankOf(card.Id[prefix.Length..]) is { } rank
                ? card with { Detail = TrustRanks.Name(rank) }
                : card);
    }
}
