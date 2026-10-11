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
        => Build(screen, tokens ?? DesignTokens.Vr, null);

    /// <summary>
    /// The pop-ups in an overlay look: the Modbot look as it has always been, or VRChat's colours and
    /// cards, so a pop-up reads as part of the same panel as the lists.
    /// </summary>
    /// <param name="look">The look in use, headset or desktop.</param>
    /// <param name="scale">
    /// How many times bigger than its layout the cards are drawn. The headset draws them as laid out;
    /// a monitor draws them smaller, as the desktop panel is drawn (<see cref="DesktopScale"/>).
    /// </param>
    public static Control Build(NotificationScreen screen, OverlayLook look, double scale = 1)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(look);

        var cards = Build(screen, look.Tokens, look.VRChat);
        return scale == 1 || screen.IsEmpty ? cards : new Zoom(scale, cards);
    }

    /// <summary>
    /// How big the pop-ups on a monitor are drawn in VRChat's look, against the size they are laid
    /// out at: about what the desktop panel is drawn at over a 1080p VRChat window.
    /// </summary>
    public const double DesktopScale = 0.78;

    private static Control Build(NotificationScreen screen, DesignTokens t, VRChatLook? v)
    {
        ArgumentNullException.ThrowIfNull(screen);

        // Nothing to say, nothing drawn. Not a faint outline, not an empty card.
        if (screen.IsEmpty)
            return new Border { Background = Brushes.Transparent };

        // Slim, because a panel in the corner of an eye has no room to spare: the cards run to the
        // panel's edge and sit close together, with only enough inside each for the text not to
        // touch its border. With the old margins a 256-pixel panel held two of its three cards.
        var stack = new StackPanel { Spacing = v is null ? 4 : 6 };
        foreach (var popUp in screen.PopUps)
            stack.Children.Add(v is null ? Card(popUp, t) : VRChatCard(popUp, t, v));

        return new Border
        {
            // Nothing behind the cards: each paints its own surface and the rest of the panel
            // lets the world through, so an empty corner is not a dark slab in the view.
            Background = Brushes.Transparent,

            // The cards' shadows fall a few pixels outside them; this is the room they fall into.
            Padding = v is null ? default : new Thickness(ShadowMargin, 2, ShadowMargin, ShadowMargin),
            Child = stack,
        };
    }

    private const double ShadowMargin = 6;

    private static StackPanel Lines(PopUp popUp, DesignTokens t)
    {
        var lines = new StackPanel { Spacing = 0 };

        lines.Children.Add(Text(popUp.Heading, t.Density.TextSmall, t.TextDimBrush, FontWeight.SemiBold));
        lines.Children.Add(Text(popUp.Body, t.Density.TextBase * 1.3, t.TextBrush, FontWeight.SemiBold));

        if (popUp.Detail is { Length: > 0 } detail)
            lines.Children.Add(Text(detail, t.Density.TextSmall, Edge(popUp.Tone, t)));

        // Who just arrived: their rank and 18+ mark, drawn as the roster row draws them.
        if (popUp.HasPersonInfo)
        {
            var marks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

            if (popUp.Rank is { } rank)
                marks.Children.Add(PersonMarks.RankLine(rank, t.Density.TextSmall, t));

            if (popUp.EighteenPlus)
                marks.Children.Add(PersonMarks.EighteenPlusChip(t));

            lines.Children.Add(marks);
        }

        return lines;
    }

    private static Control Card(PopUp popUp, DesignTokens t) => new Border
    {
        Background = t.SurfaceBrush,
        BorderBrush = Edge(popUp.Tone, t),

        // A thicker left edge rather than a full border: the eye finds it at a glance without
        // the card becoming a box inside a box.
        BorderThickness = new Thickness(5, t.Density.Hairline, t.Density.Hairline, t.Density.Hairline),
        CornerRadius = t.CornerRadius,
        Padding = new Thickness(8, 4),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Child = Lines(popUp, t),
    };

    /// <summary>
    /// A card as VRChat draws one: the button colour, the raised edge and a soft shadow, with the
    /// tone's colour as a strip down the left so a flagged arrival still reads before the words do.
    /// </summary>
    private static Control VRChatCard(PopUp popUp, DesignTokens t, VRChatLook v)
    {
        var strip = new Border { Width = 6, Background = Edge(popUp.Tone, t) };
        DockPanel.SetDock(strip, Dock.Left);

        var words = Lines(popUp, t);
        words.Margin = new Thickness(10, 6);

        return new Border
        {
            Background = v.Button,
            BorderBrush = v.RaisedEdge,
            BorderThickness = new Thickness(VRChatLook.EdgeWidth),
            CornerRadius = new CornerRadius(VRChatLook.CardRadius),
            BoxShadow = v.CardShadow,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new DockPanel { LastChildFill = true, Children = { strip, words } },
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
