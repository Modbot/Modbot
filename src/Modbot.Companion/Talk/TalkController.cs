using Modbot.Companion.Presentation;

namespace Modbot.Companion.Talk;

/// <summary>VRChat's microphone, as far as Modbot can reach it.</summary>
public interface IVRChatMic
{
    /// <summary>Whether VRChat's microphone is muted, as VRChat last said, or null until it has said.</summary>
    bool? Muted { get; }

    /// <summary>Why VRChat cannot be heard from, in one sentence, or null.</summary>
    string? Problem { get; }

    /// <summary>
    /// Taps VRChat's voice button once (the key is pressed and let go), then waits a moment for VRChat
    /// to say what the microphone is now.
    /// </summary>
    Task<MicTap> TapAsync(CancellationToken cancellationToken);
}

/// <summary>What one tap came to.</summary>
/// <param name="Sent">The tap went out.</param>
/// <param name="Muted">What VRChat says the microphone is now, or null if it has never said.</param>
public readonly record struct MicTap(bool Sent, bool? Muted);

/// <summary>Presses a key on the keyboard, for Windows to deliver to whichever program wants it.</summary>
public interface IKeyPresser
{
    /// <summary>True if Windows took the press.</summary>
    bool Press(ShortcutKeys keys);

    /// <summary>
    /// Lets go of the modifier keys in <paramref name="modifiers"/> (<see cref="ShortcutKeys.Ctrl"/> and
    /// the others, added together), as if the hand had lifted them. Letting go of a key that is not
    /// down does nothing.
    /// </summary>
    void ReleaseModifiers(int modifiers);
}

/// <summary>How an action ended.</summary>
/// <param name="Ok">It did what was asked.</param>
/// <param name="Problem">One sentence saying what failed, or null.</param>
public sealed record TalkResult(bool Ok, string? Problem = null)
{
    public static TalkResult Done { get; } = new(true);

    public static TalkResult Failed(string problem) => new(false, problem);
}

/// <summary>Where things stand, for the buttons and the settings card.</summary>
/// <param name="On">The feature is switched on.</param>
/// <param name="VRChatMuted">VRChat's microphone as VRChat said, or null if not known yet.</param>
/// <param name="DiscordMuted">Discord muted, as remembered from Modbot's own presses.</param>
/// <param name="DiscordDeafened">Discord deafened, as remembered from Modbot's own presses.</param>
/// <param name="Problem">The last failure in one sentence, or null.</param>
/// <param name="VRChatMuteHeld">VRChat was muted by its own Mute button while on VRChat's side, so that is still the side.</param>
public sealed record TalkState(bool On, bool? VRChatMuted, bool DiscordMuted, bool DiscordDeafened, string? Problem, bool VRChatMuteHeld = false)
{
    public static TalkState None { get; } = new(false, null, false, false, null);

    /// <summary>
    /// Only VRChat's own microphone decides the side, since it is the only state Modbot really knows
    /// (Discord's is a memory of Modbot's own key presses): muted is Talk in Discord, unmuted or not
    /// heard from yet is Talk in VRChat. Muting VRChat with its own button while on VRChat's side
    /// leaves it on VRChat's side (<see cref="VRChatMuteHeld"/>).
    /// </summary>
    public TalkSide Side => VRChatMuted == true && !VRChatMuteHeld ? TalkSide.Discord : TalkSide.VRChat;
}

/// <summary>
/// Talk in Discord, Talk in VRChat, and the two Discord buttons.
/// </summary>
/// <remarks>
/// <para><strong>The side being closed goes first</strong> (design spec 2026-10-17, §1). Talk in
/// Discord mutes VRChat before it opens Discord, and does not open Discord at all if VRChat could
/// not be muted: a private conversation must not reach a public instance because half a swap
/// worked. Talk in VRChat closes Discord first too, but opens VRChat even if that failed, because
/// the cost of the two failures is not the same.</para>
/// <para><strong>VRChat is never trusted blind.</strong> A tap is checked against what VRChat says
/// the microphone is afterwards; a tap that landed on the wrong side is tried once more, and that
/// is all.</para>
/// <para>One action at a time: a second press while one is running waits for it.</para>
/// <para>This presses keys and sends OSC only through the two small interfaces above, so the
/// order and the failure rules can be tested without VRChat or Discord. What leaves the machine:
/// nothing.</para>
/// </remarks>
public sealed class TalkController
{
    public const string Off = "Talk in Discord or VRChat is off.";

    public const string NoMuteKey = "Record Discord's Toggle Mute key in Settings.";

    public const string NoDeafenKey = "Record Discord's Toggle Deafen key in Settings.";

    public const string KeyNotSent = "Windows would not send the key.";

    public const string DidntAnswer = "VRChat didn't answer. Turn on OSC in VRChat's Action Menu, under Osc.";

    public const string DidntChange = "VRChat's microphone didn't change. Turn on Toggle Voice in VRChat's settings.";

    public const string CouldNotSend = "Modbot could not reach VRChat.";

    /// <summary>The most taps one request for a mute state may use: the first, and one to correct it.</summary>
    private const int MostTaps = 2;

    private readonly IVRChatMic _vrchat;
    private readonly IKeyPresser _keys;
    private readonly Func<TalkSettings> _settings;
    private readonly TimeSpan _gap;
    private readonly SemaphoreSlim _one = new(1, 1);

    private DiscordState _discord = DiscordState.Start;
    private string? _problem;
    private bool _vrchatMuteHeld;

    /// <param name="vrchat">VRChat's microphone.</param>
    /// <param name="keys">Presses keys.</param>
    /// <param name="settings">The settings as they are now, asked for at each action.</param>
    /// <param name="gap">How long to leave after each key press, so Discord takes each one.</param>
    public TalkController(IVRChatMic vrchat, IKeyPresser keys, Func<TalkSettings> settings, TimeSpan gap)
    {
        _vrchat = vrchat ?? throw new ArgumentNullException(nameof(vrchat));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _gap = gap;
    }

    /// <summary>Raised after an action ends, whatever came of it.</summary>
    public event Action? Changed;

    /// <summary>Where things stand now.</summary>
    public TalkState State => new(
        _settings().On,
        _vrchat.Muted,
        _discord.Muted,
        _discord.Deafened,
        _problem ?? _vrchat.Problem,
        _vrchatMuteHeld);

    /// <summary>VRChat muted, then Discord opened.</summary>
    public Task<TalkResult> TalkInDiscordAsync(CancellationToken cancellationToken = default)
        => RunAsync(() => TalkInDiscordNowAsync(cancellationToken));

    /// <summary>Discord closed, then VRChat opened.</summary>
    public Task<TalkResult> TalkInVRChatAsync(CancellationToken cancellationToken = default)
        => RunAsync(() => TalkInVRChatNowAsync(cancellationToken));

    /// <summary>
    /// The swap button: from Talk in Discord to Talk in VRChat, and from anywhere else to Talk in
    /// Discord. Which side it is on is read when this action's turn comes, not when it was asked
    /// for, so two quick presses go there and back.
    /// </summary>
    public Task<TalkResult> SwapAsync(CancellationToken cancellationToken = default)
        => RunAsync(side => side == TalkSide.Discord
            ? TalkInVRChatNowAsync(cancellationToken)
            : TalkInDiscordNowAsync(cancellationToken));

    /// <summary>One of the three buttons, as the VR panel and the hotkeys press them.</summary>
    public Task<TalkResult> PressButtonAsync(TalkButton button, CancellationToken cancellationToken = default) => button switch
    {
        TalkButton.Swap => SwapAsync(cancellationToken),
        TalkButton.DiscordMute => ToggleDiscordMuteAsync(cancellationToken),
        TalkButton.DiscordDeafen => ToggleDiscordDeafenAsync(cancellationToken),
        TalkButton.VRChatMute => ToggleVRChatMuteAsync(cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(button)),
    };

    /// <summary>
    /// A button pressed by its hotkey. Windows says the hotkey fired while the keys it was made of
    /// are still down, so a Discord key pressed now (<c>F13</c>) would reach Discord as, say,
    /// <c>Ctrl+F13</c>, and miss Discord's bind or fire the hotkey again. The hotkey's own modifiers are
    /// let go first, once, as the hotkey fires, and the button then runs as it does from the panel.
    /// </summary>
    /// <param name="button">The button the hotkey is for.</param>
    /// <param name="heldModifiers">The modifier keys the hotkey is made of (<see cref="ShortcutKeys.Modifiers"/>).</param>
    /// <param name="cancellationToken">Cancels the wait for the press.</param>
    public Task<TalkResult> PressHotkeyAsync(TalkButton button, int heldModifiers, CancellationToken cancellationToken = default)
    {
        if (heldModifiers != 0 && _settings().On)
            _keys.ReleaseModifiers(heldModifiers);

        return PressButtonAsync(button, cancellationToken);
    }

    /// <summary>One press of Discord's Toggle Mute key.</summary>
    public Task<TalkResult> ToggleDiscordMuteAsync(CancellationToken cancellationToken = default)
        => RunAsync(() => PressAsync(deafen: false, cancellationToken));

    /// <summary>One press of Discord's Toggle Deafen key.</summary>
    public Task<TalkResult> ToggleDiscordDeafenAsync(CancellationToken cancellationToken = default)
        => RunAsync(() => PressAsync(deafen: true, cancellationToken));

    /// <summary>
    /// VRChat's microphone muted if it is open, open if it is muted. The side stays VRChat's while it is muted
    /// this way, so the button can say so; any other action ends that.
    /// </summary>
    public Task<TalkResult> ToggleVRChatMuteAsync(CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            var mute = _vrchat.Muted != true;
            var result = await SetVRChatMutedAsync(mute, cancellationToken).ConfigureAwait(false);
            _vrchatMuteHeld = result.Ok && mute;
            return result;
        });

    private Task<TalkResult> RunAsync(Func<Task<TalkResult>> action)
        => RunAsync(_ => action());

    /// <summary>
    /// Runs one action in its turn. The side is read here, before an action ends the "muted in VRChat" hold,
    /// so an action that depends on it sees the side the button showed.
    /// </summary>
    private async Task<TalkResult> RunAsync(Func<TalkSide, Task<TalkResult>> action)
    {
        await _one.WaitAsync().ConfigureAwait(false);
        try
        {
            var side = State.Side;
            _vrchatMuteHeld = false;
            var result = _settings().On ? await action(side).ConfigureAwait(false) : TalkResult.Failed(Off);
            _problem = result.Problem;
            return result;
        }
        finally
        {
            _one.Release();
            Changed?.Invoke();
        }
    }

    private async Task<TalkResult> TalkInDiscordNowAsync(CancellationToken cancellationToken)
    {
        var vrchat = await SetVRChatMutedAsync(true, cancellationToken).ConfigureAwait(false);
        return vrchat.Ok
            ? await SetDiscordMicrophoneAsync(open: true, cancellationToken).ConfigureAwait(false)
            : vrchat;
    }

    private async Task<TalkResult> TalkInVRChatNowAsync(CancellationToken cancellationToken)
    {
        var discord = await SetDiscordMicrophoneAsync(open: false, cancellationToken).ConfigureAwait(false);
        var vrchat = await SetVRChatMutedAsync(false, cancellationToken).ConfigureAwait(false);
        return vrchat.Ok ? discord : vrchat;
    }

    private async Task<TalkResult> SetVRChatMutedAsync(bool muted, CancellationToken cancellationToken)
    {
        var known = _vrchat.Muted;

        for (var taps = 0; ; taps++)
        {
            if (known == muted)
                return TalkResult.Done;

            if (taps == MostTaps)
                return TalkResult.Failed(DidntChange);

            var tap = await _vrchat.TapAsync(cancellationToken).ConfigureAwait(false);
            if (!tap.Sent)
                return TalkResult.Failed(_vrchat.Problem ?? CouldNotSend);

            // VRChat never said what the microphone is, so there is nothing to compare with, and a
            // second blind tap could undo the first.
            if (tap.Muted is null)
                return TalkResult.Failed(_vrchat.Problem ?? DidntAnswer);

            known = tap.Muted;
        }
    }

    private async Task<TalkResult> SetDiscordMicrophoneAsync(bool open, CancellationToken cancellationToken)
    {
        if (!open)
        {
            // Deafened is already shut, and was somebody's choice to hear nothing.
            return _discord.Deafened || _discord.Muted
                ? TalkResult.Done
                : await PressAsync(deafen: false, cancellationToken).ConfigureAwait(false);
        }

        if (_discord.Deafened)
        {
            var undeafened = await PressAsync(deafen: true, cancellationToken).ConfigureAwait(false);
            if (!undeafened.Ok)
                return undeafened;
        }

        return _discord.Muted
            ? await PressAsync(deafen: false, cancellationToken).ConfigureAwait(false)
            : TalkResult.Done;
    }

    private async Task<TalkResult> PressAsync(bool deafen, CancellationToken cancellationToken)
    {
        var settings = _settings();
        if (TalkKeys.Read(deafen ? settings.DiscordDeafenKey : settings.DiscordMuteKey) is not { } keys)
            return TalkResult.Failed(deafen ? NoDeafenKey : NoMuteKey);

        if (!_keys.Press(keys))
            return TalkResult.Failed(KeyNotSent);

        _discord = deafen ? _discord.AfterDeafenPress() : _discord.AfterMutePress();

        if (_gap > TimeSpan.Zero)
            await Task.Delay(_gap, cancellationToken).ConfigureAwait(false);

        return TalkResult.Done;
    }
}
