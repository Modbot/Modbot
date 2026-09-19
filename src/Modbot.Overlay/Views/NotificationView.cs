using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.Overlay;

namespace Modbot.Overlay.Views;

/// <summary>
/// Builds the Avalonia visual tree for one <see cref="NotificationScreen"/>.
/// </summary>
/// <remarks>
/// <para><strong>Read at a glance, from the corner of an eye.</strong> One card per pop-up, a
/// heading in small dim text and one large line under it, a coloured left edge so the eye can
/// tell a flagged arrival from a fault without reading either. No lists, no controls, nothing to
/// point at.</para>
/// <para><strong>Nothing here can be tapped.</strong> There are no targets in this tree at all,
/// which is what makes the notification panel a thing that tells you something rather than a
/// thing that swallows trigger presses (two overlay modes design §2.1).</para>
/// <para><strong>Display names are hostile input.</strong> They are arbitrary user-controlled
/// text, so they are placed as text — never parsed, never interpreted as markup — and each is
/// given one line, so a name built out of newlines cannot push the rest off the panel.</para>
/// </remarks>
public static class NotificationView
{
    /// <param name="tokens">
    /// The palette and the density to draw at. The headset's by default; the notification overlay
    /// on a monitor passes the desktop's, because VR text is sized for a panel a metre away and a
    /// card that size on a screen would cover a corner of the game.
    /// </param>
    public static Control Build(NotificationScreen screen, DesignTokens? tokens = null)
    {
        ArgumentNullException.ThrowIfNull(screen);

        var t = tokens ?? DesignTokens.Vr;

        // Nothing to say, nothing drawn. Not a faint outline, not an empty card.
        if (screen.IsEmpty)
            return new Border { Background = Brushes.Transparent };

        var stack = new StackPanel { Spacing = 10 };
        foreach (var popUp in screen.PopUps)
            stack.Children.Add(Card(popUp, t));

        return new Border
        {
            // Nothing behind the cards: each paints its own surface and the rest of the panel
            // lets the world through, so an empty corner is not a dark slab in the view.
            Background = Brushes.Transparent,
            Padding = new Thickness(16),
            Child = stack,
        };
    }

    private static Control Card(PopUp popUp, DesignTokens t)
    {
        var lines = new StackPanel { Spacing = 4 };

        lines.Children.Add(Text(popUp.Heading, t.Density.TextSmall, t.TextDimBrush, FontWeight.SemiBold));
        lines.Children.Add(Text(popUp.Body, t.Density.TextBase * 1.3, t.TextBrush, FontWeight.SemiBold));

        if (popUp.Detail is { Length: > 0 } detail)
            lines.Children.Add(Text(detail, t.Density.TextSmall, Edge(popUp.Tone, t)));

        return new Border
        {
            Background = t.SurfaceBrush,
            BorderBrush = Edge(popUp.Tone, t),

            // A thicker left edge rather than a full border: the eye finds it at a glance without
            // the card becoming a box inside a box.
            BorderThickness = new Thickness(6, t.Density.Hairline, t.Density.Hairline, t.Density.Hairline),
            CornerRadius = t.CornerRadius,
            Padding = new Thickness(16, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = lines,
        };
    }

    private static IBrush Edge(PopUpTone tone, DesignTokens t) => tone switch
    {
        PopUpTone.Flagged => t.DangerBrush,
        PopUpTone.Problem => t.WarnBrush,
        _ => t.AccentForegroundBrush,
    };

    private static TextBlock Text(string content, double size, IBrush brush, FontWeight weight = FontWeight.Normal) => new()
    {
        Text = content,
        FontSize = size,
        FontFamily = new FontFamily(DesignTokens.FontFamily),
        FontWeight = weight,
        Foreground = brush,

        // Names are arbitrary user-controlled text. One line each means a name made of newlines
        // cannot push the rest of the panel out of the headset's view.
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxLines = 1,
    };
}
