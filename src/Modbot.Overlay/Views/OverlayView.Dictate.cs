using Avalonia.Controls;
using Avalonia.VisualTree;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.Views;

/// <summary>The Dictate button at the top of the headset panel.</summary>
public sealed partial class OverlayView
{
    /// <summary>
    /// The Dictate button for a card: the picture, the words and the meter the button panel's Dictate row and
    /// the chats' microphone draw, as one target (<see cref="OverlayTarget.DictateButton"/>).
    /// </summary>
    private Control DictateButton(ButtonScreen card) => DictateButton(card, _look);

    private static Border DictateButton(ButtonScreen card, OverlayLook look)
        => ChatView.MicButton(
            new ChatMicScreen(card.Dictate, card.Percent, card.Level, card.Notice),
            card.Frame,
            look,
            new OverlayTarget.DictateButton());

    /// <summary>
    /// Draws the Dictate button again inside a panel that is already built, and touches nothing else: its
    /// picture turns a step while something moves, which must not cost the whole panel a rebuild.
    /// Returns false when the panel has no such button, so the caller draws the panel from the screen instead.
    /// </summary>
    public static bool RedrawDictate(Control tree, ButtonScreen card, OverlayLook look)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(look);

        var button = tree.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(b => b.Tag is OverlayTarget.DictateButton);

        if (button is null)
            return false;

        var fresh = DictateButton(card, look);
        var inside = fresh.Child;
        fresh.Child = null;
        button.Child = inside;
        return true;
    }
}
