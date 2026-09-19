namespace Modbot.Companion.Voice;

/// <summary>Where the voice is between "never downloaded" and "ready to speak".</summary>
public enum VoiceState
{
    /// <summary>The voice files are not on this PC. Turning the voice on fetches them.</summary>
    NotDownloaded,

    Downloading,

    /// <summary>
    /// An older voice is still on this PC and the one this client speaks with is being downloaded.
    /// </summary>
    /// <remarks>
    /// Worth its own state rather than folding into <see cref="Downloading"/>: a moderator whose
    /// voice worked yesterday and who is now watching a progress figure should be told which of
    /// the two is happening (voice engine design, 2026-09-18, §6.1).
    /// </remarks>
    Replacing,

    Ready,

    /// <summary>The download or the engine failed; <see cref="VoiceStatus.Problem"/> says how.</summary>
    Failed,
}

/// <summary>The voice, as the Settings page shows it.</summary>
/// <remarks>
/// Plain words and a list of device names, with none of the audio library's types: this library
/// does not know WASAPI or OpenAL, and the page needs only what a moderator can pick from.
/// </remarks>
/// <param name="Settings">What is chosen.</param>
/// <param name="Devices">Every output device the machine has right now.</param>
/// <param name="DefaultDevice">The system default output, or null when there is none.</param>
/// <param name="HasOutput">False when this PC has no way to play sound at all; the controls still show, the voice cannot speak.</param>
/// <param name="DownloadProgress">0.0 to 1.0 while downloading.</param>
/// <param name="Problem">One line saying what failed, or null.</param>
/// <param name="DownloadSize">
/// How many bytes the whole download is. The card shows it beside the progress, because a
/// percentage on its own tells a moderator on a slow connection nothing about whether to wait.
/// </param>
/// <param name="Voices">Every voice in the download the moderator can pick from.</param>
public sealed record VoiceStatus(
    VoiceSettings Settings,
    IReadOnlyList<OutputDevice> Devices,
    OutputDevice? DefaultDevice,
    bool HasOutput,
    VoiceState State,
    double DownloadProgress,
    string? Problem,
    long DownloadSize,
    IReadOnlyList<NamedVoice> Voices)
{
    /// <summary>Before the host has said anything.</summary>
    public static VoiceStatus None { get; } = new(
        VoiceSettings.Default,
        [],
        null,
        false,
        VoiceState.NotDownloaded,
        0,
        null,
        VoiceModel.Default.Size,
        VoiceModel.Default.Voices);

    /// <summary>True while the download is running, whether it is the first one or a replacement.</summary>
    public bool IsDownloading => State is VoiceState.Downloading or VoiceState.Replacing;
}
