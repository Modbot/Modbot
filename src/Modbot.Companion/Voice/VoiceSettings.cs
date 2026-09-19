namespace Modbot.Companion.Voice;

/// <summary>
/// What the moderator has chosen about the voice: whether it speaks at all, which kinds of event
/// it speaks, how loud, and through which output device.
/// </summary>
/// <remarks>
/// <para>Off until somebody turns it on. A voice that starts talking on first run, on whatever
/// speakers happen to be on, is a surprise nobody asked for.</para>
/// <para>Kept in the <c>voice</c> object of <c>settings.json</c> (<see cref="Presentation.CompanionSettings"/>)
/// and nowhere else. No server is told any of it.</para>
/// <para>The downloaded voice holds ten voices in the one file, so which one speaks is a choice
/// here and not a second download (voice engine design, 2026-09-18, §5).</para>
/// <para><strong>Which kinds it speaks moved.</strong> Joins, leaves and flagged joins are now
/// three of the rows on the Notifications card, beside the pop-up and the sound, because a
/// moderator picking what interrupts them should pick it in one place. The three fields below stay
/// so an upgrade and an older copy of the client both read the same thing.</para>
/// </remarks>
/// <param name="On">Whether anything is spoken.</param>
/// <param name="Joins">
/// Say when somebody joins the instance the moderator is in. The Notifications card's Voice column
/// is what the voice actually reads now; this is kept in step with it and is what an upgrade reads
/// to seed that column, so a switch somebody turned off stays off (notification filters design
/// 2026-09-19 §4.2).
/// </param>
/// <param name="Leaves">Say when somebody leaves it. Kept in step the same way.</param>
/// <param name="FlaggedJoins">
/// Say when the paired server raises a flagged-join alert for this instance. Kept in step the same
/// way.
/// </param>
/// <param name="Volume">0 to 100.</param>
/// <param name="OutputDeviceId">
/// The device the voice plays through, by the id the operating system gives it, or null for the
/// system default — which then follows the default wherever the operating system moves it.
/// </param>
/// <param name="VoiceName">
/// Which of the downloaded voices speaks, by name (<see cref="VoiceModel.Voices"/>). A name the
/// downloaded voice does not have falls back to the default one rather than failing.
/// </param>
public sealed record VoiceSettings(
    bool On = false,
    bool Joins = true,
    bool Leaves = true,
    bool FlaggedJoins = true,
    int Volume = VoiceSettings.DefaultVolume,
    string? OutputDeviceId = null,
    string VoiceName = VoiceModel.DefaultName)
{
    public const int DefaultVolume = 80;

    public const int MaxVolume = 100;

    public static VoiceSettings Default { get; } = new();

    /// <summary>Keeps a volume from the file, or from a slider, inside 0 to 100.</summary>
    public static int ClampVolume(int volume) => Math.Clamp(volume, 0, MaxVolume);

    /// <summary>The volume as the gain applied to the sound: 0.0 to 1.0.</summary>
    public float Gain => ClampVolume(Volume) / (float)MaxVolume;
}
