namespace Modbot.Companion.Talk;

/// <summary>Which side the microphone is on.</summary>
public enum TalkSide
{
    /// <summary>Talk in Discord: VRChat's microphone is muted.</summary>
    Discord,

    /// <summary>Talk in VRChat: VRChat's microphone is unmuted, or VRChat has not said yet.</summary>
    VRChat,
}

/// <summary>The buttons: on the VR panel and, for the first three, as keys on the keyboard.</summary>
public enum TalkButton
{
    /// <summary>Moves the microphone to the other side.</summary>
    Swap,

    /// <summary>Discord's Toggle Mute.</summary>
    DiscordMute,

    /// <summary>Discord's Toggle Deafen.</summary>
    DiscordDeafen,

    /// <summary>VRChat's own microphone, muted or open again. On the VR panel only, beside the VRChat logo.</summary>
    VRChatMute,
}

/// <summary>What the row of Talk buttons on the VR panel shows right now.</summary>
/// <param name="IsVisible">The row is drawn. False while Talk in Discord or VRChat is off.</param>
/// <param name="Side">Which side the microphone is on.</param>
/// <param name="DiscordMuted">Discord is muted, as Modbot remembers it.</param>
/// <param name="DiscordDeafened">Discord is deafened, as Modbot remembers it.</param>
/// <param name="Problem">The last failure in one sentence, or null.</param>
/// <param name="VRChatMuted">VRChat's microphone is muted, as VRChat last said.</param>
public readonly record struct TalkRow(
    bool IsVisible,
    TalkSide Side,
    bool DiscordMuted,
    bool DiscordDeafened,
    string? Problem,
    bool VRChatMuted = false)
{
    /// <summary>Off, so there is no row.</summary>
    public static TalkRow None { get; } = default;

    /// <summary>The swap button's words: the side being talked in.</summary>
    public string SwapCaption => Side == TalkSide.Discord ? "Talking in Discord" : "Talking in VRChat";

    /// <summary>The Discord Mute button's words.</summary>
    public string MuteCaption => DiscordMuted ? "Discord Muted" : "Discord Mute";

    /// <summary>The VRChat Mute button's words, shown in place of the Discord ones while the microphone is on VRChat's side.</summary>
    public string VRChatMuteCaption => VRChatMuted ? "VRChat Muted" : "VRChat Mute";

    /// <summary>The Discord Deafen button's words.</summary>
    public string DeafenCaption => DiscordDeafened ? "Discord Deafened" : "Discord Deafen";
}

/// <summary>Works out the row from where the controller stands.</summary>
/// <remarks>
/// A row only for a controller that exists and is on: a moderator who never switched Talk in Discord
/// or VRChat on finds nothing new in front of them in VR.
/// </remarks>
public static class TalkRowRule
{
    /// <summary>What the row shows, given the controller's state (null when there is no controller).</summary>
    public static TalkRow For(TalkState? state)
        => state is { On: true }
            ? new TalkRow(true, state.Side, state.DiscordMuted, state.DiscordDeafened, state.Problem, state.VRChatMuted == true)
            : TalkRow.None;
}
