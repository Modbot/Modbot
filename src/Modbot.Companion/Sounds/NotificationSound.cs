using Modbot.Companion.Presentation;
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
/// <para><strong>The moderator's filters are asked here.</strong> Beside the switch and the
/// volume rather than inside <see cref="BleepRule"/>, which is about pacing -- the same thing not
/// sounding twice, and a quiet gap between sounds -- and not about whether a kind is wanted at all
/// (notification filters design 2026-09-19 §5).</para>
/// <para><strong>The moderator's own file, when they chose one.</strong> The client still ships no
/// audio file; it makes one, and plays a <c>.wav</c> of the moderator's instead when the
/// Notifications card names one (<see cref="SoundFile"/>). One file is read, by the exact path they
/// typed, and only the first time it is needed — after that the samples are held. A file that has
/// gone missing, or that this account may not read, plays the built-in sound and leaves one
/// sentence in <see cref="LastProblem"/> for the settings screen.</para>
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
    private readonly Func<NotificationFilters>? _filters;

    /// <summary>Made once. The samples are the same every time, and there are only a few thousand.</summary>
    private readonly Lazy<VoiceClip> _clip = new(() => Bleep.Make());

    /// <summary>The moderator's own file as samples, and the path it was read from.</summary>
    private readonly Lock _ownSound = new();

    private string? _ownSoundPath;
    private VoiceClip? _ownSoundClip;

    private int _playing;

    /// <param name="settings">Read at the moment of playing, so a switch flipped mid-session takes effect at once.</param>
    /// <param name="outputDeviceId">The voice's chosen output device, or null for the system default.</param>
    /// <param name="log">Where one-line notes go: a sound that could not be played.</param>
    /// <param name="filters">
    /// The Notifications card's Sound column, read at the moment of playing so a tick changed
    /// mid-session takes effect at once. Null means nothing is filtered.
    /// </param>
    public NotificationSound(
        IVoicePlayer player,
        IOutputDevices devices,
        Func<NotificationSettings> settings,
        Func<string?> outputDeviceId,
        BleepRule rule,
        Action<string>? log = null,
        Func<NotificationFilters>? filters = null)
    {
        _player = player;
        _devices = devices;
        _settings = settings;
        _outputDeviceId = outputDeviceId;
        _rule = rule;
        _log = log;
        _filters = filters;
    }

    /// <summary>True while a sound is being played.</summary>
    public bool IsPlaying => Volatile.Read(ref _playing) == 1;

    /// <summary>
    /// What went wrong with the moderator's own sound file most recently, in one sentence, or null.
    /// Shown on the Notifications card; the built-in sound plays either way.
    /// </summary>
    public string? LastProblem { get; private set; }

    /// <summary>
    /// Says that something happened. Returns at once; the sound, if the rule allows one, plays on
    /// its own.
    /// </summary>
    public void Ask(NotificationKind kind, string? about = null)
        => _ = PlayAsync(kind, about);

    /// <summary>
    /// The same thing, awaited: false when the moderator does not want this kind, the rule refused
    /// it, the bleep is off, the volume is nothing, or a sound is already playing.
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

        if (_filters?.Invoke() is { } filters && !filters.SoundPlays(kind))
            return false;

        if (!_rule.Ask(kind, about))
            return false;

        if (Interlocked.CompareExchange(ref _playing, 1, 0) != 0)
            return false;

        try
        {
            var device = OutputDeviceChoice.Resolve(_outputDeviceId(), _devices).Device;
            await _player.PlayAsync(Clip(settings).WithGain(settings.Gain), device, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Which samples are played: the moderator's own file when they named one and it can be read,
    /// and the one the client makes otherwise.
    /// </summary>
    /// <remarks>
    /// The file is read the first time it is wanted and then held, so forty sounds in an evening is
    /// one read. A path that changes is read again; a path that could not be read is not tried
    /// again until it changes, because a sound is not worth hitting a missing disk for every time.
    /// </remarks>
    private VoiceClip Clip(NotificationSettings settings)
    {
        var wanted = settings.SoundOrNone;

        lock (_ownSound)
        {
            if (!string.Equals(_ownSoundPath, wanted, StringComparison.Ordinal))
            {
                _ownSoundPath = wanted;
                var read = SoundFile.Read(wanted);
                _ownSoundClip = read.Clip;
                LastProblem = read.Problem;

                if (read.Problem is { } problem)
                    _log?.Invoke($"{problem} Modbot's own sound is being played instead.");
            }

            return _ownSoundClip ?? _clip.Value;
        }
    }
}
