namespace Modbot.Companion.Talk;

/// <summary>
/// Talk in Discord / Talk in VRChat: whether it is on, and the two keys Modbot presses for Discord.
/// </summary>
/// <remarks>
/// <para><strong>Off is the default and stays the default.</strong> Nothing is advertised to VRChat,
/// nothing listens and no key is ever pressed until somebody turns this on themselves (design spec
/// 2026-10-17, §5).</para>
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
public sealed record TalkSettings(bool On = false, string? DiscordMuteKey = null, string? DiscordDeafenKey = null)
{
    /// <summary>Off, no keys.</summary>
    public static TalkSettings Default { get; } = new();

    /// <summary>
    /// The same settings with a key that cannot be pressed (a hand-edited file) taken out, so it
    /// reads as "not recorded" rather than as a key that silently does nothing.
    /// </summary>
    public TalkSettings Cleaned() => this with
    {
        DiscordMuteKey = TalkKeys.CanBePressed(DiscordMuteKey) ? DiscordMuteKey!.Trim().ToLowerInvariant() : null,
        DiscordDeafenKey = TalkKeys.CanBePressed(DiscordDeafenKey) ? DiscordDeafenKey!.Trim().ToLowerInvariant() : null,
    };
}
