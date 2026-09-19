using Modbot.Core.Time;

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
public sealed record PopUp(string Id, string Heading, string Body, string? Detail, PopUpTone Tone);

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
/// </remarks>
public sealed class PopUps
{
    private readonly IModbotClock _clock;
    private readonly List<Shown> _shown = [];
    private readonly Lock _gate = new();

    public PopUps(IModbotClock clock) => _clock = clock;

    /// <summary>How long a new pop-up stays. Set from settings, and changed while running.</summary>
    public TimeSpan Dwell { get; set; } = NotifyOverlaySettings.Default.Dwell;

    /// <summary>At most this many are drawn at once.</summary>
    public int MostAtOnce { get; set; } = NotifyOverlaySettings.MostPopUpsAtOnce;

    /// <summary>Puts one up, newest first. The same id again restarts its time rather than stacking.</summary>
    public void Show(PopUp popUp)
    {
        ArgumentNullException.ThrowIfNull(popUp);

        lock (_gate)
        {
            var now = _clock.UtcNow;
            _shown.RemoveAll(s => string.Equals(s.PopUp.Id, popUp.Id, StringComparison.Ordinal));
            _shown.Insert(0, new Shown(popUp, now));

            while (_shown.Count > Math.Max(1, MostAtOnce))
                _shown.RemoveAt(_shown.Count - 1);
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
    public IReadOnlyList<PopUp> Current()
    {
        lock (_gate)
        {
            var now = _clock.UtcNow;
            _shown.RemoveAll(s => now - s.ShownAt >= Dwell);
            return [.. _shown.Select(s => s.PopUp)];
        }
    }

    private sealed record Shown(PopUp PopUp, DateTimeOffset ShownAt);
}
