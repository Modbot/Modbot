using Modbot.Companion.Sounds;
using Modbot.Core.Time;
using Modbot.Core.Users;

namespace Modbot.Companion.Overlay;

/// <summary>How loud a pop-up is: it decides the colour, and nothing else.</summary>
public enum PopUpTone
{
    /// <summary>Something happened. A moderator's own action, a person arriving.</summary>
    Plain,

    /// <summary>Something is wrong with Modbot itself: a rejected token, a server out of reach.</summary>
    Problem,

    /// <summary>The one that matters: a flagged person has walked in.</summary>
    Flagged,
}

/// <summary>
/// One short-lived pop-up on the notification overlay.
/// </summary>
/// <param name="Id">
/// What makes two pop-ups the same thing. The same id twice does not stack up a second card; it
/// restarts the first one's time.
/// </param>
/// <param name="Heading">The line above, in small dim text. Usually the group.</param>
/// <param name="Body">The line that matters, large.</param>
/// <param name="Detail">One more line, or null.</param>
/// <param name="Rank">
/// The person's VRChat trust rank, drawn as the roster row draws it: a dot in the rank's own
/// colour and its name. Null on every card that is not about a person, and on a person's card
/// until the server has said it.
/// </param>
/// <param name="EighteenPlus">
/// Whether the person carries Modbot's 18+ mark, drawn as the roster row's green chip. False
/// draws nothing, the way the Members list shows the badge only on those who have it.
/// </param>
/// <param name="SubjectId">
/// Who the card is about, so their rank and 18+ mark can be written onto it once the server has
/// said them. Null on a card that is about nobody in particular.
/// </param>
/// <remarks>
/// The rank and the mark are kept as what they are rather than written into <paramref name="Detail"/>,
/// because a line of text has nowhere to hold the rank's colour.
/// </remarks>
public sealed record PopUp(
    string Id,
    string Heading,
    string Body,
    string? Detail,
    PopUpTone Tone,
    TrustRank? Rank = null,
    bool EighteenPlus = false,
    string? SubjectId = null)
{
    /// <summary>Whether the card has a rank or an 18+ mark to draw.</summary>
    public bool HasPersonInfo => Rank is not null || EighteenPlus;
}

/// <summary>
/// The pop-ups the notification overlay is showing, and when each one's time is up.
/// </summary>
/// <remarks>
/// <para><strong>Nothing is queued.</strong> A pop-up is about a moment; one shown later
/// interrupts a moderator with news from twenty minutes ago, about an instance that has since
/// emptied. When more arrive than there is room for, the oldest goes, not the newest.</para>
/// <para><strong>Nothing here is transmitted, and nothing here is read from a server.</strong>
/// These are made by the client out of what it already holds, and they live in memory until their
/// time is up.</para>
/// <para><strong>The moderator's filters are asked here.</strong> <see cref="Wanted"/> is the
/// pop-up column of the Notifications card, so every card in the client passes one gate rather
/// than each caller remembering to ask (notification filters design 2026-09-19 §5).</para>
/// </remarks>
public sealed class PopUps
{
    private readonly IModbotClock _clock;
    private readonly List<Shown> _shown = [];
    private readonly Lock _gate = new();

    public PopUps(IModbotClock clock) => _clock = clock;

    /// <summary>
    /// How long a pop-up is kept at all. Set from settings, and changed while running.
    /// </summary>
    /// <remarks>
    /// There is more than one surface a pop-up can appear on — the headset's notification overlay
    /// and the one on a monitor — and each has its own number of seconds. This is the longest of
    /// them, so a card is dropped only once nobody wants it any more; a surface that shows it for
    /// less says so when it asks (<see cref="Current(TimeSpan)"/>).
    /// </remarks>
    public TimeSpan Dwell { get; set; } = NotifyOverlaySettings.Default.Dwell;

    /// <summary>At most this many are drawn at once.</summary>
    public int MostAtOnce { get; set; } = NotifyOverlaySettings.MostPopUpsAtOnce;

    /// <summary>
    /// Whether the moderator wants a card for this kind. Set from settings, and read at the moment
    /// of showing, so a tick changed mid-session takes effect at once. Null lets everything
    /// through.
    /// </summary>
    public Func<NotificationKind, bool>? Wanted { get; set; }

    /// <summary>
    /// Puts one up, newest first. The same id again restarts its time rather than stacking.
    /// </summary>
    /// <param name="kind">
    /// What happened, so the moderator's filters can be asked. Null for a card that is not a
    /// notification — the overlay preview and the sample screens — which is never filtered.
    /// </param>
    public void Show(PopUp popUp, NotificationKind? kind = null)
    {
        ArgumentNullException.ThrowIfNull(popUp);

        if (kind is { } asked && Wanted is { } wanted && !wanted(asked))
            return;

        lock (_gate)
        {
            var now = _clock.UtcNow;
            _shown.RemoveAll(s => string.Equals(s.PopUp.Id, popUp.Id, StringComparison.Ordinal));
            _shown.Insert(0, new Shown(popUp, now));

            while (_shown.Count > Math.Max(1, MostAtOnce))
                _shown.RemoveAt(_shown.Count - 1);
        }
    }

    /// <summary>
    /// Changes the words on cards that are already up, keeping their place and their time. A
    /// change that would give a card a different id is ignored.
    /// </summary>
    /// <remarks>
    /// For something learned after the card went up — a person's trust rank, which the server
    /// only knows once this client has reported them. Showing the card again would restart its
    /// time and move it to the top; filling it in does neither.
    /// </remarks>
    public void Amend(Func<PopUp, PopUp> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        lock (_gate)
        {
            for (var i = 0; i < _shown.Count; i++)
            {
                var was = _shown[i].PopUp;
                var now = change(was);

                if (now != was && string.Equals(now.Id, was.Id, StringComparison.Ordinal))
                    _shown[i] = _shown[i] with { PopUp = now };
            }
        }
    }

    /// <summary>Takes one away early, by id.</summary>
    public void Clear(string id)
    {
        lock (_gate)
            _shown.RemoveAll(s => string.Equals(s.PopUp.Id, id, StringComparison.Ordinal));
    }

    /// <summary>Takes them all away: leaving an instance, or the overlay being switched off.</summary>
    public void ClearAll()
    {
        lock (_gate)
            _shown.Clear();
    }

    /// <summary>
    /// What to draw now, newest first, with anything whose time is up already dropped. Empty means
    /// the notification overlay draws nothing at all.
    /// </summary>
    public IReadOnlyList<PopUp> Current() => Current(Dwell);

    /// <summary>
    /// The same, for a surface that shows a pop-up for its own number of seconds.
    /// </summary>
    /// <remarks>
    /// <para>Asking for longer than <see cref="Dwell"/> gets <see cref="Dwell"/>: what has already
    /// been dropped is gone, and nothing is queued.</para>
    /// <para>A card shown more than its time "after" now goes too. That only happens when this
    /// PC's clock is set back, and without it the card would stay up for as long as the clock was
    /// moved.</para>
    /// </remarks>
    public IReadOnlyList<PopUp> Current(TimeSpan dwell)
    {
        lock (_gate)
        {
            var now = _clock.UtcNow;
            _shown.RemoveAll(s => now - s.ShownAt >= Dwell || s.ShownAt - now >= Dwell);
            return [.. _shown.Where(s => now - s.ShownAt < dwell && s.ShownAt - now < dwell).Select(s => s.PopUp)];
        }
    }

    private sealed record Shown(PopUp PopUp, DateTimeOffset ShownAt);
}
