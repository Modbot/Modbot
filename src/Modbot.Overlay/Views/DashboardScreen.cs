using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Companion.Sounds;

namespace Modbot.Overlay.Views;

/// <summary>
/// What the SteamVR dashboard tab shows: the settings it can change, read from the companion's
/// own settings each time they change.
/// </summary>
/// <remarks>
/// The same settings the window's SteamVR page and its <strong>Tell me about</strong> card show,
/// and changed through the same calls, so the tab and the window never disagree for longer than
/// one redraw.
/// </remarks>
/// <param name="OverlayOn">The main panel's switch (<c>overlayOn</c>).</param>
/// <param name="Anchor">What the main panel is fixed to (<c>overlay.anchor</c>).</param>
/// <param name="Notify">The notification overlay (<c>notifyOverlay</c>).</param>
/// <param name="Filters">Which kinds of event pop up (<c>notificationFilters.popUp</c>), with the other two ways as they are.</param>
public sealed record DashboardScreen(
    bool OverlayOn,
    OverlayAnchor Anchor,
    NotifyOverlaySettings Notify,
    NotificationFilters Filters)
{
    /// <summary>Everything at its default, for a tab drawn before the settings have been read.</summary>
    public static DashboardScreen Default { get; } = new(
        true,
        OverlayPlacement.Default.Anchor,
        NotifyOverlaySettings.Default,
        NotificationFilters.Default);
}

/// <summary>The two switches on the tab.</summary>
public enum DashboardSwitch
{
    /// <summary>The main panel, on or off.</summary>
    Overlay,

    /// <summary>The notification overlay, on or off.</summary>
    Notifications,
}

/// <summary>The notification overlay's numbers, each a slider on the tab.</summary>
public enum DashboardSlider
{
    Across,
    Down,
    Distance,
    Width,
    Opacity,
    Seconds,
}

/// <summary>Something on the tab the laser can press.</summary>
/// <remarks>The view marks a control by putting one of these in its <c>Tag</c>, as the panels do.</remarks>
public abstract record DashboardTarget
{
    /// <summary>A switch: pressing it turns it the other way.</summary>
    public sealed record Toggle(DashboardSwitch Which) : DashboardTarget;

    /// <summary>One of the main panel's anchors.</summary>
    public sealed record FixTo(OverlayAnchor Anchor) : DashboardTarget;

    /// <summary><strong>Put it back in front of me</strong>, for the main panel.</summary>
    public sealed record PutBack : DashboardTarget;

    /// <summary>One of the notification overlay's six places on the screen.</summary>
    public sealed record Spot(ScreenSpot Where) : DashboardTarget;

    /// <summary>One kind of event's pop-up tick.</summary>
    public sealed record PopUp(NotificationKind Kind) : DashboardTarget;

    /// <summary>A slider's minus (-1) or plus (+1).</summary>
    public sealed record Step(DashboardSlider Which, int By) : DashboardTarget;

    /// <summary>A slider's track: pressed or dragged, the value follows the laser across it.</summary>
    public sealed record Track(DashboardSlider Which) : DashboardTarget;
}

/// <summary>A target and where it was drawn, in tab pixels.</summary>
public readonly record struct PlacedDashboardTarget(DashboardTarget Target, Rect Bounds);

/// <summary>
/// Finds the tab's targets in a laid-out tree, the way <c>OverlayTargets</c> finds a panel's:
/// by walking it with a running offset, deepest target winning.
/// </summary>
public static class DashboardTargets
{
    public static IReadOnlyList<PlacedDashboardTarget> Find(Visual root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var found = new List<PlacedDashboardTarget>();
        Walk(root, new Point(0, 0), found);
        return found;
    }

    /// <summary>The deepest target under a point, with where it was drawn, or null.</summary>
    public static PlacedDashboardTarget? At(Visual root, Point point)
    {
        PlacedDashboardTarget? deepest = null;
        foreach (var placed in Find(root))
        {
            if (placed.Bounds.Contains(point))
                deepest = placed;
        }

        return deepest;
    }

    private static void Walk(Visual visual, Point offset, List<PlacedDashboardTarget> found)
    {
        var bounds = visual.Bounds;
        var origin = new Point(offset.X + bounds.X, offset.Y + bounds.Y);

        if (visual is Control { Tag: DashboardTarget target })
            found.Add(new PlacedDashboardTarget(target, new Rect(origin, bounds.Size)));

        foreach (var child in visual.GetVisualChildren())
            Walk(child, origin, found);
    }
}

/// <summary>
/// What each press on the tab changes, kept apart from SteamVR and from drawing so it can be
/// checked on its own.
/// </summary>
/// <remarks>
/// The sliders' bounds and steps are the window's: the notification overlay's own limits, moved
/// by the same step the window's minus and plus move them.
/// </remarks>
public static class DashboardEdits
{
    /// <summary>A slider's lowest and highest value, and how far one step moves it.</summary>
    public readonly record struct Range(double Minimum, double Maximum, double Step)
    {
        /// <summary>A value inside the range, on a step.</summary>
        public double Snap(double value)
        {
            if (!double.IsFinite(value))
                value = Minimum;

            var steps = Math.Round((value - Minimum) / Step);
            return Math.Clamp(Minimum + (steps * Step), Minimum, Maximum);
        }

        /// <summary>The value at a fraction of the way along the track, snapped to a step.</summary>
        public double At(double fraction) => Snap(Minimum + (Math.Clamp(fraction, 0, 1) * (Maximum - Minimum)));

        /// <summary>How far along the track a value sits, 0 to 1.</summary>
        public double FractionOf(double value)
            => Maximum > Minimum ? Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1) : 0;
    }

    public static Range RangeOf(DashboardSlider slider) => slider switch
    {
        DashboardSlider.Across => new(-NotifyOverlaySettings.MaxFine, NotifyOverlaySettings.MaxFine, 0.02),
        DashboardSlider.Down => new(-NotifyOverlaySettings.MaxFine, NotifyOverlaySettings.MaxFine, 0.02),
        DashboardSlider.Distance => new(NotifyOverlaySettings.MinDistance, NotifyOverlaySettings.MaxDistance, 0.05),
        DashboardSlider.Width => new(NotifyOverlaySettings.MinWidth, NotifyOverlaySettings.MaxWidth, 0.05),
        DashboardSlider.Opacity => new(NotifyOverlaySettings.MinOpacity, 1, 0.05),
        _ => new(NotifyOverlaySettings.MinSeconds, NotifyOverlaySettings.MaxSeconds, 1),
    };

    /// <summary>A slider's value as the settings hold it.</summary>
    public static double ValueOf(NotifyOverlaySettings settings, DashboardSlider slider)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return slider switch
        {
            DashboardSlider.Across => settings.Across,
            DashboardSlider.Down => settings.Down,
            DashboardSlider.Distance => settings.Distance,
            DashboardSlider.Width => settings.Width,
            DashboardSlider.Opacity => settings.Opacity,
            _ => settings.Seconds,
        };
    }

    /// <summary>The settings with one slider moved to a value, snapped and kept inside its range.</summary>
    public static NotifyOverlaySettings With(NotifyOverlaySettings settings, DashboardSlider slider, double value)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var snapped = (float)RangeOf(slider).Snap(value);
        return slider switch
        {
            DashboardSlider.Across => settings with { Across = snapped },
            DashboardSlider.Down => settings with { Down = snapped },
            DashboardSlider.Distance => settings with { Distance = snapped },
            DashboardSlider.Width => settings with { Width = snapped },
            DashboardSlider.Opacity => settings with { Opacity = snapped },
            _ => settings with { Seconds = snapped },
        };
    }

    /// <summary>The settings with a slider moved one step up (+1) or down (-1).</summary>
    public static NotifyOverlaySettings Stepped(NotifyOverlaySettings settings, DashboardSlider slider, int by)
        => With(settings, slider, ValueOf(settings, slider) + (Math.Sign(by) * RangeOf(slider).Step));

    /// <summary>The slider's label with its value, written the way the window writes it.</summary>
    public static string Label(NotifyOverlaySettings settings, DashboardSlider slider)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return slider switch
        {
            DashboardSlider.Across => $"Across {settings.Across:0.00} m",
            DashboardSlider.Down => $"Down {settings.Down:0.00} m",
            DashboardSlider.Distance => $"Distance {settings.Distance:0.00} m",
            DashboardSlider.Width => $"Width {settings.Width:0.00} m",
            DashboardSlider.Opacity => $"Opacity {settings.Opacity:0%}",
            _ => $"Pop-up stays {settings.Seconds:0} s",
        };
    }
}
