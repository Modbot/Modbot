namespace Modbot.Companion.Sounds;

/// <summary>
/// What the moderator has chosen about being told things on this PC: whether the bleep sounds, how
/// loud it is, and how many times the tray notice has already been shown.
/// </summary>
/// <remarks>
/// <para>On by default, where the voice is off by default. A PC that starts talking is a surprise
/// nobody asked for; a fifth of a second of tone when somebody flagged walks in is the shortest
/// possible form of the thing the client exists to say, and one switch turns it off.</para>
/// <para>Which device it plays through is not here: it is the voice's chosen output device, read at
/// the moment of playing, so moving the voice to a headset moves the bleep with it.</para>
/// <para>Kept in the <c>notifications</c> object of <c>settings.json</c>
/// (<see cref="Presentation.CompanionSettings"/>) and nowhere else. No server is told any of it.</para>
/// </remarks>
/// <param name="Bleep">Whether the short sound is played at all.</param>
/// <param name="Volume">0 to 100, its own, not the voice's.</param>
/// <param name="TrayNoticesShown">
/// How many times the moderator has been told that closing the window leaves Modbot running in the
/// tray. Counted rather than switched off by hand, so nobody has to find a control for it.
/// </param>
public sealed record NotificationSettings(
    bool Bleep = true,
    int Volume = NotificationSettings.DefaultVolume,
    int TrayNoticesShown = 0)
{
    public const int DefaultVolume = 70;

    public const int MaxVolume = 100;

    /// <summary>
    /// How many times the tray notice is shown before it stops for good.
    /// </summary>
    /// <remarks>
    /// Three: enough that somebody who closes the window on their first evening and comes back a
    /// week later is told again, few enough that it is never the thing they learn to dismiss.
    /// </remarks>
    public const int TrayNoticesToShow = 3;

    public static NotificationSettings Default { get; } = new();

    /// <summary>Keeps a volume from the file, or from a slider, inside 0 to 100.</summary>
    public static int ClampVolume(int volume) => Math.Clamp(volume, 0, MaxVolume);

    /// <summary>The volume as the gain applied to the sound: 0.0 to 1.0.</summary>
    public float Gain => ClampVolume(Volume) / (float)MaxVolume;

    /// <summary>Whether closing the window to the tray should say so this time.</summary>
    public bool ShowTrayNotice => TrayNoticesShown < TrayNoticesToShow;

    /// <summary>The same settings with one more tray notice counted.</summary>
    public NotificationSettings WithTrayNoticeShown()
        => this with { TrayNoticesShown = Math.Min(TrayNoticesShown + 1, TrayNoticesToShow) };
}
