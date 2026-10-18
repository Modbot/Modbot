using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.Presentation;
using Modbot.Companion.Talk;

namespace Modbot.Companion.App;

/// <summary>
/// The Settings page's Talk in Discord or VRChat card: whether it runs, the two keys Modbot
/// presses for Discord, and the three hotkeys that run its buttons.
/// </summary>
/// <remarks>
/// <para>Each key is recorded by pressing it, like the desktop overlay's shortcut, and stored
/// spelled the way the window's own keys are (<c>f13</c>, <c>mod+shift+m</c>). Unlike that
/// shortcut a key may have no modifier, because it is pressed rather than claimed, and a hotkey
/// is claimed by one key only. A key lives in one place: recording it again somewhere else takes it
/// out of the first.</para>
/// <para>What went wrong, if anything, is the one line under the controls. That is an error saying
/// what failed, which is all the text this card gets.</para>
/// </remarks>
public sealed partial class MainWindow
{
    // Built once, like the other settings controls: a switch rebuilt by the one-second render
    // loses the click on it.
    private readonly CheckBox _talkOn = new();

    /// <summary>One slot's two buttons and the caption inside the first, kept so setting the caption keeps the button's styling.</summary>
    private sealed record TalkKeyControls(Button Record, Button Clear, TextBlock Caption);

    private readonly Dictionary<TalkKeySlot, TalkKeyControls> _talkKeys = Enum.GetValues<TalkKeySlot>()
        .ToDictionary(slot => slot, _ => new TalkKeyControls(
            Ui.Button(""),
            Ui.Button("Clear"),
            Ui.Text("", Ui.T.Density.TextSmall, Ui.T.TextBrush, FontWeight.Medium, wrap: false)));

    private readonly TextBlock _talkLine = Ui.Text("", Ui.T.Density.TextTiny, Ui.T.DangerBrush);

    private bool _talkWired;

    /// <summary>Which key the next press is recorded for, or null when none is waiting.</summary>
    private TalkKeySlot? _capturingTalkKey;

    private void WireTalk()
    {
        if (_talkWired)
            return;

        _talkWired = true;

        _talkOn.Content = Ui.Text("On", Ui.T.Density.TextSmall, Ui.T.TextBrush);
        _talkOn.IsCheckedChanged += (_, _) => TalkChanged(s => s with { On = _talkOn.IsChecked == true });

        foreach (var (slot, controls) in _talkKeys)
        {
            var captured = slot;
            controls.Record.Content = controls.Caption;
            controls.Record.MinWidth = 140;
            controls.Record.Click += (_, _) => ToggleTalkCapture(captured);
            controls.Clear.Click += (_, _) => ClearTalkKey(captured);
        }
    }

    private void ToggleTalkCapture(TalkKeySlot target)
    {
        _capturingTalkKey = _capturingTalkKey == target ? null : target;
        RenderPage();
    }

    private void ClearTalkKey(TalkKeySlot target)
    {
        _capturingTalkKey = null;
        TalkChanged(s => s.WithKey(target, null));
        RenderPage();
    }

    /// <summary>What the card's controls say right now, handed to the application as one record.</summary>
    private void TalkChanged(Func<TalkSettings, TalkSettings> change)
    {
        if (Quiet)
            return;

        _actions.SetTalk(change(_snapshot.Talk));
    }

    /// <summary>
    /// One key press while one of the buttons is waiting for it. Escape gives up; anything
    /// that can be pressed is saved and the button stops waiting.
    /// </summary>
    private void CaptureTalkKey(KeyEventArgs e)
    {
        e.Handled = true;

        if (e.Key is Key.Escape)
        {
            _capturingTalkKey = null;
            RenderPage();
            return;
        }

        // A press that is only modifiers, or that nothing could press, leaves the button waiting:
        // the moderator is still holding keys down.
        if (TalkTokenFor(e) is not { } token || !TalkKeys.CanBePressed(token))
            return;

        if (_capturingTalkKey is not { } target)
            return;

        _capturingTalkKey = null;
        TalkChanged(s => s.WithKey(target, token));
        RenderPage();
    }

    /// <summary>
    /// One key event as a recorded key. Unlike <see cref="TokenFor"/> it names the function keys
    /// (F1 to F24) and the number pad, and it keeps Shift on a letter, because the key is pressed
    /// exactly as recorded.
    /// </summary>
    private static string? TalkTokenFor(KeyEventArgs e)
    {
        // The Windows key cannot be pressed on someone's behalf.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            return null;

        var mod = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        var named = e.Key switch
        {
            >= Key.F1 and <= Key.F24 => $"f{1 + (e.Key - Key.F1)}",
            >= Key.NumPad0 and <= Key.NumPad9 => $"num{e.Key - Key.NumPad0}",
            _ => null,
        };

        if (named is not null)
            return KeyTokens.Token(named, mod, alt, shift);

        // Everything else is read the window's way, which leaves Shift off a printable key.
        var token = TokenFor(e);
        if (token is null || !shift || token.Split('+').Contains("shift"))
            return token;

        var parts = token.Split('+').ToList();
        parts.Insert(parts.Count - 1, "shift");
        return string.Join('+', parts);
    }

    /// <summary>
    /// What the card says right now, put into its controls. Called from the Settings page's own
    /// refresh, which has already stopped the controls answering back.
    /// </summary>
    private void RefreshTalkControls()
    {
        WireTalk();

        var talk = _snapshot.Talk;

        _talkOn.IsChecked = talk.On;

        foreach (var (slot, controls) in _talkKeys)
        {
            var key = talk.Key(slot);
            controls.Caption.Text = KeyCaption(key, _capturingTalkKey == slot);
            controls.Record.IsEnabled = talk.On;
            controls.Clear.IsVisible = key is not null;
            controls.Clear.IsEnabled = talk.On;
        }

        _talkLine.Text = _snapshot.TalkProblem ?? "";
        _talkLine.IsVisible = _snapshot.TalkProblem is not null;
    }

    private static string KeyCaption(string? key, bool waiting)
        => waiting ? "Press the keys" : key is null ? "Record" : TalkKeys.Describe(key);

    private Control TalkKeyRow(string label, TalkKeySlot slot) => Ui.Field(label, new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children = { _talkKeys[slot].Record, _talkKeys[slot].Clear },
    });

    /// <summary>The card: the switch, the two Discord keys, the three hotkeys, and what failed if anything did.</summary>
    private Control TalkCard()
    {
        WireTalk();

        foreach (var control in new Control[] { _talkOn, _talkLine }.Concat(_talkKeys.Values.SelectMany(c => new Control[] { c.Record, c.Clear })))
            DetachFromParent(control);

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                _talkOn,
                TalkKeyRow("Discord Toggle Mute", TalkKeySlot.DiscordMute),
                TalkKeyRow("Discord Toggle Deafen", TalkKeySlot.DiscordDeafen),
                TalkKeyRow("Talk in Discord or VRChat hotkey", TalkKeySlot.SwapHotkey),
                TalkKeyRow("Discord Mute hotkey", TalkKeySlot.MuteHotkey),
                TalkKeyRow("Discord Deafen hotkey", TalkKeySlot.DeafenHotkey),
                _talkLine,
            },
        };
    }
}
