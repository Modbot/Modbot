using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.Feedback;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.Views;

/// <summary>
/// Builds the Avalonia visual tree for one <see cref="FeedbackScreen"/>: the kind chips, the title,
/// the words, and Cancel and Send.
/// </summary>
/// <remarks>
/// <para>Every word is placed as plain text, never parsed: the draft comes from speech and from a model.</para>
/// <para><strong>Long text is cut here, not in what is sent.</strong> The panel shows the first lines of
/// the title and the words, ending in an ellipsis; Send still sends all of it.</para>
/// </remarks>
public static class FeedbackView
{
    /// <summary>The most lines of the title the panel shows.</summary>
    public const int TitleLines = 2;

    /// <summary>The most lines of the words the panel shows.</summary>
    public const int DetailLines = 9;

    private static readonly (string Kind, string Label)[] Chips =
    [
        (FeedbackKinds.Bug, "Bug"),
        (FeedbackKinds.Idea, "Idea"),
        (FeedbackKinds.Change, "Change"),
    ];

    public static Control Build(FeedbackScreen screen, OverlayLook look)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(look);

        var t = look.Tokens;

        var chips = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), Margin = new Thickness(0, 0, 0, 8) };
        for (var i = 0; i < Chips.Length; i++)
        {
            var chip = Chip(Chips[i].Kind, Chips[i].Label, string.Equals(screen.Kind, Chips[i].Kind, StringComparison.Ordinal), t);
            Grid.SetColumn(chip, i);
            chips.Children.Add(chip);
        }

        var text = new StackPanel { Spacing = 6 };

        if (screen.Title.Length > 0)
            text.Children.Add(Words(screen.Title, t.TextBrush, t, FontWeight.SemiBold, TitleLines));

        text.Children.Add(Words(screen.Details, t.TextDimBrush, t, FontWeight.Normal, DetailLines));

        if (screen.Problem is { Length: > 0 } problem)
            text.Children.Add(Words(problem, t.DangerBrush, t, FontWeight.Normal, 3));

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 8, 0, 0) };
        var cancel = Button(new OverlayTarget.FeedbackCancel(), "Cancel", t.Surface2Brush, t.TextBrush, enabled: !screen.Sending, t);
        var send = Button(
            new OverlayTarget.FeedbackSend(),
            screen.Sending ? "Sending" : "Send",
            screen.CanSend ? t.AccentDimBrush : t.Surface2Brush,
            t.TextBrush,
            screen.CanSend,
            t);
        Grid.SetColumn(send, 1);
        buttons.Children.Add(cancel);
        buttons.Children.Add(send);

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var body = new Border { ClipToBounds = true, Child = text };
        Grid.SetRow(body, 1);
        Grid.SetRow(buttons, 2);
        layout.Children.Add(chips);
        layout.Children.Add(body);
        layout.Children.Add(buttons);

        return layout;
    }

    private static Border Chip(string kind, string label, bool chosen, DesignTokens t) => new()
    {
        Tag = new OverlayTarget.FeedbackKind(kind),
        Margin = new Thickness(3, 0),
        Padding = new Thickness(0, 8),
        CornerRadius = new CornerRadius(t.Density.Radius * 2),
        Background = chosen ? t.AccentDimBrush : t.Surface2Brush,
        BorderBrush = chosen ? t.AccentBrush : t.Border2Brush,
        BorderThickness = new Thickness(t.Density.Hairline),
        Child = Label(label, t.TextBrush, t, chosen ? FontWeight.SemiBold : FontWeight.Normal),
    };

    private static Border Button(OverlayTarget target, string label, IBrush fill, IBrush ink, bool enabled, DesignTokens t) => new()
    {
        Tag = target,
        Margin = new Thickness(3, 0),
        Padding = new Thickness(0, 10),
        CornerRadius = new CornerRadius(t.Density.Radius * 2),
        Background = fill,
        BorderBrush = t.Border2Brush,
        BorderThickness = new Thickness(t.Density.Hairline),
        Opacity = enabled ? 1 : 0.5,
        Child = Label(label, ink, t, FontWeight.SemiBold),
    };

    private static TextBlock Label(string text, IBrush brush, DesignTokens t, FontWeight weight) => new()
    {
        Text = text,
        FontSize = t.Density.TextBase,
        FontFamily = new FontFamily(DesignTokens.FontFamily),
        FontWeight = weight,
        Foreground = brush,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static TextBlock Words(string text, IBrush brush, DesignTokens t, FontWeight weight, int lines) => new()
    {
        Text = text,
        FontSize = t.Density.TextBase,
        FontFamily = new FontFamily(DesignTokens.FontFamily),
        FontWeight = weight,
        Foreground = brush,
        TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxLines = lines,
    };
}
