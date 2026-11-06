using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.Chat;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.Views;

/// <summary>
/// Builds the Avalonia visual tree for one <see cref="ChatScreen"/>: your message on the right, the
/// answer on the left, scrolled to where the hands left it.
/// </summary>
/// <remarks>
/// <para>Every word is placed as plain text, never parsed: the answer comes from a model and is not
/// to be trusted with markup.</para>
/// <para>The log is one target (<see cref="OverlayTarget.ChatLog"/>), so a drag or the right stick
/// that starts on it scrolls it.</para>
/// </remarks>
public static class ChatView
{
    /// <summary>How many typing-animation frames there are in a round; a dot is lit for two of them.</summary>
    public const int Frames = 8;

    /// <summary>How long one typing-animation frame lasts, in milliseconds.</summary>
    public const double FrameMilliseconds = 150;

    private const double DotSize = 9;

    /// <summary>The room kept clear on the far side of a bubble, so the two sides read apart.</summary>
    private const double SideRoom = 40;

    /// <param name="scrollOffset">How far the log is scrolled, in layout pixels.</param>
    /// <param name="frame">Which frame of the typing animation to draw, 0 to <see cref="Frames"/> - 1.</param>
    public static Control Build(ChatScreen screen, double scrollOffset, int frame, OverlayLook look)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(look);

        var t = look.Tokens;
        var rows = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 4) };

        foreach (var line in screen.Lines)
            rows.Children.Add(Row(line, frame, t));

        return new Border
        {
            Tag = new OverlayTarget.ChatLog(),
            Background = Brushes.Transparent,
            Child = new ScrollingList(rows, t.TextDimBrush, scrollOffset),
        };
    }

    /// <summary>How far the log in a laid-out tree from <see cref="Build"/> can be scrolled, in layout pixels; 0 when it has none.</summary>
    public static double MaxOffset(Control tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        return ScrollingList.In(tree)?.MaxOffset ?? 0;
    }

    /// <summary>Scrolls the log in a tree from <see cref="Build"/> to <paramref name="offset"/>, held inside its ends.</summary>
    /// <returns>Where the log now is, in layout pixels.</returns>
    public static double SetOffset(Control tree, double offset)
    {
        ArgumentNullException.ThrowIfNull(tree);

        if (ScrollingList.In(tree) is not { } list)
            return 0;

        list.Offset = Math.Clamp(offset, 0, list.MaxOffset);
        return list.Offset;
    }

    private static Control Row(ChatLine line, int frame, DesignTokens t)
    {
        if (line.Mine)
            return Bubble(Words(line.Text, t.TextBrush, t), t.AccentDimBrush, HorizontalAlignment.Right, t);

        return line.State switch
        {
            ChatLineState.Typing => Bubble(Dots(frame, t), t.Surface2Brush, HorizontalAlignment.Left, t),
            ChatLineState.Error => Words(line.Text, t.DangerBrush, t),
            _ => Bubble(Words(line.Text, t.TextBrush, t), t.Surface2Brush, HorizontalAlignment.Left, t),
        };
    }

    private static Border Bubble(Control content, IBrush fill, HorizontalAlignment side, DesignTokens t) => new()
    {
        Background = fill,
        CornerRadius = new CornerRadius(t.Density.Radius * 2),
        Padding = new Thickness(10, 7),
        HorizontalAlignment = side,
        Margin = side == HorizontalAlignment.Right ? new Thickness(SideRoom, 0, 0, 0) : new Thickness(0, 0, SideRoom, 0),
        Child = content,
    };

    private static TextBlock Words(string text, IBrush brush, DesignTokens t) => new()
    {
        Text = text,
        FontSize = t.Density.TextBase,
        FontFamily = new FontFamily(DesignTokens.FontFamily),
        Foreground = brush,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>Three dots, each lit in its turn.</summary>
    private static StackPanel Dots(int frame, DesignTokens t)
    {
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        var lit = Math.Abs(frame) % Frames / 2;

        for (var i = 0; i < 3; i++)
        {
            dots.Children.Add(new Ellipse
            {
                Width = DotSize,
                Height = DotSize,
                Fill = i == lit ? t.TextBrush : t.TextFaintBrush,
            });
        }

        return dots;
    }
}
