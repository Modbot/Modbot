using Modbot.Companion.Overlay;

namespace Modbot.Overlay.Views;

/// <summary>
/// Everything the notification overlay draws, in one immutable snapshot: the pop-ups that are up
/// right now, newest first.
/// </summary>
/// <remarks>
/// <para><strong>Empty means nothing is drawn.</strong> Not a blank frame, not a faint outline —
/// nothing. A panel pinned to the corner of somebody's eye that is usually empty is what makes
/// people turn overlays off (two overlay modes design §2.3).</para>
/// <para><strong>A value, not a live view.</strong> Like the main panel's screen, this is a copy
/// of what the client already holds at a moment. Nothing is fetched while drawing.</para>
/// </remarks>
public sealed record NotificationScreen(IReadOnlyList<PopUp> PopUps)
{
    /// <summary>Nothing to say.</summary>
    public static NotificationScreen Empty { get; } = new([]);

    public bool IsEmpty => PopUps.Count == 0;

    /// <summary>Whether two screens would draw the same pixels.</summary>
    public bool LooksTheSameAs(NotificationScreen other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (PopUps.Count != other.PopUps.Count)
            return false;

        for (var i = 0; i < PopUps.Count; i++)
        {
            if (!string.Equals(PopUps[i].Id, other.PopUps[i].Id, StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
