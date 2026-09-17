namespace Modbot.Companion.Voice;

/// <summary>Where the voice is between "never downloaded" and "ready to speak".</summary>
public enum VoiceState
{
    /// <summary>The voice files are not on this PC. Turning the voice on fetches them.</summary>
    NotDownloaded,

    Downloading,

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
public sealed record VoiceStatus(
    VoiceSettings Settings,
    IReadOnlyList<OutputDevice> Devices,
    OutputDevice? DefaultDevice,
    bool HasOutput,
    VoiceState State,
    double DownloadProgress,
    string? Problem)
{
    /// <summary>Before the host has said anything.</summary>
    public static VoiceStatus None { get; } = new(VoiceSettings.Default, [], null, false, VoiceState.NotDownloaded, 0, null);
}
