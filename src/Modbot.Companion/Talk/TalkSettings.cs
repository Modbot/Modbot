namespace Modbot.Companion.Talk;

/// <summary>The five keys a person records for Talk in Discord or VRChat.</summary>
public enum TalkKeySlot
{
    /// <summary>The key Modbot presses for Discord's Toggle Mute.</summary>
    DiscordMute,

    /// <summary>The key Modbot presses for Discord's Toggle Deafen.</summary>
    DiscordDeafen,

    /// <summary>The key that, pressed anywhere, moves the microphone between Discord and VRChat.</summary>
    SwapHotkey,

    /// <summary>The key that, pressed anywhere, presses Discord's Toggle Mute.</summary>
    MuteHotkey,

    /// <summary>The key that, pressed anywhere, presses Discord's Toggle Deafen.</summary>
    DeafenHotkey,
}

/// <summary>
/// Talk in Discord / Talk in VRChat: whether it is on, the two keys Modbot presses for Discord and the
/// three keys that run its buttons from the keyboard.
/// </summary>
/// <remarks>
/// <para><strong>Off is the default and stays the default.</strong> Nothing is advertised to VRChat,
/// nothing listens and no key is ever pressed until somebody turns this on themselves (design spec
/// 2026-10-17, §5). The three hotkeys start unset, and none is claimed from Windows until one is
/// recorded and this is on.</para>
/// <para>Kept in the <c>talk</c> object of <c>settings.json</c>
/// (<see cref="Presentation.CompanionSettings.SaveTalk"/>) and nowhere else. No server is told any of
/// it, and no server can change it.</para>
/// </remarks>
/// <param name="On">Whether the feature runs. False unless a person turned it on.</param>
/// <param name="DiscordMuteKey">
/// The key recorded for Discord's <em>Toggle Mute</em>, in <see cref="Presentation.KeyTokens"/> spelling
/// (<c>f13</c>, <c>mod+shift+m</c>), or null when none has been recorded.
/// </param>
/// <param name="DiscordDeafenKey">The same for Discord's <em>Toggle Deafen</em>.</param>
/// <param name="SwapHotkey">The key for the Talk in Discord / Talk in VRChat button, or null.</param>
/// <param name="MuteHotkey">The key for the Discord Mute button, or null.</param>
/// <param name="DeafenHotkey">The key for the Discord Deafen button, or null.</param>
public sealed record TalkSettings(
    bool On = false,
    string? DiscordMuteKey = null,
    string? DiscordDeafenKey = null,
    string? SwapHotkey = null,
    string? MuteHotkey = null,
    string? DeafenHotkey = null)
{
    /// <summary>Off, no keys.</summary>
    public static TalkSettings Default { get; } = new();

    /// <summary>The key recorded in one slot, or null.</summary>
    public string? Key(TalkKeySlot slot) => slot switch
    {
        TalkKeySlot.DiscordMute => DiscordMuteKey,
        TalkKeySlot.DiscordDeafen => DiscordDeafenKey,
        TalkKeySlot.SwapHotkey => SwapHotkey,
        TalkKeySlot.MuteHotkey => MuteHotkey,
        TalkKeySlot.DeafenHotkey => DeafenHotkey,
        _ => null,
    };

    /// <summary>
    /// The same settings with one slot set (null clears it). A key lives in one slot only: a key
    /// that clashes with it in another slot is taken out of that one. Without that, the key Modbot
    /// presses for Discord could also be one of Modbot's own hotkeys, and each press would start the
    /// next. Between a hotkey and a Discord key the clash is the <em>main key</em>, whatever is held
    /// with it: when <c>Ctrl+F13</c> fires, Ctrl is still down, so Modbot's own F13 would go out as
    /// another Ctrl+F13.
    /// </summary>
    public TalkSettings WithKey(TalkKeySlot slot, string? key)
    {
        var set = this;

        if (key is not null)
        {
            foreach (var other in Enum.GetValues<TalkKeySlot>())
            {
                if (other != slot && Clash(slot, key, other, Key(other)))
                    set = set.Put(other, null);
            }
        }

        return set.Put(slot, key);
    }

    /// <summary>
    /// The same settings with a key that cannot be pressed (a hand-edited file) taken out, so it
    /// reads as "not recorded" rather than as a key that silently does nothing. A key recorded in
    /// two slots stays in the first one only. A hotkey that Windows could not be asked for without
    /// taking a typing key from every program (<see cref="TalkKeys.ReadHotkey"/>) is taken out too.
    /// </summary>
    public TalkSettings Cleaned()
    {
        var cleaned = this;

        foreach (var slot in Enum.GetValues<TalkKeySlot>())
        {
            var key = cleaned.Key(slot);
            if (!(IsHotkey(slot) ? TalkKeys.CanBeHotkey(key) : TalkKeys.CanBePressed(key)))
            {
                cleaned = cleaned.Put(slot, null);
                continue;
            }

            key = key!.Trim().ToLowerInvariant();
            cleaned = cleaned.Put(slot, key);

            foreach (var later in Enum.GetValues<TalkKeySlot>().Where(s => s > slot))
            {
                if (Clash(slot, key, later, cleaned.Key(later)))
                    cleaned = cleaned.Put(later, null);
            }
        }

        return cleaned;
    }

    /// <summary>Whether a slot holds a key Modbot claims from Windows, rather than one it presses.</summary>
    public static bool IsHotkey(TalkKeySlot slot)
        => slot is TalkKeySlot.SwapHotkey or TalkKeySlot.MuteHotkey or TalkKeySlot.DeafenHotkey;

    /// <summary>
    /// Whether two recorded keys cannot both stay. Two Discord keys or two hotkeys clash when they
    /// are the very same keys, however spelled; a hotkey and a Discord key clash when their main
    /// key is the same, because the hotkey's modifiers are still held when Modbot presses the other.
    /// </summary>
    private static bool Clash(TalkKeySlot slotA, string? a, TalkKeySlot slotB, string? b)
    {
        if (TalkKeys.Read(a) is not { } first || TalkKeys.Read(b) is not { } second)
            return false;

        return IsHotkey(slotA) != IsHotkey(slotB) ? first.Key == second.Key : first == second;
    }

    private TalkSettings Put(TalkKeySlot slot, string? key) => slot switch
    {
        TalkKeySlot.DiscordMute => this with { DiscordMuteKey = key },
        TalkKeySlot.DiscordDeafen => this with { DiscordDeafenKey = key },
        TalkKeySlot.SwapHotkey => this with { SwapHotkey = key },
        TalkKeySlot.MuteHotkey => this with { MuteHotkey = key },
        TalkKeySlot.DeafenHotkey => this with { DeafenHotkey = key },
        _ => this,
    };
}
