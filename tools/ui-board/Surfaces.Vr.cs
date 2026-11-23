using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Modbot.Companion.Overlay;
using Modbot.Overlay;
using Modbot.Overlay.Views;

namespace Modbot.UiBoard;

internal static partial class Surfaces
{
    /// <summary>The chat panel is drawn at twice its layout size, so its words are as sharp as in the headset.</summary>
    private const int ChatDrawScale = 2;

    /// <summary>Adds what draws the VR (SteamVR) tiles of <see cref="Names"/>: each one at the headset panel's own texture size.</summary>
    private static void AddVr(Dictionary<string, Func<RenderTargetBitmap>> draws)
    {
        // Each surface in both looks: the tile id ends "-vr" for the Modbot look, "-vr-vrchat" for VRChat's.
        void Both(string id, Func<OverlayLook, RenderTargetBitmap> draw)
        {
            draws[id + "-vr"] = () => draw(Looks.Headset);
            draws[id + "-vr-vrchat"] = () => draw(Looks.VRChat);
        }

        // AI Chat is on in the made-up state, so the panel carries its Dictate button, as OverlayHost.SetDictate gives it.
        var ready = ButtonScreen.Shown with { Dictate = DictateState.Idle };

        void Overlay(string id, OverlayScreen screen)
            => Both(id, look => Drawing.Draw(HeadsetPanel(screen with { Dictate = ready }, look), OverlayHost.DefaultResolution, OverlayHost.DefaultResolution));

        Overlay("overlay-instance", Samples.Roster());
        Overlay("overlay-auditlog", Samples.AuditLog());
        Overlay("overlay-choices", Samples.RosterWithChoices());

        void Button(string id, ButtonScreen screen)
            => Both(id, look => Drawing.Draw(ButtonView.Build(screen, look), OverlayButton.PanelPixels, OverlayButton.PanelPixels));

        Button("button-hide", ButtonScreen.Shown);
        Button("button-show", ButtonScreen.Hidden);

        // The moving states are drawn part-way round.
        const int Frame = 2;
        Button("button-dictate-idle", ButtonScreen.Shown with { Dictate = DictateState.Idle });
        Button("button-dictate-starting", ButtonScreen.Shown with { Dictate = DictateState.Starting, Frame = Frame });
        Button("button-dictate-listening", ButtonScreen.Shown with { Dictate = DictateState.Listening, Level = 5 });
        Button("button-dictate-writing", ButtonScreen.Shown with { Dictate = DictateState.Writing, Frame = Frame });
        Button("button-dictate-sending", ButtonScreen.Shown with { Dictate = DictateState.Sending, Frame = Frame });
        Button("button-dictate-getting-ready", ButtonScreen.Shown with { Dictate = DictateState.GettingReady, Percent = 42, Frame = Frame });
        Button("button-dictate-notice", ButtonScreen.Shown with { Dictate = DictateState.Idle, Notice = "Nothing heard" });

        var cards = Samples.Notification();
        Both("notifications", look => Drawing.Draw(HeadsetNotification(cards, look), OverlayHost.DefaultNotificationResolution, OverlayHost.DefaultNotificationResolution));

        void Chat(string id, ChatScreen screen)
            => Both(id, look => Drawing.Draw(HeadsetChat(screen, look), ChatHost.DesignPixels * ChatDrawScale, ChatHost.DesignPixels * ChatDrawScale));

        Chat("chat-empty", Samples.ChatEmpty());
        Chat("chat-conversation", Samples.ChatConversation());
        Chat("chat-error", Samples.ChatError());
    }

    /// <summary>
    /// The headset panel: what OverlayHost hands the renderer at the shape the main panel starts in (tall, like
    /// the desktop window), with the bar drawn but not showing, shrunk into the texture's square.
    /// </summary>
    private static Control HeadsetPanel(OverlayScreen screen, OverlayLook look)
    {
        var shape = OverlayPlacement.MainPanelDefault;
        const double Texture = OverlayHost.DefaultResolution;
        var side = Texture * shape.Longest;

        var panel = PanelFrame.Main(
            OverlayView.BuildForHeadset(screen, null, look, Texture, shape.Wide),
            PanelBar.For(shape, false),
            null,
            Texture * shape.Tall,
            OverlayView.HeadsetPanelWidth(screen, look, Texture, shape.Wide),
            side);

        return new Viewbox
        {
            Stretch = Stretch.Fill,
            Child = new Border { Width = side, Height = side, Child = panel },
        };
    }

    /// <summary>The headset notification panel: what NotificationHost hands the renderer, with the bar drawn but not showing.</summary>
    private static Control HeadsetNotification(NotificationScreen cards, OverlayLook look)
        => PanelFrame.Notification(
            NotificationView.Build(cards, look),
            PanelBar.For(OverlayPlacement.Default, false),
            null,
            OverlayHost.DefaultNotificationResolution);

    /// <summary>The headset chat panel: what ChatHost hands the renderer at its design size and ordinary shape, drawn bigger.</summary>
    private static Control HeadsetChat(ChatScreen screen, OverlayLook look)
    {
        var shape = ChatHost.DefaultPlacement;
        const int Frame = 2;

        var panel = PanelFrame.Chat(
            ChatView.Build(screen, 0, Frame, look),
            PanelBar.For(shape, false),
            null,
            ChatHost.DesignPixels,
            shape.Wide,
            shape.Tall,
            ChatView.Foot(screen, Frame, look));

        return new Viewbox
        {
            Stretch = Stretch.Fill,
            Child = new Border { Width = ChatHost.DesignPixels, Height = ChatHost.DesignPixels, Child = panel },
        };
    }
}
