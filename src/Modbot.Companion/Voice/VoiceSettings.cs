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
/// </remarks>
/// <param name="On">Whether anything is spoken.</param>
/// <param name="Joins">Say when somebody joins the instance the moderator is in.</param>
/// <param name="Leaves">Say when somebody leaves it.</param>
/// <param name="FlaggedJoins">Say when the paired server raises a flagged-join alert for this instance.</param>
/// <param name="Volume">0 to 100.</param>
/// <param name="OutputDeviceId">
/// The device the voice plays through, by the id the operating system gives it, or null for the
/// system default — which then follows the default wherever the operating system moves it.
/// </param>
public sealed record VoiceSettings(
    bool On = false,
    bool Joins = true,
    bool Leaves = true,
    bool FlaggedJoins = true,
    int Volume = VoiceSettings.DefaultVolume,
    string? OutputDeviceId = null)
{
    public const int DefaultVolume = 80;

    public const int MaxVolume = 100;

    public static VoiceSettings Default { get; } = new();

    /// <summary>Keeps a volume from the file, or from a slider, inside 0 to 100.</summary>
    public static int ClampVolume(int volume) => Math.Clamp(volume, 0, MaxVolume);

    /// <summary>The volume as the gain applied to the sound: 0.0 to 1.0.</summary>
    public float Gain => ClampVolume(Volume) / (float)MaxVolume;
}
