using Modbot.Overlay.Driving;
using Modbot.Overlay.Views;

namespace Modbot.Companion.App;

/// <summary>
/// The two places one overlay screen can be shown: the headset panel and the desktop overlay.
/// </summary>
/// <remarks>
/// <para>The drive loop builds one screen and pushes it once. There is nothing about a screen that
/// belongs to a headset, so the same one goes to both panels, and either may be absent — a
/// moderator with no headset switch on has only the window, and a moderator who never opens the
/// window has only the panel.</para>
/// <para>Looked up through functions rather than held, because each panel comes and goes while the
/// loop runs: the headset host is built and dropped by the overlay switch, and the window is
/// created the first time it is asked for.</para>
/// <para>It reads nothing and sends nothing; it hands a value to whichever panels exist.</para>
/// </remarks>
internal sealed class OverlayScreens(Func<IOverlayPresenter?> headset, Func<IOverlayPresenter?> desktop) : IOverlayPresenter
{
    /// <summary>
    /// Shows the screen on both panels, and says whether either of them drew.
    /// </summary>
    /// <remarks>
    /// Both are asked, never short-circuited: a panel that skipped its update because the other
    /// one had already reported a draw would go stale.
    /// </remarks>
    public bool Update(OverlayScreen screen)
    {
        var drew = headset()?.Update(screen) ?? false;
        return (desktop()?.Update(screen) ?? false) || drew;
    }

    public void Show()
    {
        headset()?.Show();
        desktop()?.Show();
    }

    public void Hide()
    {
        headset()?.Hide();
        desktop()?.Hide();
    }
}
