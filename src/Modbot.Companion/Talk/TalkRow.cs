namespace Modbot.Companion.Talk;

/// <summary>Which side the microphone is on.</summary>
public enum TalkSide
{
    /// <summary>Neither, or not known: both closed, both open, or VRChat has not said.</summary>
    Neither,

    /// <summary>Talk in Discord: VRChat muted and Discord's microphone open.</summary>
    Discord,

    /// <summary>Talk in VRChat: VRChat unmuted and Discord's microphone shut.</summary>
    VRChat,
}

/// <summary>The three buttons: on the VR panel and, as keys, on the keyboard.</summary>
public enum TalkButton
{
    /// <summary>Moves the microphone to the other side.</summary>
    Swap,

    /// <summary>Discord's Toggle Mute.</summary>
    DiscordMute,

    /// <summary>Discord's Toggle Deafen.</summary>
    DiscordDeafen,
}

/// <summary>What the row of Talk buttons on the VR panel shows right now.</summary>
/// <param name="IsVisible">The row is drawn. False while Talk in Discord or VRChat is off.</param>
/// <param name="Side">Which side the microphone is on.</param>
/// <param name="DiscordMuted">Discord is muted, as Modbot remembers it.</param>
/// <param name="DiscordDeafened">Discord is deafened, as Modbot remembers it.</param>
/// <param name="Problem">The last failure in one sentence, or null.</param>
public readonly record struct TalkRow(
    bool IsVisible,
    TalkSide Side,
    bool DiscordMuted,
    bool DiscordDeafened,
    string? Problem)
{
    /// <summary>Off, so there is no row.</summary>
    public static TalkRow None { get; } = default;

    /// <summary>The swap button's words: the side being talked in, or the side a press goes to.</summary>
    public string SwapCaption => Side switch
    {
        TalkSide.Discord => "Talking in Discord",
        TalkSide.VRChat => "Talking in VRChat",
        _ => "Talk in Discord",
    };

    /// <summary>The Discord Mute button's words.</summary>
    public string MuteCaption => DiscordMuted ? "Discord Muted" : "Discord Mute";

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
            ? new TalkRow(true, state.Side, state.DiscordMuted, state.DiscordDeafened, state.Problem)
            : TalkRow.None;
}
