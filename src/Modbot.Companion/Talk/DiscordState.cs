namespace Modbot.Companion.Talk;

/// <summary>
/// What Modbot believes about Discord's microphone: only what its own key presses did.
/// </summary>
/// <remarks>
/// <para>Discord does not say. This starts from not muted and not deafened, which is how Discord
/// starts, and moves only when Modbot presses one of the two keys it was given (design spec
/// 2026-10-17, §3.1). A mute made by hand in Discord is therefore not seen, and the next press of
/// Modbot's still flips Discord's real state, so the buttons keep working and one more press puts
/// the memory right again.</para>
/// <para>Pure: no key is pressed here, and nothing is read. What leaves the machine: nothing.</para>
/// </remarks>
/// <param name="Muted">The microphone is muted. Always true while deafened, as Discord shows it.</param>
/// <param name="Deafened">Discord is deafened: nothing is heard and nothing is sent.</param>
/// <param name="MutedBeforeDeafen">What <paramref name="Muted"/> was when Discord was last deafened, which Discord brings back when it is undeafened.</param>
public sealed record DiscordState(bool Muted = false, bool Deafened = false, bool MutedBeforeDeafen = false)
{
    /// <summary>How Discord starts: not muted, not deafened.</summary>
    public static DiscordState Start { get; } = new();

    /// <summary>Whether what is said would reach Discord.</summary>
    public bool MicrophoneOpen => !Muted && !Deafened;

    /// <summary>
    /// After Modbot presses Toggle Mute. While deafened, Discord's own mute button also undeafens
    /// and unmutes, so that is what is remembered (to be checked by the user: spec §3.1).
    /// </summary>
    public DiscordState AfterMutePress() => Deafened
        ? new DiscordState()
        : this with { Muted = !Muted };

    /// <summary>
    /// After Modbot presses Toggle Deafen. Going deaf mutes and keeps what was before; coming back
    /// restores it, as Discord does.
    /// </summary>
    public DiscordState AfterDeafenPress() => Deafened
        ? new DiscordState(MutedBeforeDeafen, false, false)
        : new DiscordState(true, true, Muted);
}
