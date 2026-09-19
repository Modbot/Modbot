using Modbot.Companion.Voice;

namespace Modbot.Companion.Sounds;

/// <summary>
/// Plays the bleep when the client has something to tell the moderator.
/// </summary>
/// <remarks>
/// <para><strong>Nothing leaves the machine and nothing is read.</strong> The sound is made from
/// the formula in <see cref="Bleep"/> and played on this PC. No server is told that it happened,
/// and the client learns nothing from playing it.</para>
/// <para><strong>The voice's device, the bleep's own switch.</strong> It plays through the output
/// the moderator chose for the voice — the same fallback rule, so a headset that is not plugged in
/// right now means the system default rather than silence — while whether it plays at all, and how
/// loud, are its own settings. Somebody who wants a sound but not a talking PC turns the voice off
/// and leaves this on.</para>
/// <para><strong>A failure is not a fault.</strong> No output device, a device that went away
/// mid-sound, an audio library that will not load: it is written down and everything else carries
/// on. This is the least important thing the client does.</para>
/// </remarks>
public sealed class NotificationSound
{
    private readonly IVoicePlayer _player;
    private readonly IOutputDevices _devices;
    private readonly Func<NotificationSettings> _settings;
    private readonly Func<string?> _outputDeviceId;
    private readonly BleepRule _rule;
    private readonly Action<string>? _log;

    /// <summary>Made once. The samples are the same every time, and there are only a few thousand.</summary>
    private readonly Lazy<VoiceClip> _clip = new(() => Bleep.Make());

    private int _playing;

    /// <param name="settings">Read at the moment of playing, so a switch flipped mid-session takes effect at once.</param>
    /// <param name="outputDeviceId">The voice's chosen output device, or null for the system default.</param>
    /// <param name="log">Where one-line notes go: a sound that could not be played.</param>
    public NotificationSound(
        IVoicePlayer player,
        IOutputDevices devices,
        Func<NotificationSettings> settings,
        Func<string?> outputDeviceId,
        BleepRule rule,
        Action<string>? log = null)
    {
        _player = player;
        _devices = devices;
        _settings = settings;
        _outputDeviceId = outputDeviceId;
        _rule = rule;
        _log = log;
    }

    /// <summary>True while a sound is being played.</summary>
    public bool IsPlaying => Volatile.Read(ref _playing) == 1;

    /// <summary>
    /// Says that something happened. Returns at once; the sound, if the rule allows one, plays on
    /// its own.
    /// </summary>
    public void Ask(NotificationKind kind, string? about = null)
        => _ = PlayAsync(kind, about);

    /// <summary>
    /// The same thing, awaited: false when the rule refused it, the bleep is off, the volume is
    /// nothing, or a sound is already playing.
    /// </summary>
    public async Task<bool> PlayAsync(NotificationKind kind, string? about = null, CancellationToken cancellationToken = default)
    {
        var settings = _settings();

        // The Test button sounds whether or not the bleep is switched on, the way the voice's Test
        // speaks whether or not the voice is on: a person pressed it.
        if (!settings.Bleep && kind is not NotificationKind.Test)
            return false;

        if (settings.Volume <= 0)
            return false;

        if (!_rule.Ask(kind, about))
            return false;

        if (Interlocked.CompareExchange(ref _playing, 1, 0) != 0)
            return false;

        try
        {
            var device = OutputDeviceChoice.Resolve(_outputDeviceId(), _devices).Device;
            await _player.PlayAsync(_clip.Value.WithGain(settings.Gain), device, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"The notification sound could not be played: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            Volatile.Write(ref _playing, 0);
        }
    }
}
