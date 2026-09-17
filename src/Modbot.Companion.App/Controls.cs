using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.Presentation;
using Modbot.Overlay;

namespace Modbot.Companion.App;

/// <summary>
/// The prototype's component vocabulary, as Avalonia controls.
/// </summary>
/// <remarks>
/// <para>Cards, pills, notes and stat tiles, built from the shared design tokens so the client
/// reads as the same product as the web UI. The prototype in <c>explore/design/index.html</c> is
/// the reference; these are its classes, not new inventions.</para>
/// <para>Nothing here reads anything or sends anything. It is shape and colour.</para>
/// </remarks>
internal static class Ui
{
    /// <summary>The desktop token set: the dark palette at the dense scale.</summary>
    internal static DesignTokens T => DesignTokens.Desktop;

    internal static readonly FontFamily Sans = new(DesignTokens.FontFamily);

    internal static readonly FontFamily Mono = new(DesignTokens.MonoFontFamily);

    internal static TextBlock Text(
        string text,
        double? size = null,
        IBrush? brush = null,
        FontWeight weight = FontWeight.Normal,
        bool wrap = true,
        bool mono = false) => new()
        {
            Text = text,
            FontSize = size ?? T.Density.TextSmall,
            FontFamily = mono ? Mono : Sans,
            FontWeight = weight,
            Foreground = brush ?? T.TextBrush,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        };

    internal static TextBlock Dim(string text, double? size = null)
        => Text(text, size, T.TextDimBrush);

    internal static TextBlock Faint(string text)
        => Text(text, T.Density.TextTiny, T.TextFaintBrush);

    /// <summary>An uppercase label above a value. The prototype's <c>.stat .k</c>.</summary>
    internal static TextBlock Label(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = T.Density.TextTiny,
        FontFamily = Sans,
        FontWeight = FontWeight.SemiBold,
        Foreground = T.TextFaintBrush,
        LetterSpacing = 0.6,
    };

    internal static Border Card(Control child, string? title = null, Control? action = null, Control? picture = null)
    {
        var body = new StackPanel { Spacing = 0 };

        if (title is not null)
        {
            var head = new DockPanel { LastChildFill = false };
            var heading = Text(title, T.Density.TextBase, T.TextBrush, FontWeight.SemiBold, wrap: false);
            heading.VerticalAlignment = VerticalAlignment.Center;

            if (picture is not null)
            {
                picture.Margin = new Thickness(0, 0, 10, 0);
                DockPanel.SetDock(picture, Dock.Left);
                head.Children.Add(picture);
            }

            DockPanel.SetDock(heading, Dock.Left);
            head.Children.Add(heading);

            if (action is not null)
            {
                DockPanel.SetDock(action, Dock.Right);
                head.Children.Add(action);
            }

            body.Children.Add(new Border
            {
                Padding = new Thickness(16, 12),
                BorderBrush = T.BorderBrush,
                BorderThickness = new Thickness(0, 0, 0, T.Density.Hairline),
                Child = head,
            });
        }

        body.Children.Add(new Border { Padding = new Thickness(16, 14), Child = child });

        return new Border
        {
            Background = T.SurfaceBrush,
            BorderBrush = T.BorderBrush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Child = body,
        };
    }

    /// <summary>A picture in a rounded square, the way the web app shows a group's icon.</summary>
    internal static Control Picture(Avalonia.Media.Imaging.Bitmap bitmap, double size) => new Border
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 4),
        ClipToBounds = true,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill },
    };

    /// <summary>
    /// A status pill: a dot, then a word.
    /// </summary>
    /// <remarks>
    /// The dot is the point. Colour alone fails for colour-blind readers and stops registering
    /// once somebody has seen it a hundred times, so the word carries the meaning and the colour
    /// only speeds up finding it.
    /// </remarks>
    internal static Control Pill(string text, Color colour, Color background) => new Border
    {
        Background = DesignTokens.Brush(background),
        CornerRadius = new CornerRadius(999),
        Padding = new Thickness(8, 3),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new Ellipse
                {
                    Width = 5,
                    Height = 5,
                    Fill = DesignTokens.Brush(colour),
                    VerticalAlignment = VerticalAlignment.Center,
                },
                Text(text, T.Density.TextTiny, DesignTokens.Brush(colour), FontWeight.Medium, wrap: false),
            },
        },
    };

    /// <summary>A note: a panel with a coloured left edge. The prototype's <c>.note</c>.</summary>
    internal static Control Note(string message, Color edge) => new Border
    {
        Background = T.Surface2Brush,
        BorderBrush = DesignTokens.Brush(edge),
        BorderThickness = new Thickness(2, 0, 0, 0),
        CornerRadius = new CornerRadius(T.Density.Radius),
        Padding = new Thickness(12, 10),
        Child = Text(message, T.Density.TextSmall, T.TextDimBrush),
    };

    internal static Button Button(string caption, bool primary = false, bool danger = false)
    {
        var button = new Button
        {
            Content = Text(
                caption,
                T.Density.TextSmall,
                primary ? T.AccentForegroundBrush : danger ? T.DangerBrush : T.TextBrush,
                FontWeight.Medium,
                wrap: false),
            Height = T.Density.ControlHeight,
            Padding = new Thickness(12, 0),
            CornerRadius = new CornerRadius(T.Density.Radius),
            Background = primary ? T.AccentBrush : danger ? Brushes.Transparent : T.Surface2Brush,
            BorderBrush = primary ? T.AccentBrush : danger ? T.DangerBrush : T.Border2Brush,
            BorderThickness = new Thickness(T.Density.Hairline),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        return button;
    }

    internal static TextBox Input(string? watermark = null) => new()
    {
        Height = T.Density.ControlHeight,
        Padding = new Thickness(10, 0),
        FontSize = T.Density.TextSmall,
        FontFamily = Sans,
        Watermark = watermark,
        Background = T.BackgroundBrush,
        Foreground = T.TextBrush,
        BorderBrush = T.Border2Brush,
        BorderThickness = new Thickness(T.Density.Hairline),
        CornerRadius = new CornerRadius(T.Density.Radius),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    internal static Control Field(string label, Control input) => new StackPanel
    {
        Spacing = 4,
        Children = { Dim(label), input },
    };

    /// <summary>A hairline between two things, the web app's <c>--hairline</c> border.</summary>
    internal static Control Hairline() => new Border
    {
        Height = T.Density.Hairline,
        Background = T.BorderBrush,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    /// <summary>
    /// A key, drawn as a key. <paramref name="keys"/> is the registry's spelling: <c>mod+k</c>,
    /// <c>g e</c>, <c>?</c>. A chord is two caps with "then" between them.
    /// </summary>
    internal static Control Kbd(string keys)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var first = true;
        foreach (var combo in keys.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!first)
            {
                var then = Faint("then");
                then.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(then);
            }

            first = false;
            row.Children.Add(new Border
            {
                Background = T.Surface2Brush,
                BorderBrush = T.BorderBrush,
                BorderThickness = new Thickness(T.Density.Hairline),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1),
                MinWidth = 20,
                VerticalAlignment = VerticalAlignment.Center,
                Child = Text(KeyTokens.DescribeCombo(combo), T.Density.TextTiny, T.TextDimBrush, wrap: false),
            });
        }

        return row;
    }

    /// <summary>
    /// One row of a list the keyboard can move through: the palette, the filter picker. Lit when
    /// it is the one the cursor is on.
    /// </summary>
    internal static Button ListRow(Control content, bool active, Action press)
    {
        var button = new Button
        {
            Content = content,
            Height = T.Density.RowHeight,
            Padding = new Thickness(10, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = active ? T.Surface3Brush : Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(T.Density.Radius),
        };

        button.Click += (_, _) => press();
        return button;
    }

    /// <summary>
    /// A panel that opens over the window: the command palette, the shortcut sheet. Raised off
    /// the page with a shadow rather than a heavier border.
    /// </summary>
    internal static Border Sheet(Control child, double maxWidth) => new()
    {
        Background = T.SurfaceBrush,
        BorderBrush = T.Border2Brush,
        BorderThickness = new Thickness(T.Density.Hairline),
        CornerRadius = new CornerRadius(10),
        MaxWidth = maxWidth,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Top,
        BoxShadow = new BoxShadows(new BoxShadow
        {
            Blur = 32,
            OffsetY = 12,
            Color = Color.FromArgb(0x99, 0x00, 0x00, 0x00),
        }),
        Child = new Border { ClipToBounds = true, CornerRadius = new CornerRadius(10), Child = child },
    };

    /// <summary>A big number with a quiet label. The prototype's <c>.stat</c>.</summary>
    internal static Control Stat(string label, string value, IBrush? valueBrush = null) => new Border
    {
        Background = T.SurfaceBrush,
        BorderBrush = T.BorderBrush,
        BorderThickness = new Thickness(T.Density.Hairline),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(14, 12),
        Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Label(label),
                Text(value, 22, valueBrush ?? T.TextBrush, FontWeight.Medium, wrap: false, mono: true),
            },
        },
    };
}
