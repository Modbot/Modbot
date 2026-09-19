namespace Modbot.Companion.Voice;

/// <summary>A sentence as sound: mono samples from -1 to 1, and how many of them make a second.</summary>
public sealed record VoiceClip(float[] Samples, int SampleRate)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / (double)Math.Max(1, SampleRate));

    /// <summary>The same clip at a gain of 0.0 to 1.0.</summary>
    public VoiceClip WithGain(float gain)
    {
        var scaled = new float[Samples.Length];
        for (var i = 0; i < Samples.Length; i++)
            scaled[i] = Math.Clamp(Samples[i] * gain, -1f, 1f);

        return new VoiceClip(scaled, SampleRate);
    }
}

/// <summary>Turns a sentence into sound. The text-to-speech engine sits behind this.</summary>
public interface IVoiceSynthesizer : IDisposable
{
    /// <param name="text">The sentence to say.</param>
    /// <param name="voiceName">
    /// Which of the downloaded voices says it, by the name the Settings screen shows
    /// (<see cref="VoiceModel.Voices"/>). A name the voice does not have falls back to the default
    /// one rather than failing.
    /// </param>
    VoiceClip Speak(string text, string? voiceName);
}

/// <summary>One place sound can come out of: a pair of speakers, a headset.</summary>
/// <param name="Id">The operating system's own id for it, which is what settings remember.</param>
/// <param name="Name">What the operating system calls it, which is what the settings page shows.</param>
public sealed record OutputDevice(string Id, string Name);

/// <summary>The output devices the machine has right now, and which one is the system default.</summary>
public interface IOutputDevices
{
    IReadOnlyList<OutputDevice> List();

    /// <summary>The system default output, or null when the machine has no output at all.</summary>
    OutputDevice? Default();
}

/// <summary>Plays one clip and finishes when it has been heard.</summary>
public interface IVoicePlayer
{
    /// <param name="device">The device to play on, or null for the system default — whatever that is at this moment.</param>
    Task PlayAsync(VoiceClip clip, OutputDevice? device, CancellationToken cancellationToken);
}

/// <summary>
/// Which device a line plays through, worked out from what the moderator chose and what is
/// plugged in right now.
/// </summary>
/// <remarks>
/// A chosen device that is not present — a headset not yet switched on, a dock left at home —
/// falls back to the system default rather than to silence, and the choice is kept so the device
/// is used again the moment it is back. Choosing nothing means the system default, followed
/// wherever the operating system moves it, the way a voice chat program does.
/// </remarks>
/// <param name="Device">The device to open, or null for the system default.</param>
/// <param name="FellBack">True when a chosen device was not there and the default is standing in.</param>
public sealed record OutputDeviceChoice(OutputDevice? Device, bool FellBack)
{
    public static OutputDeviceChoice Resolve(string? wantedId, IOutputDevices devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        if (string.IsNullOrWhiteSpace(wantedId))
            return new OutputDeviceChoice(null, false);

        var present = devices.List().FirstOrDefault(d => string.Equals(d.Id, wantedId, StringComparison.Ordinal));
        return present is null
            ? new OutputDeviceChoice(null, true)
            : new OutputDeviceChoice(present, false);
    }
}
