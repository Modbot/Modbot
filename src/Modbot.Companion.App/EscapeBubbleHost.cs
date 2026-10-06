using Avalonia;
using Avalonia.Threading;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.App;

/// <summary>
/// Keeps Modbot's bubble in VRChat's HUD row while the desktop overlay is switched on: shown when
/// VRChat's window is there, gone when it is not, never a window left behind.
/// </summary>
/// <remarks>
/// <para><strong>What decides is <see cref="EscapeBubblePlan"/></strong>, which is plain code with
/// tests. This only asks <c>VRChatWindow.cs</c> where VRChat is, four times a second, and carries
/// the answer out on an <see cref="EscapeBubbleWindow"/>.</para>
/// <para><strong>It never takes the keyboard.</strong> The window is shown without being
/// activated, and a click on it is the same action as the shortcut (the owner's
/// <c>toggle</c>), so the overlay opens and closes by the one piece of code that already does.</para>
/// <para><strong>Sizes.</strong> VRChat's rectangle and the bubble's position are in desktop pixels;
/// the bubble window's own sizes are in a screen's pixels over its scale. So the sizes are asked of
/// the metrics at the scale divided by the scale of the screen the bubble lands on, which makes the
/// bubble the size of the bubbles beside it on a screen that is not at 100%.</para>
/// </remarks>
internal sealed class EscapeBubbleHost
{
    private readonly Func<bool> _overlayVisible;
    private readonly Action _toggle;
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private EscapeBubbleWindow? _window;
    private EscapeBubblePlan? _drawn;
    private nint _vrchat;
    private string _shortcut = DesktopOverlaySettings.DefaultShortcut;
    private bool _on;

    /// <param name="overlayVisible">Whether the desktop overlay's window is up now.</param>
    /// <param name="toggle">What the shortcut does: opens the overlay, or closes it.</param>
    public EscapeBubbleHost(Func<bool> overlayVisible, Action toggle)
    {
        _overlayVisible = overlayVisible ?? throw new ArgumentNullException(nameof(overlayVisible));
        _toggle = toggle ?? throw new ArgumentNullException(nameof(toggle));
        _watch.Tick += (_, _) => Refresh();
    }

    /// <summary>Whether a bubble is on screen now.</summary>
    public bool Showing => _window is not null;

    /// <summary>The bubble has just come on screen, so a preview of one can go.</summary>
    public event Action? Appeared;

    /// <summary>The desktop overlay is on: look for VRChat from now on.</summary>
    public void Start(string shortcut)
    {
        _on = true;
        _shortcut = shortcut;
        _watch.Start();
        Refresh();
    }

    /// <summary>The overlay's shortcut was changed: the bubble says the new one.</summary>
    public void SetShortcut(string shortcut)
    {
        if (_shortcut == shortcut)
            return;

        _shortcut = shortcut;
        Refresh();
    }

    /// <summary>The desktop overlay is off: no more looking, and no bubble.</summary>
    public void Stop()
    {
        _on = false;
        _watch.Stop();
        Refresh();
    }

    /// <summary>Looks at VRChat's window and puts the bubble as it should be, or takes it away.</summary>
    public void Refresh()
    {
        var look = _on ? VRChatWindow.Look(_vrchat) : default;
        _vrchat = look.Handle;

        var plan = EscapeBubblePlan.For(_on, _overlayVisible(), look.Window, look.Left, look.Top, _shortcut);

        if (plan is null)
        {
            Close();
            return;
        }

        if (_window is not null && plan == _drawn)
            return;

        var appeared = _window is null;
        var window = _window ??= Build();

        var centre = new PixelPoint(plan.ScreenX, plan.ScreenY);
        var screen = window.Screens.ScreenFromPoint(centre) ?? window.Screens.Primary;
        var scaling = screen is { Scaling: > 0 } ? screen.Scaling : 1;

        window.Apply(new EscapeBubbleMetrics(plan.Metrics.Scale / scaling));
        window.SetLabel(plan.Label);
        window.SetLit(plan.Lit);
        window.PlaceAt(centre);
        _drawn = plan;

        if (appeared)
        {
            window.Show();
            Appeared?.Invoke();
        }
    }

    private EscapeBubbleWindow Build()
    {
        var window = new EscapeBubbleWindow();
        window.Clicked += () =>
        {
            _toggle();
            Refresh();
        };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
            {
                _window = null;
                _drawn = null;
            }
        };

        return window;
    }

    private void Close()
    {
        var window = _window;
        _window = null;
        _drawn = null;
        window?.Close();
    }
}
