namespace Modbot.Companion.Listening;

/// <summary>One microphone this PC has.</summary>
/// <param name="Id">The operating system's own id for it, which is what settings remember.</param>
/// <param name="Name">What the operating system calls it, which is what the settings page shows.</param>
public sealed record Microphone(string Id, string Name);

/// <summary>
/// Which microphone the phrase listener opens, worked out from what the moderator chose and what
/// is plugged in right now.
/// </summary>
/// <remarks>
/// <para><strong>One list, not two saved slots.</strong> The ask was for a headset microphone and
/// a desk microphone to both be easy to use. Two saved choices with a switch between them would be
/// a second control doing what the first already does, and it would have to be kept in step with a
/// list of devices that changes whenever a headset is switched on. One list of what this PC has,
/// with the Windows default first, covers both: a moderator in a headset picks the headset, a
/// moderator at their desk picks the desk microphone, and anybody who does not want to think about
/// it leaves it on the default and lets Windows move it.</para>
/// <para><strong>A chosen microphone that is not there falls back to the default.</strong> Not to
/// silence. A headset left switched off would otherwise mean the phrase was never heard again and
/// nothing on the screen would say why. The choice is kept, so the moment the headset is back it
/// is used again, and the card says the default is standing in. This is the same rule the voice's
/// output follows (<see cref="Voice.OutputDeviceChoice"/>) and it is deliberately the same rule,
/// because a moderator should not have to learn two.</para>
/// </remarks>
/// <param name="Microphone">The microphone to open, or null for whichever Windows calls the default.</param>
/// <param name="FellBack">True when a chosen microphone was not there and the default is standing in.</param>
public sealed record MicrophoneChoice(Microphone? Microphone, bool FellBack)
{
    /// <summary>The Windows default, chosen by choosing nothing.</summary>
    public static MicrophoneChoice Default { get; } = new(null, false);

    /// <summary>What the Listening card says when a chosen microphone is not there.</summary>
    public const string NotConnected =
        "That microphone is not connected. Modbot is using the Windows default instead.";

    /// <summary>That sentence when it applies, or null.</summary>
    public string? Problem => FellBack ? NotConnected : null;

    /// <param name="wantedId">The id saved in settings, or null or blank for the Windows default.</param>
    /// <param name="microphones">What this PC has right now.</param>
    public static MicrophoneChoice Resolve(string? wantedId, IReadOnlyList<Microphone> microphones)
    {
        ArgumentNullException.ThrowIfNull(microphones);

        if (string.IsNullOrWhiteSpace(wantedId))
            return Default;

        var present = microphones.FirstOrDefault(m => string.Equals(m.Id, wantedId, StringComparison.Ordinal));

        return present is null
            ? new MicrophoneChoice(null, true)
            : new MicrophoneChoice(present, false);
    }
}
