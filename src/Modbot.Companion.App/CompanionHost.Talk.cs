using Avalonia.Threading;
using Modbot.Companion.Presentation;
using Modbot.Companion.Talk;
using Serilog;

namespace Modbot.Companion.App;

/// <summary>
/// Owns Talk in Discord or VRChat: the OSC service, the controller and the three hotkeys, which
/// exist only while the switch in Settings is on.
/// </summary>
/// <remarks>
/// <para>Off means nothing is started: no port is opened, nothing is announced on the network and
/// no key is ever pressed. Switching it on starts the OSC service (see <see cref="VRChatOsc"/> for
/// what it listens for and what it announces); switching it off, or quitting, stops it and
/// withdraws the announcement.</para>
/// <para>What leaves the machine: nothing over the internet. The packets stay on this PC's loopback
/// address, and the announcement is the local-network notice <see cref="VRChatOsc"/> describes.</para>
/// </remarks>
internal sealed partial class CompanionHost
{
    /// <summary>How long to leave after each press of a Discord key, so Discord takes each one.</summary>
    private static readonly TimeSpan TalkKeyGap = TimeSpan.FromMilliseconds(150);

    /// <summary>The three buttons and the hotkey slot each one answers to.</summary>
    private static readonly (TalkKeySlot Slot, TalkButton Button)[] TalkHotkeySlots =
    [
        (TalkKeySlot.SwapHotkey, TalkButton.Swap),
        (TalkKeySlot.MuteHotkey, TalkButton.DiscordMute),
        (TalkKeySlot.DeafenHotkey, TalkButton.DiscordDeafen),
    ];

    private readonly Dictionary<TalkKeySlot, DesktopOverlayShortcut> _talkHotkeys = [];

    private VRChatOsc? _talkOsc;
    private TalkController? _talk;

    /// <summary>
    /// A key is being recorded on the Settings card. The three hotkeys are given back to Windows for
    /// that time, so pressing one of them to record it over itself records it instead of firing it.
    /// </summary>
    private bool _talkRecording;

    /// <summary>
    /// Starts Talk in Discord or VRChat if Settings has it on, and claims its hotkeys. Called once at
    /// start-up and when the switch goes on.
    /// </summary>
    private void StartTalk()
    {
        if (_state is null || !_state.Settings.Talk.On || _talk is not null)
            return;

        // Pressing a key on somebody's behalf is a Windows call, and nothing here is worth starting without it.
        if (!OperatingSystem.IsWindows())
        {
            _state.TalkProblem = "Only works on Windows.";
            return;
        }

        var osc = new VRChatOsc();
        osc.Start();

        var talk = new TalkController(
            osc,
            new WindowsKeyPresser(),
            () => _state?.Settings.Talk ?? TalkSettings.Default,
            TalkKeyGap);
        talk.Changed += () => Dispatcher.UIThread.Post(ShowTalkProblem);

        _talkOsc = osc;
        _talk = talk;

        Log.Information("Talk in Discord or VRChat is on: listening for VRChat's microphone on the loopback address only");
        ApplyTalkHotkeys();
        ShowTalkProblem();
    }

    /// <summary>Stops it, withdraws the announcement and gives the hotkeys back. Safe to call when it never started.</summary>
    private void StopTalk()
    {
        _talk = null;
        _talkOsc?.Dispose();
        _talkOsc = null;

        ApplyTalkHotkeys();

        if (_state is not null)
            _state.TalkProblem = null;

        if (_overlay is not null)
            _overlay.Talk = TalkRow.None;
    }

    /// <summary>The Settings card changed: saved, then acted on at once.</summary>
    private void SetTalk(TalkSettings talk)
    {
        ArgumentNullException.ThrowIfNull(talk);

        if (_state is null || _state.Settings.Talk == talk)
            return;

        _state.Settings = _state.Settings with { Talk = talk };

        if (!CompanionSettings.SaveTalk(_settingsPath, talk))
            Log.Warning("Could not save the talk settings to {Path}", _settingsPath);

        // Starting and stopping each match the hotkeys to the settings themselves; only a key
        // changed while it keeps running still needs it.
        var wasRunning = _talk is not null;

        if (talk.On)
            StartTalk();
        else
            StopTalk();

        if (wasRunning && _talk is not null)
            ApplyTalkHotkeys();

        // Off, or not on this system, leaves what StartTalk and StopTalk just said as it is.
        if (_talk is not null)
            ShowTalkProblem();
        else
            Render();
    }

    private void ShowTalkProblem()
    {
        if (_state is null)
            return;

        // Both when both are true: a hotkey taken by another program is not hidden by the controller's own problem.
        var problems = new[] { _talk?.State.Problem, TalkHotkeyProblem() }.OfType<string>().ToArray();
        _state.TalkProblem = problems.Length > 0 ? string.Join(Environment.NewLine, problems) : null;

        if (_overlay is not null)
            _overlay.Talk = TalkRowRule.For(_talk?.State);

        Render();
    }

    /// <summary>What the VR panel's row of Talk buttons shows right now; the overlay's turn asks for it.</summary>
    private TalkRow CurrentTalkRow() => TalkRowRule.For(_talk?.State);

    /// <summary>
    /// A button was pressed, on the VR panel or by its hotkey. The controller does the work and says
    /// what went wrong, if anything, through <see cref="TalkController.Changed"/>.
    /// </summary>
    private void PressTalk(TalkButton button)
    {
        if (_talk is { } talk)
            _ = PressTalkAsync(talk, button, heldModifiers: 0);
    }

    /// <summary>
    /// A button pressed by its hotkey. The hotkey's own modifiers are still down, so the controller
    /// lets go of them before it presses Discord's key (<see cref="TalkController.PressHotkeyAsync"/>).
    /// </summary>
    private void PressTalkHotkey(TalkButton button, TalkKeySlot slot)
    {
        if (_talk is not { } talk)
            return;

        var held = TalkKeys.ReadHotkey(_state?.Settings.Talk.Key(slot))?.Modifiers ?? 0;
        _ = PressTalkAsync(talk, button, held);
    }

    private static async Task PressTalkAsync(TalkController talk, TalkButton button, int heldModifiers)
    {
        try
        {
            var result = heldModifiers == 0
                ? await talk.PressButtonAsync(button).ConfigureAwait(false)
                : await talk.PressHotkeyAsync(button, heldModifiers).ConfigureAwait(false);
            if (!result.Ok)
                Log.Information("Talk button {Button} did not work: {Problem}", button, result.Problem);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Talk button {Button} failed", button);
        }
    }

    /// <summary>
    /// A key started or stopped being recorded on the Settings card. The hotkeys are given back for
    /// as long as it is, and claimed again after.
    /// </summary>
    private void SetTalkRecording(bool recording)
    {
        if (_talkRecording == recording)
            return;

        _talkRecording = recording;
        ApplyTalkHotkeys();

        // The re-claim may find a key another program took in the meantime.
        if (!recording && _talk is not null)
            ShowTalkProblem();
    }

    /// <summary>
    /// Makes the three hotkeys match the settings: each recorded one claimed while Talk in Discord or
    /// VRChat is running (and no key is being recorded), each other one given back.
    /// </summary>
    private void ApplyTalkHotkeys()
    {
        var talk = _state?.Settings.Talk ?? TalkSettings.Default;

        foreach (var (slot, button) in TalkHotkeySlots)
        {
            var wanted = _talk is not null && talk.On && !_talkRecording ? talk.Key(slot) : null;

            if (wanted is null)
            {
                if (_talkHotkeys.Remove(slot, out var unwanted))
                    unwanted.Dispose();

                continue;
            }

            if (!_talkHotkeys.TryGetValue(slot, out var shortcut))
            {
                var (pressed, claimed) = (button, slot);
                shortcut = new DesktopOverlayShortcut(() => PressTalkHotkey(pressed, claimed), key => TalkKeys.ReadHotkey(key), "pressing a Talk button");
                _talkHotkeys[slot] = shortcut;
            }

            if (shortcut.Registered != wanted)
                shortcut.Ask(wanted);
        }
    }

    /// <summary>Which hotkey another program already has, in one sentence, or null.</summary>
    private string? TalkHotkeyProblem()
    {
        var talk = _state?.Settings.Talk ?? TalkSettings.Default;

        foreach (var (slot, shortcut) in _talkHotkeys)
        {
            if (shortcut.State is ShortcutState.Taken && talk.Key(slot) is { } key)
                return $"Another program has {TalkKeys.Describe(key)}.";
        }

        return null;
    }
}
