using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Path = Avalonia.Controls.Shapes.Path;

namespace Modbot.Overlay.Views;

/// <summary>
/// Builds the SteamVR dashboard tab: a settings page in three cards, Overlay, Notifications and
/// Placement, drawn in the headset palette.
/// </summary>
/// <remarks>
/// <para><strong>Controls and their names, nothing else.</strong> Switches, rows of choices, ticks
/// and sliders, each with a plain label, as on the window's SteamVR page.</para>
/// <para><strong>Sized for a laser at the dashboard's distance.</strong> Every control is at least
/// 60 pixels tall on a page 2.5 metres across, and a slider has a minus and a plus beside it for a
/// hand that cannot land the laser on one step of the track.</para>
/// <para><strong>Nothing here reads SteamVR.</strong> What is on and where is handed in; the
/// controls are marked with <see cref="DashboardTarget"/>s in their tags, and the host finds what
/// a press landed on by walking the laid-out tree.</para>
/// </remarks>
public static class DashboardView
{
    private static DesignTokens T => DesignTokens.Vr;

    private const double Gutter = 32;
    private const double CardPadding = 24;
    private const double HeadingSize = 32;
    private const double LabelSize = 24;
    private const double ControlHeight = 60;
    private const double ControlText = 24;

    /// <summary>
    /// The slider's knob, across. Its middle travels from half this in from the track's left end
    /// to half this in from its right, which is the stretch a press on the track is read against.
    /// </summary>
    public const double KnobSide = 40;

    /// <param name="width">The tab's texture, across, in pixels.</param>
    /// <param name="height">And down.</param>
    public static Control Build(DashboardScreen screen, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(screen);

        var left = new StackPanel
        {
            Spacing = Gutter,
            Children =
            {
                Card("Overlay", OverlayLines(screen)),
                Card("Notifications", NotificationLines(screen)),
            },
        };

        var right = Card("Placement", PlacementLines(screen.Notify));

        var columns = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = Gutter,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        columns.Children.Add(left);
        columns.Children.Add(right);
        right.VerticalAlignment = VerticalAlignment.Top;

        return new Border
        {
            Width = width,
            Height = height,
            Background = T.BackgroundBrush,
            Padding = new Thickness(Gutter),
            ClipToBounds = true,
            Child = columns,
        };
    }

    private static Control[] OverlayLines(DashboardScreen screen)
    {
        // The window's words for the same four anchors.
        (OverlayAnchor Anchor, string Caption)[] anchors =
        [
            (OverlayAnchor.Head, "Head"),
            (OverlayAnchor.LeftHand, "Left wrist"),
            (OverlayAnchor.RightHand, "Right wrist"),
            (OverlayAnchor.World, "Room"),
        ];

        var choices = anchors.Select(a => (
            Target: (DashboardTarget)new DashboardTarget.FixTo(a.Anchor),
            a.Caption,
            Chosen: a.Anchor == screen.Anchor));

        return
        [
            Switch(DashboardSwitch.Overlay, "Overlay on", screen.OverlayOn),
            Field("Fixed to", Choices(choices, 4)),
        ];
    }

    private static Control[] NotificationLines(DashboardScreen screen)
    {
        var notify = screen.Notify;
        var spots = Enum.GetValues<ScreenSpot>().Select(spot => (
            Target: (DashboardTarget)new DashboardTarget.Spot(spot),
            Caption: NotifyOverlaySettings.Name(spot),
            Chosen: spot == notify.Spot && notify.Placed is null));

        // Two across, so the longest name ("Changed avatar") is never cut short.
        const int across = 2;
        var ticks = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 10,
            RowSpacing = 10,
        };

        var kinds = NotificationFilters.Kinds;
        for (var row = 0; row < (kinds.Count + across - 1) / across; row++)
            ticks.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (var index = 0; index < kinds.Count; index++)
        {
            var kind = kinds[index];
            var tick = Tick(new DashboardTarget.PopUp(kind), NotificationFilters.Label(kind), screen.Filters.PopUpShows(kind));
            Grid.SetRow(tick, index / across);
            Grid.SetColumn(tick, index % across);
            ticks.Children.Add(tick);
        }

        return
        [
            Switch(DashboardSwitch.Notifications, "Notification overlay on", notify.On),
            Field("Where on the screen", Choices(spots, 3)),
            Field(NotificationFilters.Label(NotificationWay.PopUp), ticks),
        ];
    }

    private static Control[] PlacementLines(NotifyOverlaySettings notify)
        => [.. Enum.GetValues<DashboardSlider>().Select(slider => Field(DashboardEdits.Label(notify, slider), Slider(notify, slider)))];

    private static Control Card(string heading, Control[] lines)
    {
        var stack = new StackPanel { Spacing = 20 };
        stack.Children.Add(Text(heading, HeadingSize, T.TextBrush, FontWeight.SemiBold));
        foreach (var line in lines)
            stack.Children.Add(line);

        return new Border
        {
            Background = T.SurfaceBrush,
            BorderBrush = T.BorderBrush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(CardPadding),
            Child = stack,
        };
    }

    private static Control Field(string label, Control control) => new StackPanel
    {
        Spacing = 10,
        Children = { Text(label, LabelSize, T.TextDimBrush), control },
    };

    private static TextBlock Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) => new()
    {
        Text = text,
        FontFamily = new FontFamily(DesignTokens.FontFamily),
        FontSize = size,
        FontWeight = weight,
        Foreground = brush,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A switch with its name: a track that fills with the accent and a knob that moves right when on.</summary>
    private static Control Switch(DashboardSwitch which, string label, bool on)
    {
        const double trackWidth = 96;
        const double trackHeight = 52;
        const double knob = 40;

        var track = new Border
        {
            Width = trackWidth,
            Height = trackHeight,
            CornerRadius = new CornerRadius(trackHeight / 2),
            Background = on ? T.AccentBrush : T.Surface3Brush,
            BorderBrush = on ? T.AccentBrush : T.Border2Brush,
            BorderThickness = new Thickness(T.Density.Hairline),
            Child = new Ellipse
            {
                Width = knob,
                Height = knob,
                Fill = on ? T.AccentForegroundBrush : T.TextDimBrush,
                HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4),
            },
        };

        return new Border
        {
            Tag = new DashboardTarget.Toggle(which),
            Background = Brushes.Transparent,
            MinHeight = ControlHeight,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 20,
                Children = { track, Text(label, ControlText, T.TextBrush) },
            },
        };
    }

    /// <summary>Choices in rows of <paramref name="across"/>, the chosen one filled with the accent.</summary>
    private static Control Choices(IEnumerable<(DashboardTarget Target, string Caption, bool Chosen)> choices, int across)
    {
        var list = choices.ToList();
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 10 };

        for (var column = 0; column < across; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        for (var row = 0; row < (list.Count + across - 1) / across; row++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (var index = 0; index < list.Count; index++)
        {
            var (target, caption, chosen) = list[index];
            var button = Button(target, caption, chosen);
            Grid.SetRow(button, index / across);
            Grid.SetColumn(button, index % across);
            grid.Children.Add(button);
        }

        return grid;
    }

    private static Control Button(DashboardTarget target, string caption, bool chosen, double textSize = ControlText)
    {
        var text = Text(caption, textSize, chosen ? T.AccentForegroundBrush : T.TextBrush);
        text.HorizontalAlignment = HorizontalAlignment.Center;

        return new Border
        {
            Tag = target,
            Height = ControlHeight,
            CornerRadius = new CornerRadius(12),
            Background = chosen ? T.AccentBrush : T.Surface2Brush,
            BorderBrush = chosen ? T.AccentBrush : T.Border2Brush,
            BorderThickness = new Thickness(T.Density.Hairline),
            Padding = new Thickness(12, 0),
            Child = text,
        };
    }

    /// <summary>A tick box with its name, the whole of it pressable.</summary>
    private static Control Tick(DashboardTarget target, string caption, bool on)
    {
        var box = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            Background = on ? T.AccentBrush : T.Surface3Brush,
            BorderBrush = on ? T.AccentBrush : T.Border2Brush,
            BorderThickness = new Thickness(T.Density.Hairline),
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (on)
        {
            box.Child = new Path
            {
                Data = Geometry.Parse("M8,18 L15,25 L28,11"),
                Stroke = T.AccentForegroundBrush,
                StrokeThickness = 4,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
            };
        }

        var text = Text(caption, ControlText, T.TextBrush);
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(box, Dock.Left);
        text.Margin = new Thickness(14, 0, 0, 0);
        row.Children.Add(box);
        row.Children.Add(text);

        return new Border
        {
            Tag = target,
            Height = ControlHeight,
            CornerRadius = new CornerRadius(12),
            Background = T.Surface2Brush,
            BorderBrush = T.Border2Brush,
            BorderThickness = new Thickness(T.Density.Hairline),
            Padding = new Thickness(12, 0),
            Child = row,
        };
    }

    /// <summary>
    /// A slider between a minus and a plus: the track fills up to the value and a knob sits on
    /// it. The whole track's height can be pressed and dragged, not only the thin line.
    /// </summary>
    private static Control Slider(NotifyOverlaySettings notify, DashboardSlider which)
    {
        var range = DashboardEdits.RangeOf(which);
        var fraction = range.FractionOf(DashboardEdits.ValueOf(notify, which));

        const double knobSide = KnobSide;

        // Three columns put the knob where the value is without knowing the track's width: what
        // is left of the knob takes the value's share of the room, what is right of it the rest.
        // The tiny floor keeps a zero share a share at all.
        var track = new Grid
        {
            Tag = new DashboardTarget.Track(which),
            Background = Brushes.Transparent,
            Height = ControlHeight,
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(Math.Max(fraction, 0.0001), GridUnitType.Star)),
                new ColumnDefinition(new GridLength(knobSide)),
                new ColumnDefinition(new GridLength(Math.Max(1 - fraction, 0.0001), GridUnitType.Star)),
            },
        };

        var line = new Border
        {
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Background = T.Surface3Brush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumnSpan(line, 3);

        var filled = new Border
        {
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Background = T.AccentBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumnSpan(filled, 2);

        var knob = new Ellipse
        {
            Width = knobSide,
            Height = knobSide,
            Fill = T.TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(knob, 1);

        track.Children.Add(line);
        track.Children.Add(filled);
        track.Children.Add(knob);

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{ControlHeight},*,{ControlHeight}"),
            ColumnSpacing = 16,
        };

        var minus = Button(new DashboardTarget.Step(which, -1), "−", chosen: false, textSize: 36);
        var plus = Button(new DashboardTarget.Step(which, 1), "+", chosen: false, textSize: 36);
        Grid.SetColumn(minus, 0);
        Grid.SetColumn(track, 1);
        Grid.SetColumn(plus, 2);
        row.Children.Add(minus);
        row.Children.Add(track);
        row.Children.Add(plus);

        return row;
    }
}
