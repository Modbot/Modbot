using Avalonia.Controls;
using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.Views;

/// <summary>The Dictate button at the top of the headset panel.</summary>
public sealed partial class OverlayView
{
    /// <summary>
    /// The Dictate button for a card: the picture, the words and the meter the button panel's Dictate row and
    /// the chats' microphone draw, as one target (<see cref="OverlayTarget.DictateButton"/>).
    /// </summary>
    private Control DictateButton(ButtonScreen card)
        => ChatView.MicButton(
            new ChatMicScreen(card.Dictate, card.Percent, card.Level, card.Notice),
            card.Frame,
            _look,
            new OverlayTarget.DictateButton());
}
