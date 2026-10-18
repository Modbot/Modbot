using Avalonia.Threading;
using Modbot.Companion.Presentation;
using Modbot.Companion.Talk;
using Serilog;

namespace Modbot.Companion.App;

/// <summary>
/// Owns Talk in Discord or VRChat: the OSC service and the controller, which exist only while the
/// switch in Settings is on.
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

    private VRChatOsc? _talkOsc;
    private TalkController? _talk;

    /// <summary>Starts Talk in Discord or VRChat if Settings has it on. Called once at start-up.</summary>
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
        ShowTalkProblem();
    }

    /// <summary>Stops it and withdraws the announcement. Safe to call when it never started.</summary>
    private void StopTalk()
    {
        _talk = null;
        _talkOsc?.Dispose();
        _talkOsc = null;

        if (_state is not null)
            _state.TalkProblem = null;
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

        if (talk.On)
            StartTalk();
        else
            StopTalk();

        Render();
    }

    private void ShowTalkProblem()
    {
        if (_state is null)
            return;

        _state.TalkProblem = _talk?.State.Problem;
        Render();
    }
}
