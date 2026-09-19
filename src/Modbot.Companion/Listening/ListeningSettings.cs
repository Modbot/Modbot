namespace Modbot.Companion.Listening;

/// <summary>
/// Whether the client listens for a spoken phrase, so a moderator wearing a headset can save a
/// clip without reaching a keyboard.
/// </summary>
/// <remarks>
/// <para><strong>Off is the default and stays the default.</strong> A microphone that opened
/// itself the first time somebody installed an update would be the single worst surprise this
/// client could spring, and it is the one the whole "bounded and checkable" argument exists to
/// prevent. Nothing is opened, and no listener is built at all, until somebody turns this on
/// themselves.</para>
/// <para><strong>Nothing is recorded, kept or sent.</strong> When it is on, sound from the
/// microphone is examined for the phrase and thrown away as it goes. Nothing is written to disk,
/// nothing is held for longer than the fraction of a second it takes to look at it, and no server
/// is told that this exists or that it fired.</para>
/// <para>Kept in the <c>listenForPhrase</c> object of <c>settings.json</c>
/// (<see cref="Presentation.CompanionSettings"/>) and nowhere else. No server is told any of it,
/// and no server can change it.</para>
/// </remarks>
/// <param name="On">
/// Whether the microphone is opened at all while VRChat is running. False unless a person turned
/// it on.
/// </param>
public sealed record ListeningSettings(bool On = false)
{
    /// <summary>Off.</summary>
    public static ListeningSettings Default { get; } = new();
}
