using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.Talk;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.Views;

/// <summary>
/// The row of Talk buttons: Talk in Discord or VRChat, Discord Mute and Discord Deafen.
/// </summary>
public sealed partial class OverlayView
{
    /// <summary>
    /// The three buttons side by side, each saying where things stand now and coloured for it: the
    /// swap button in the accent colour on a side, Discord Mute and Discord Deafen in the warning
    /// colour while on. A failure, if there is one, is the one line under them.
    /// </summary>
    /// <remarks>
    /// Inside a headset there is no settings screen and no notification, so the row is the only place
    /// a failed press can say what went wrong. It is drawn only while Talk in Discord or VRChat is on.
    /// </remarks>
    private Control TalkBar(TalkRow talk)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            ColumnSpacing = 8,
        };

        row.Children.Add(TalkButtonFor(talk.SwapCaption, TalkButton.Swap, 0, active: talk.Side is not TalkSide.Neither, T.AccentForegroundBrush));
        row.Children.Add(TalkButtonFor(talk.MuteCaption, TalkButton.DiscordMute, 1, talk.DiscordMuted, T.DangerBrush));
        row.Children.Add(TalkButtonFor(talk.DeafenCaption, TalkButton.DiscordDeafen, 2, talk.DiscordDeafened, T.DangerBrush));

        if (talk.Problem is not { Length: > 0 } problem)
            return Backed(row, fill: true);

        var line = Text(problem, T.Density.TextTiny, T.DangerBrush);
        line.TextWrapping = TextWrapping.Wrap;
        line.MaxLines = 2;

        return Backed(new StackPanel { Spacing = 6, Children = { row, line } }, fill: true);
    }

    private Control TalkButtonFor(string caption, TalkButton button, int column, bool active, IBrush activeBrush)
    {
        var label = Text(caption, T.Density.TextBase, active ? activeBrush : T.TextBrush, FontWeight.SemiBold);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.TextAlignment = TextAlignment.Center;

        // Two lines at most, so a long caption in a narrow column wraps rather than being cut off.
        label.TextWrapping = TextWrapping.Wrap;
        label.MaxLines = 2;

        var border = new Border
        {
            Tag = new OverlayTarget.Talk(button),
            Background = T.Surface2Brush,
            BorderBrush = active ? activeBrush : T.Border2Brush,
            BorderThickness = new Thickness(T.Density.Hairline),
            CornerRadius = T.CornerRadius,
            MinHeight = T.Density.RowHeight,
            Padding = new Thickness(10, 8),
            Child = label,
        };

        Grid.SetColumn(border, column);
        return border;
    }
}
