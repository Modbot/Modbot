namespace Modbot.Companion.Clips;

/// <summary>
/// Whether the client keeps the last few minutes of the screen, how many minutes, where saved clips
/// go, and how much room they may take.
/// </summary>
/// <remarks>
/// <para><strong>Off is the default and stays the default.</strong> A client that started recording
/// on first run would be exactly the surprise the old "it never captures the screen" promise existed
/// to prevent (see the clips design spec, §2). Nothing is captured, and no recorder is built at all,
/// until somebody turns this on themselves.</para>
/// <para>Nothing here leaves the machine. These four values decide what the client does on this PC;
/// no server is told any of them, and no server can change them.</para>
/// </remarks>
/// <param name="On">
/// Whether to keep the last few minutes while VRChat is running. False unless a person turned it on.
/// </param>
/// <param name="Minutes">
/// How many minutes to keep, between <see cref="MinMinutes"/> and <see cref="MaxMinutes"/>. Always
/// read through <see cref="ClampMinutes"/>: a hand-edited file saying 90 must not become an hour and
/// a half of video on somebody's disk.
/// </param>
/// <param name="Folder">
/// Where saved clips are written, or null for the usual place — the machine's own Videos folder plus
/// <c>Modbot Clips</c> (<see cref="ClipsFolder"/>).
/// </param>
/// <param name="KeepGigabytes">
/// How much room saved clips may take before the oldest are deleted to make space. No control on the
/// settings screen; it is in <c>settings.json</c> for somebody with a small disk.
/// </param>
public sealed record ClipSettings(
    bool On = false,
    int Minutes = ClipSettings.DefaultMinutes,
    string? Folder = null,
    int KeepGigabytes = ClipSettings.DefaultKeepGigabytes)
{
    /// <summary>The shortest length on offer. Below this a clip rarely holds what happened.</summary>
    public const int MinMinutes = 2;

    /// <summary>The longest length on offer.</summary>
    public const int MaxMinutes = 5;

    public const int DefaultMinutes = 3;

    /// <summary>The smallest and largest the folder may be allowed to grow.</summary>
    public const int MinKeepGigabytes = 1;

    public const int MaxKeepGigabytes = 200;

    public const int DefaultKeepGigabytes = 5;

    /// <summary>Off, three minutes, the usual folder, five gigabytes.</summary>
    public static ClipSettings Default { get; } = new();

    public static int ClampMinutes(int minutes) => Math.Clamp(minutes, MinMinutes, MaxMinutes);

    public static int ClampKeepGigabytes(int gigabytes)
        => Math.Clamp(gigabytes, MinKeepGigabytes, MaxKeepGigabytes);

    /// <summary>The chosen length, clamped, as a span.</summary>
    public TimeSpan Length => TimeSpan.FromMinutes(ClampMinutes(Minutes));

    /// <summary>How much room saved clips may take, clamped, in bytes.</summary>
    public long KeepBytes => (long)ClampKeepGigabytes(KeepGigabytes) * 1024 * 1024 * 1024;

    /// <summary>The same settings with every number brought inside its range.</summary>
    public ClipSettings Clamped() => this with
    {
        Minutes = ClampMinutes(Minutes),
        Folder = string.IsNullOrWhiteSpace(Folder) ? null : Folder.Trim(),
        KeepGigabytes = ClampKeepGigabytes(KeepGigabytes),
    };
}
