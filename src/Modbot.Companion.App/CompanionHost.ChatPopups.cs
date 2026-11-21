using Avalonia.Threading;
using Modbot.Companion.Chat;
using Modbot.Companion.Dictation;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Views;
using Serilog;

namespace Modbot.Companion.App;

/// <summary>
/// The pop-up chats on screen: for each open <see cref="ChatPopup"/>, its panel in the headset and
/// its window on the monitor, made when Dictate finishes and taken down when that chat is closed.
/// </summary>
/// <remarks>
/// <para>Every call here runs on the window's thread; a chat closed from another thread has its
/// panels taken down there (<see cref="StartChat"/> hands it over). The input loop reads
/// <see cref="_chatPanelList"/>, a copy that is replaced, never changed, so a chat closed in the middle
/// of a turn does not upset the turn.</para>
/// <para>Only the first chat remembers where it was put (the saved spots); later ones open a step
/// off the last (<see cref="ChatCascade"/>) and are forgotten when closed.</para>
/// </remarks>
internal sealed partial class CompanionHost
{
    /// <summary>What one pop-up chat has on screen.</summary>
    private sealed class ChatPanels(ChatPopup popup)
    {
        public ChatPopup Popup { get; } = popup;

        /// <summary>The panel in the headset, when the headset panel is on.</summary>
        public ChatHost? Host { get; set; }

        /// <summary>The window on the monitor, when the desktop overlay is on.</summary>
        public DesktopChatWindow? Window { get; set; }

        public DateTimeOffset AttachTriedAt { get; set; } = DateTimeOffset.MinValue;

        /// <summary>Lets go of what the window listens to on the overlay.</summary>
        public Action? Unhook { get; set; }

        /// <summary>This chat's own text box and microphone, tied to this chat's conversation only.</summary>
        public ChatInput? Input { get; set; }

        /// <summary>Redraws the chat ten times a second while its microphone listens, so the level follows the voice.</summary>
        public DispatcherTimer? Level { get; set; }
    }

    private ChatPopups? _chatPopups;
    private readonly Dictionary<ChatPopup, ChatPanels> _chatPanels = [];

    /// <summary>The open chats' panels, newest first: the one on top is read first.</summary>
    private ChatPanels[] _chatPanelList = [];

    private void RebuildChatPanelList()
        => _chatPanelList = [.. _chatPanels.Values.OrderByDescending(panels => panels.Popup.Number)];

    /// <summary>Makes the panel and the window for a chat that just opened, and puts them in the lists.</summary>
    private ChatPanels MakeChatPanels(ChatPopup popup)
    {
        var number = popup.Number;
        var settings = _state!.Settings;

        // Listed first, so whatever is half made when something throws is taken down with the chat.
        var panels = new ChatPanels(popup);
        _chatPanels.Add(popup, panels);
        RebuildChatPanelList();

        // Its own microphone button on the shared microphone; the words it hears and the words typed
        // here go into this chat's conversation and no other.
        var controller = MakeSpeechController($"Chat {number}");
        var level = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        panels.Level = level;
        panels.Input = new ChatInput(controller, popup.Session);

        controller.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (controller.State is DictationState.Listening && _chatPanels.ContainsKey(popup))
                level.Start();

            ShowChat(popup);
        });

        level.Tick += (_, _) =>
        {
            if (controller.State is not DictationState.Listening)
                level.Stop();

            ShowChat(popup);
        };

        if (_overlayHost is not null)
        {
            var host = ChatHost.Create(number, placement: number == 1 ? settings.OverlayChatSpot : null);
            panels.Host = host;
            host.EditMode = settings.OverlayEditMode;
            host.PushStep = PushStep(settings.OverlayPushSpeed);
            host.Look = _headsetLook;

            if (number == 1)
                host.PlacementChanged += ChatPlacementChanged;

            // The X ends this conversation and no other.
            host.Closed += () => ClosePopup(popup);
            host.MicTapped += () => ToggleChatMic(panels);
        }

        if (_desktopOverlay is { } overlay)
        {
            var window = new DesktopChatWindow(number == 1 ? settings.DesktopChatSpot : null)
            {
                Beside = overlay,
                EditMode = settings.OverlayEditMode,
                Shift = number == 1 ? (0, 0) : ChatCascade.ForDesktop(number),
            };
            panels.Window = window;
            window.Apply(settings.DesktopOverlay);
            window.UseLook(overlay.Look);

            Action lookChanged = () => window.UseLook(overlay.Look);
            overlay.LookChanged += lookChanged;
            panels.Unhook = () => overlay.LookChanged -= lookChanged;

            window.SwitchPressed += SetDesktopOverlay;
            window.CloseTapped += () => ClosePopup(popup);
            window.MicTapped += () => ToggleChatMic(panels);
            window.TextSent += text => _ = CrashGuard.RunAsync("asking AI Chat", () => panels.Input!.SendAsync(text));

            if (number == 1)
                window.SpotChanged += DesktopChatSpotChanged;
        }

        // Attached now, not at the next look, so the panel is up when the words are. A failure ends the
        // chat when it has no window to fall back on.
        if (panels.Host is not null && _overlayHost?.Status.State is OverlayRuntimeState.Running)
            AttachChat(panels);

        return panels;
    }

    /// <summary>Closes this chat and ends its conversation, unless it was closed already.</summary>
    private void ClosePopup(ChatPopup popup)
    {
        if (_chatPopups is { } popups && popups.Find(popup.Number) == popup)
            popups.Close(popup.Number);
    }

    /// <summary>A chat was closed: its panel and window go, and nothing else does.</summary>
    private void ChatPopupClosed(ChatPopup popup)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ChatPopupClosed(popup));
            return;
        }

        if (!_chatPanels.Remove(popup, out var panels))
            return;

        RebuildChatPanelList();

        panels.Unhook?.Invoke();
        panels.Level?.Stop();
        panels.Input?.Dictation.Abandon();
        panels.Host?.Dispose();

        if (panels.Window is { } window)
        {
            window.Dismiss();
            window.Close();
        }
    }

    /// <summary>A tap on one chat's microphone, only while Dictate would be offered.</summary>
    private void ToggleChatMic(ChatPanels panels)
    {
        if (DictateAvailable)
            panels.Input?.Dictation.Toggle();
    }

    /// <summary>What a chat's panels draw: its conversation, and its own microphone button while chat is on.</summary>
    private ChatScreen ScreenFor(ChatPanels panels)
        => new(panels.Popup.Session.Lines, DictateAvailable && panels.Input is { } input ? ChatMicScreen.Of(input.Dictation) : null);

    /// <summary>
    /// Attaches a chat's panel once the headset panel is up, and again after a VR runtime that closed
    /// is back. Its own attach, because it is an overlay of its own; a failure puts only this chat's
    /// panel away.
    /// </summary>
    private void AttachChat(ChatPanels panels)
    {
        if (panels.Host is not { } host)
            return;

        panels.AttachTriedAt = _clock.UtcNow;

        try
        {
            host.Start();
        }
        catch (Exception ex) when (PanelRetries.IsPanelFailure(ex))
        {
            Log.Warning(ex, "A chat panel could not start ({Reason}); the panel carries on without it", PanelRetries.ShortReason(ex));

            panels.Host = null;
            host.Dispose();

            // A chat with nowhere to show is ended.
            if (panels.Window is null)
                ClosePopup(panels.Popup);
        }
    }

    /// <summary>
    /// Opens a new pop-up chat with a conversation of its own and puts the dictated words into it.
    /// Each dictation is a new chat.
    /// </summary>
    private void AskChat(string text)
    {
        if (_chatPopups is not { } popups || (_overlayHost is null && _desktopOverlay is null))
        {
            LogDictation("the chat panel is not up, so what was said was not sent", null);
            _dictation?.SendEnded("The chat panel is not up");
            return;
        }

        var popup = popups.Open();
        var session = popup.Session;

        // The session's thread is not the window's: what it draws is handed over.
        session.Changed += () => Dispatcher.UIThread.Post(() => ShowChat(popup));

        ChatPanels panels;

        try
        {
            panels = MakeChatPanels(popup);
        }
        catch (Exception ex)
        {
            LogDictation("the chat panel could not be made", ex);
            popups.Close(popup.Number);
            _dictation?.SendEnded("The chat panel is not up");
            return;
        }

        // The panel could not start and there is no window: the chat was ended already.
        if (popups.Find(popup.Number) != popup)
        {
            _dictation?.SendEnded("The chat panel is not up");
            return;
        }

        // Dictate's button says Sending until this first answer begins, or says why it could not. Only
        // this first message is Dictate's; what is asked in the chat afterwards is the chat's own.
        Action<string?>? firstAnswer = null;
        firstAnswer = problem =>
        {
            session.AnswerStarted -= firstAnswer;
            _dictation?.SendEnded(problem);
        };
        session.AnswerStarted += firstAnswer;

        // The words go onto the conversation at once (the first part of asking runs here), so the panel
        // opens already showing them and the waiting line under them.
        _ = CrashGuard.RunAsync("asking AI Chat", () => session.AskAsync(text));

        try
        {
            var screen = ScreenFor(panels);
            panels.Host?.Show(screen);
            panels.Window?.Show(screen);
        }
        catch (Exception ex)
        {
            LogDictation("the chat panel could not be shown", ex);
        }
    }

    /// <summary>Draws one chat's conversation on its panels while they are up.</summary>
    private void ShowChat(ChatPopup popup)
    {
        if (_chatPopups?.Find(popup.Number) != popup || !_chatPanels.TryGetValue(popup, out var panels))
            return;

        var screen = ScreenFor(panels);

        if (panels.Host is { Hidden: false } host)
            host.Update(screen);

        if (panels.Window is { Hidden: false } window)
            window.Update(screen);
    }

    /// <summary>
    /// One look at the controllers for every chat panel, the newest first so the one on top is read
    /// first. Answers the hand any of them is using, for the panels under them.
    /// </summary>
    private Hand? PollChatInput(TimeSpan now)
    {
        Hand? busy = null;

        foreach (var panels in _chatPanelList)
        {
            if (panels.Host is not { } host)
                continue;

            host.PollInput(now, busy ?? _notifyHost?.Busy);
            busy ??= host.Busy;
        }

        return busy;
    }

    /// <summary>Lets every chat panel be heard, and attaches the ones whose panel is up but they are not.</summary>
    private void PollChatHosts()
    {
        foreach (var panels in _chatPanelList)
        {
            if (panels.Host is not { } host)
                continue;

            host.Poll();

            if (_overlayHost?.Status.State is OverlayRuntimeState.Running
                && host.Status.State is not OverlayRuntimeState.Running
                && _clock.UtcNow - panels.AttachTriedAt >= OverlayAttachInterval)
            {
                AttachChat(panels);
            }
        }
    }

    private bool ChatHolding()
    {
        foreach (var panels in _chatPanelList)
        {
            if (panels.Host?.Holding is not null)
                return true;
        }

        return false;
    }

    private bool ChatBusyWith(Hand hand)
    {
        foreach (var panels in _chatPanelList)
        {
            if (panels.Host?.Busy == hand)
                return true;
        }

        return false;
    }

    private void SetChatPushStep(float step)
    {
        foreach (var panels in _chatPanelList)
        {
            if (panels.Host is { } host)
                host.PushStep = step;
        }
    }

    private void SetChatLook(OverlayLook look)
    {
        foreach (var panels in _chatPanelList)
        {
            if (panels.Host is { } host)
                host.Look = look;
        }
    }

    /// <summary>The Edit mode switch, for every chat's panel and window.</summary>
    private void SetChatEditMode(bool on)
    {
        foreach (var panels in _chatPanelList)
        {
            if (panels.Host is { } host)
                host.EditMode = on;

            if (panels.Window is { } window)
                window.EditMode = on;
        }
    }

    private void ApplyToChatWindows(DesktopOverlaySettings settings)
    {
        foreach (var panels in _chatPanelList)
            panels.Window?.Apply(settings);
    }

    /// <summary>The desktop overlay is going: every chat window closes, and a chat left with no panel ends.</summary>
    private void CloseChatWindows()
    {
        foreach (var panels in _chatPanelList)
        {
            if (panels.Window is { } window)
            {
                panels.Window = null;
                panels.Unhook?.Invoke();
                panels.Unhook = null;
                window.Dismiss();
                window.Close();
            }

            if (panels.Host is null)
                ClosePopup(panels.Popup);
        }
    }

    /// <summary>The companion is going: every chat panel lets go of its overlay.</summary>
    private void DisposeChatHosts()
    {
        foreach (var panels in _chatPanelList)
            panels.Host?.Dispose();
    }
}
