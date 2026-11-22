using Avalonia.Media.Imaging;
using Modbot.Companion.App;
using Modbot.Companion.Presentation;
using Modbot.Overlay;
using Modbot.Overlay.Views;

namespace Modbot.UiBoard;

internal static partial class Surfaces
{
    /// <summary>How much of the overlay window's top is kept for the Dictate button tiles: the strip and a little under it.</summary>
    private const int StripPixels = 64;

    /// <summary>
    /// Adds what draws the desktop (VRChat's window) tiles of <see cref="Names"/>. These are the real
    /// windows, shown offscreen and photographed, so the top strip and the window's own frame are the
    /// ones the moderator gets. Nothing here starts a watcher: the windows are only built and shown.
    /// </summary>
    private static void AddDesktop(Dictionary<string, Func<RenderTargetBitmap>> draws)
    {
        // Each surface in both looks: the tile id ends "-desktop" for the Modbot look, "-desktop-vrchat" for VRChat's.
        void Both(string id, Func<OverlayLook, RenderTargetBitmap> draw)
        {
            draws[id + "-desktop"] = () => draw(Looks.Headset);
            draws[id + "-desktop-vrchat"] = () => draw(Looks.VRChat);
        }

        // AI Chat is on in the made-up state, so the strip carries its Dictate button.
        var ready = ButtonScreen.Shown with { Dictate = DictateState.Idle };

        void Overlay(string id, OverlayScreen screen)
            => Both(id, look => Drawing.OfWindow(OverlayWindow(screen, ready, look)));

        Overlay("overlay-instance", Samples.Roster());
        Overlay("overlay-auditlog", Samples.AuditLog());
        Overlay("overlay-choices", Samples.RosterWithChoices());

        // The Dictate states are drawn where the desktop has the button: on the strip, so only the strip is kept.
        void Dictate(string id, ButtonScreen card)
            => Both(id, look => Drawing.OfWindow(OverlayWindow(Samples.Roster(), card, look), StripPixels));

        Dictate("button-dictate-idle", ready);
        Dictate("button-dictate-starting", ready with { Dictate = DictateState.Starting });
        Dictate("button-dictate-listening", ready with { Dictate = DictateState.Listening, Level = 5 });
        Dictate("button-dictate-writing", ready with { Dictate = DictateState.Writing });
        Dictate("button-dictate-sending", ready with { Dictate = DictateState.Sending });
        Dictate("button-dictate-getting-ready", ready with { Dictate = DictateState.GettingReady, Percent = 42 });
        Dictate("button-dictate-notice", ready with { Dictate = DictateState.Idle, Notice = "Nothing heard" });

        Both("notifications", look => Drawing.OfWindow(NotifyWindow(Samples.Notification(), look)));

        void Chat(string id, ChatScreen screen)
            => Both(id, look => Drawing.OfWindow(ChatWindow(screen, look)));

        Chat("chat-empty", Samples.ChatEmpty());
        Chat("chat-conversation", Samples.ChatConversation());
        Chat("chat-error", Samples.ChatError());
    }

    /// <summary>The desktop overlay, at its own size, with a screen and the Dictate card, in the Modbot look or VRChat's.</summary>
    private static DesktopOverlayWindow OverlayWindow(OverlayScreen screen, ButtonScreen dictate, OverlayLook look)
    {
        var window = new DesktopOverlayWindow();

        // The look comes from VRChat's palette in the real client; the made-up palette stands in for it.
        window.UseColours(look.VRChat is null ? null : Looks.VRChatColours);
        window.Apply(new DesktopOverlaySettings(true, DesktopOverlaySettings.DefaultShortcut, DesktopOverlaySettings.MaximumOpacity));
        window.SetDictate(dictate);
        window.Update(screen);

        // Show, not Open: Open would start the watchers that look for VRChat's window and palette.
        window.Show();
        return window;
    }

    /// <summary>The desktop notification window with its cards up.</summary>
    private static DesktopNotifyWindow NotifyWindow(NotificationScreen cards, OverlayLook look)
    {
        var window = new DesktopNotifyWindow();

        window.Look = look.VRChat is null ? null : look;
        window.Apply(DesktopNotifySettings.Default);
        window.Update(cards);
        return window;
    }

    /// <summary>The desktop chat window, up with a conversation, at the size a first one opens at.</summary>
    private static DesktopChatWindow ChatWindow(ChatScreen screen, OverlayLook look)
    {
        var window = new DesktopChatWindow(null);

        window.UseLook(look);
        window.Show(screen);
        return window;
    }
}
