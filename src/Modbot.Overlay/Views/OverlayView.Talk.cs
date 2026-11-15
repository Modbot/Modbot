using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.Clips;
using Modbot.Companion.Talk;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.Views;

/// <summary>
/// Save a clip and the Talk buttons on one row: the three Talk buttons are small icons beside it.
/// </summary>
public sealed partial class OverlayView
{
    // Drawn on a 24-unit square, like the eye on the show and hide button.
    private const string SwapGlyph = "M4,8 L18,8 M14,4 L18,8 L14,12 M20,16 L6,16 M10,12 L6,16 L10,20";

    private const string MicrophoneGlyph =
        "M12,3 C10.3,3 9,4.3 9,6 L9,11 C9,12.7 10.3,14 12,14 C13.7,14 15,12.7 15,11 L15,6 C15,4.3 13.7,3 12,3 Z "
        + "M6,11 C6,14.3 8.7,17 12,17 C15.3,17 18,14.3 18,11 M12,17 L12,21 M9,21 L15,21";

    private const string HeadphonesGlyph =
        "M4,15 L4,12 C4,7.6 7.6,4 12,4 C16.4,4 20,7.6 20,12 L20,15 "
        + "M4,14 L8,14 L8,20 L6,20 C4.9,20 4,19.1 4,18 Z M20,14 L16,14 L16,20 L18,20 C19.1,20 20,19.1 20,18 Z";

    /// <summary>
    /// Save a clip with the Talk icons on its row, each saying where things stand now and coloured for
    /// it: the swap icon in the accent colour on a side, the Discord Mute and Discord Deafen icons struck
    /// through in the warning colour while on. A failure, if there is one, is the one line under them.
    /// </summary>
    /// <remarks>
    /// <para>Inside a headset there is no settings screen and no notification, so the row is the only place
    /// a failed press can say what went wrong. The icons are drawn only while Talk in Discord or VRChat is
    /// on; either part is left out when it is off.</para>
    /// <para>Each icon carries the words it used to say as its automation name.</para>
    /// </remarks>
    private Control ClipsAndTalk(ClipButton clips, TalkRow talk)
    {
        if (!talk.IsVisible)
            return SaveClipBar(clips);

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(clips.IsVisible ? "*,Auto,Auto,Auto" : "Auto,Auto,Auto"),
            ColumnSpacing = 8,
        };

        var column = 0;
        if (clips.IsVisible)
        {
            var save = SaveClipBar(clips, shared: true);
            Grid.SetColumn(save, column++);
            row.Children.Add(save);
        }

        var side = talk.Side switch { TalkSide.Discord => "D", TalkSide.VRChat => "V", _ => null };
        row.Children.Add(TalkIconFor(talk.SwapCaption, TalkButton.Swap, column++, SwapGlyph, side, active: talk.Side is not TalkSide.Neither, struck: false, T.AccentForegroundBrush));
        row.Children.Add(TalkIconFor(talk.MuteCaption, TalkButton.DiscordMute, column++, MicrophoneGlyph, null, talk.DiscordMuted, struck: talk.DiscordMuted, T.DangerBrush));
        row.Children.Add(TalkIconFor(talk.DeafenCaption, TalkButton.DiscordDeafen, column, HeadphonesGlyph, null, talk.DiscordDeafened, struck: talk.DiscordDeafened, T.DangerBrush));

        if (talk.Problem is not { Length: > 0 } problem)
            return Backed(row, fill: true);

        var line = Text(problem, T.Density.TextTiny, T.DangerBrush);
        line.TextWrapping = TextWrapping.Wrap;
        line.MaxLines = 2;

        return Backed(new StackPanel { Spacing = 6, Children = { row, line } }, fill: true);
    }

    /// <param name="caption">What the button used to say: its automation name.</param>
    /// <param name="side">A letter beside the swap icon for the side the microphone is on, or null.</param>
    /// <param name="struck">A line through the icon, for a Discord switch that is on.</param>
    private Control TalkIconFor(string caption, TalkButton button, int column, string glyph, string? side, bool active, bool struck, IBrush activeBrush)
    {
        var brush = active ? activeBrush : T.TextBrush;

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { TalkGlyph(glyph, struck, brush, T.Surface2Brush) },
        };

        if (side is not null)
        {
            var letter = Text(side, T.Density.TextSmall, brush, FontWeight.Bold);
            letter.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(letter);
        }

        var border = new Border
        {
            Tag = new OverlayTarget.Talk(button),
            Background = T.Surface2Brush,
            BorderBrush = active ? activeBrush : T.Border2Brush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = T.CornerRadius,

            // Small, but still a target a hand in a headset can land on without aiming.
            MinWidth = 56,
            MinHeight = T.Density.RowHeight,
            Padding = new Thickness(10, 6),
            Child = content,
        };

        AutomationProperties.SetName(border, caption);
        Grid.SetColumn(border, column);
        return border;
    }

    /// <summary>An icon on a 24-unit square, with a line through it when <paramref name="struck"/>, cut out of the button's own colour so it reads as a gap.</summary>
    private static Control TalkGlyph(string data, bool struck, IBrush brush, IBrush ground)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };

        canvas.Children.Add(GlyphPath(data, brush, 1.8));

        if (struck)
        {
            canvas.Children.Add(GlyphPath("M4.5,4.5 L19.5,19.5", ground, 4.6));
            canvas.Children.Add(GlyphPath("M4.5,4.5 L19.5,19.5", brush, 1.8));
        }

        return new Viewbox { Width = 26, Height = 26, Child = canvas };
    }

    private static Avalonia.Controls.Shapes.Path GlyphPath(string data, IBrush brush, double thickness) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = brush,
        StrokeThickness = thickness,
        StrokeJoin = PenLineJoin.Round,
        StrokeLineCap = PenLineCap.Round,
    };
}
