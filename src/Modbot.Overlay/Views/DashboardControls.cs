using Avalonia;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.OpenVr;

namespace Modbot.Overlay.Views;

/// <summary>One control on the dashboard tab as last drawn: its label, what it is, how it is set and where.</summary>
/// <param name="Label">The words on it; a slider's minus and plus are its name and the sign.</param>
/// <param name="Kind">switch, choice, button, tick, step or slider.</param>
/// <param name="On">A switch or a tick: on. A choice: the chosen one. Null for the rest.</param>
/// <param name="Value">A slider's value, as the settings hold it. Null for the rest.</param>
/// <param name="Bounds">Where it is on the tab's texture, in pixels from the top left.</param>
public sealed record DashboardControl(string Label, string Kind, bool? On, double? Value, Rect Bounds, DashboardTarget Target);

/// <summary>
/// The dashboard tab's controls by their labels, for the test remote: listed as last drawn, and
/// pressed the way SteamVR's laser presses them.
/// </summary>
/// <remarks>
/// A press goes through <see cref="DashboardHost.Handle"/> with a move, the trigger down and the
/// trigger up at the control's middle, which is exactly what SteamVR's mouse events turn into. Nothing
/// here sets a setting; the host raises the change, and the companion saves it as it saves any press.
/// </remarks>
public static class DashboardControls
{
    /// <summary>A slider's name, without its value: <c>Width</c>, <c>Pop-up stays</c>.</summary>
    public static string SliderName(DashboardSlider slider) => slider switch
    {
        DashboardSlider.Across => "Across",
        DashboardSlider.Down => "Down",
        DashboardSlider.Distance => "Distance",
        DashboardSlider.Width => "Width",
        DashboardSlider.Opacity => "Opacity",
        _ => "Pop-up stays",
    };

    /// <summary>The words a control is called by.</summary>
    public static string LabelOf(DashboardTarget target) => target switch
    {
        DashboardTarget.Toggle { Which: DashboardSwitch.Overlay } => "Overlay on",
        DashboardTarget.Toggle => "Notification overlay on",
        DashboardTarget.FixTo fix => fix.Anchor switch
        {
            OverlayAnchor.Head => "Head",
            OverlayAnchor.LeftHand => "Left wrist",
            OverlayAnchor.RightHand => "Right wrist",
            _ => "Room",
        },
        DashboardTarget.PutBack => "Put it back in front of me",
        DashboardTarget.Spot spot => NotifyOverlaySettings.Name(spot.Where),
        DashboardTarget.PopUp tick => NotificationFilters.Label(tick.Kind),
        DashboardTarget.Step step => SliderName(step.Which) + (step.By < 0 ? " -" : " +"),
        DashboardTarget.Track track => SliderName(track.Which),
        _ => target.GetType().Name,
    };

    /// <summary>Every control on the tab as last drawn. Empty while the tab is not drawn.</summary>
    public static IReadOnlyList<DashboardControl> Of(DashboardHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var screen = host.Showing;
        return [.. host.Targets.Select(placed => Describe(placed, screen))];
    }

    /// <summary>The control with this label, ignoring case; a typed <c>-</c> matches the minus sign.</summary>
    public static DashboardControl? Find(IReadOnlyList<DashboardControl> controls, string label)
    {
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(label);

        var wanted = Plain(label);
        return controls.FirstOrDefault(c => string.Equals(Plain(c.Label), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Presses a control by its label: the laser moves to its middle, the trigger goes down and
    /// comes up. Null when no control has that label, or the tab is not drawn.
    /// </summary>
    public static DashboardControl? Press(DashboardHost host, string label)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (Find(Of(host), label) is not { } control)
            return null;

        var at = control.Bounds.Center;
        Point(host, at.X, at.Y);
        return control;
    }

    /// <summary>
    /// Presses a slider's track at a fraction of the way along it, where its knob's middle would sit
    /// at that value. Null when no slider has that label.
    /// </summary>
    public static DashboardControl? Slide(DashboardHost host, string label, double fraction)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (Find(Of(host), label) is not { Target: DashboardTarget.Track } track)
            return null;

        var bounds = track.Bounds;
        var travel = Math.Max(0, bounds.Width - DashboardView.KnobSide);
        var x = bounds.X + (DashboardView.KnobSide / 2) + (Math.Clamp(fraction, 0, 1) * travel);
        Point(host, x, bounds.Center.Y);
        return track;
    }

    /// <summary>What SteamVR's laser sends for one press: a move there, the trigger down, the trigger up.</summary>
    private static void Point(DashboardHost host, double x, double y)
    {
        host.Handle(new DashboardPointer(DashboardPointerKind.Move, x, y));
        host.Handle(new DashboardPointer(DashboardPointerKind.Down, x, y));
        host.Handle(new DashboardPointer(DashboardPointerKind.Up, x, y));
    }

    private static DashboardControl Describe(PlacedDashboardTarget placed, DashboardScreen screen)
    {
        var target = placed.Target;
        var notify = screen.Notify;
        var label = LabelOf(target);

        return target switch
        {
            DashboardTarget.Toggle { Which: DashboardSwitch.Overlay } => new(label, "switch", screen.OverlayOn, null, placed.Bounds, target),
            DashboardTarget.Toggle => new(label, "switch", notify.On, null, placed.Bounds, target),
            DashboardTarget.FixTo fix => new(label, "choice", fix.Anchor == screen.Anchor, null, placed.Bounds, target),
            DashboardTarget.Spot spot => new(label, "choice", spot.Where == notify.Spot && notify.Placed is null, null, placed.Bounds, target),
            DashboardTarget.PopUp tick => new(label, "tick", screen.Filters.PopUpShows(tick.Kind), null, placed.Bounds, target),
            DashboardTarget.Step => new(label, "step", null, null, placed.Bounds, target),
            DashboardTarget.Track track => new(label, "slider", null, Math.Round(DashboardEdits.ValueOf(notify, track.Which), 3), placed.Bounds, target),
            _ => new(label, "button", null, null, placed.Bounds, target),
        };
    }

    private static string Plain(string label) => label.Trim().Replace('−', '-');
}
