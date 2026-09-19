using Modbot.Companion.CloudBackup;
using Modbot.Companion.Instances;
using Modbot.Companion.Journal;
using Modbot.Companion.Presentation;
using Modbot.Companion.Sounds;

namespace Modbot.Companion.Voice;

/// <summary>
/// The voice: hears what the companion saw, decides what is worth saying, and says it one line at
/// a time.
/// </summary>
/// <remarks>
/// <para><strong>What it says.</strong> From the companion's own reading of VRChat's log, people
/// joining and leaving the instance the moderator is standing in — never the moderator's own
/// arrival or departure, which they know about. From the paired server, a flagged-join alert the
/// overlay would show as a card, and the one problem worth hearing: a server that rejected this
/// device, because that means reporting has silently stopped. It can also say the three it used
/// not to — somebody who was already there, an avatar change, and VRChat's log stopping — though
/// none of those is on by default.</para>
/// <para><strong>Which kinds it says is the Notifications card's Voice column.</strong> Read on
/// every event, so a tick changed mid-session takes effect at once. When no filters are handed in
/// — a test, or a client older than the card — the voice's own three switches decide, as they
/// always did (notification filters design 2026-09-19 §4.2).</para>
/// <para><strong>Silent while paused.</strong> Pausing reporting means "stop watching what I do",
/// and a voice that kept narrating the instance would be watching. Anything queued is dropped rather
/// than saved up.</para>
/// <para><strong>Never over itself.</strong> One line is spoken at a time; while it plays, new
/// lines wait in the queue, which folds a burst into one sentence and drops what has gone stale.</para>
/// <para><strong>Nothing leaves the machine.</strong> Sentences are made and played on this PC.
/// No server is told the voice exists.</para>
/// </remarks>
public sealed class VoiceAnnouncer : IObservationSink
{
    /// <summary>The line the Test button speaks.</summary>
    public const string TestLine = "Modbot's voice is working.";

    private readonly AnnouncementQueue _queue;
    private readonly ISpokenName _names;
    private readonly Func<VoiceSettings> _settings;
    private readonly Func<NotificationFilters>? _filters;
    private readonly Func<string?> _moderatorId;
    private readonly Func<IVoiceSynthesizer?> _synthesizer;
    private readonly IVoicePlayer _player;
    private readonly IOutputDevices _devices;
    private readonly Action<string>? _log;
    private readonly HashSet<string> _fallbacksLogged = new(StringComparer.Ordinal);

    private int _speaking;

    /// <param name="settings">Read on every event, so a switch flipped mid-session takes effect at once.</param>
    /// <param name="moderatorId">The moderator's own VRChat id as the log last said, or null while unknown.</param>
    /// <param name="synthesizer">The engine, or null while the voice is not downloaded or not loaded; lines wait and age out.</param>
    /// <param name="log">Where one-line notes go: a device that fell back, a line that could not be played.</param>
    /// <param name="filters">
    /// The Notifications card's Voice column. Null falls back to the voice's own three switches.
    /// </param>
    public VoiceAnnouncer(
        AnnouncementQueue queue,
        ISpokenName names,
        Func<VoiceSettings> settings,
        Func<string?> moderatorId,
        Func<IVoiceSynthesizer?> synthesizer,
        IVoicePlayer player,
        IOutputDevices devices,
        Action<string>? log = null,
        Func<NotificationFilters>? filters = null)
    {
        _queue = queue;
        _names = names;
        _settings = settings;
        _moderatorId = moderatorId;
        _synthesizer = synthesizer;
        _player = player;
        _devices = devices;
        _log = log;
        _filters = filters;
    }

    /// <summary>Set by the host: true while reporting to any paired server is paused.</summary>
    public bool Paused { get; set; }

    /// <summary>True while a line is being made or played.</summary>
    public bool IsSpeaking => Volatile.Read(ref _speaking) == 1;

    /// <summary>How many lines are waiting.</summary>
    public int Waiting => _queue.Count;

    /// <summary>
    /// The engine's observations, as they are read. The kinds the Voice column has ticked become
    /// lines; the moderator's own comings and goings never do.
    /// </summary>
    public void Offer(IReadOnlyList<ObservedPresence> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        var settings = _settings();
        if (!settings.On || Paused)
            return;

        var moderator = _moderatorId();

        foreach (var observation in observations)
        {
            // A stopped log is about the moderator themselves, so it is the one kind their own id
            // does not disqualify.
            if (observation.Kind is not PresenceKind.LogStopped
                && moderator is not null
                && string.Equals(observation.SubjectId, moderator, StringComparison.Ordinal))
            {
                continue;
            }

            if (NotificationFilters.KindOf(observation.Kind) is not { } kind || !Says(settings, kind))
                continue;

            var name = _names.Spoken(observation.DisplayName);

            switch (observation.Kind)
            {
                case PresenceKind.Joined:
                    _queue.Add(AnnouncementKind.Joined, name);
                    break;
                case PresenceKind.Left:
                    _queue.Add(AnnouncementKind.Left, name);
                    break;
                case PresenceKind.PresenceObserved:
                    _queue.Add(AnnouncementKind.AlreadyThere, name);
                    break;
                case PresenceKind.AvatarChanged:
                    _queue.Add(
                        AnnouncementKind.ChangedAvatar,
                        SentJournal.Sentence(PresenceKind.AvatarChanged, name, observation.AvatarName));
                    break;
                case PresenceKind.LogStopped:
                    _queue.Add(AnnouncementKind.LogStopped, SentJournal.Sentence(PresenceKind.LogStopped, name));
                    break;
            }
        }
    }

    /// <summary>
    /// Whether the voice says this kind: the Notifications card's Voice column when there is one,
    /// and the voice's own three switches when there is not.
    /// </summary>
    private bool Says(VoiceSettings settings, NotificationKind kind)
    {
        if (_filters?.Invoke() is { } filters)
            return filters.VoiceSays(kind);

        return kind switch
        {
            NotificationKind.Joined => settings.Joins,
            NotificationKind.Left => settings.Leaves,
            NotificationKind.FlaggedJoin => settings.FlaggedJoins,
            NotificationKind.Problem => true,
            _ => false,
        };
    }

    /// <summary>A flagged-join alert the overlay is showing: "Flagged user Rin joined".</summary>
    public void FlaggedJoin(string? displayName)
    {
        var settings = _settings();
        if (!settings.On || Paused || !Says(settings, NotificationKind.FlaggedJoin))
            return;

        _queue.Add(AnnouncementKind.FlaggedJoin, $"Flagged user {_names.Spoken(displayName)} joined");
    }

    /// <summary>Something the moderator needs to hear, as a finished sentence.</summary>
    public void Problem(string sentence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sentence);

        var settings = _settings();
        if (!settings.On || Paused || !Says(settings, NotificationKind.Problem))
            return;

        _queue.Add(AnnouncementKind.Problem, sentence);
    }

    /// <summary>The Test button: one line, whether or not the voice is on.</summary>
    public void Test() => _queue.Add(AnnouncementKind.Test, TestLine);

    /// <summary>Forgets everything waiting. Used when the voice is turned off or paused.</summary>
    public void Clear() => _queue.Clear();

    /// <summary>
    /// Says the next line if there is one and nothing is being said. Safe to call on a timer.
    /// </summary>
    /// <returns>True when a line was spoken (or attempted); false when there was nothing to do.</returns>
    public async Task<bool> SpeakNextAsync(CancellationToken cancellationToken = default)
    {
        if (Paused && _queue.Count > 0)
            _queue.Clear();

        if (_queue.Count == 0 || IsSpeaking)
            return false;

        var synthesizer = _synthesizer();
        if (synthesizer is null)
            return false;

        if (Interlocked.CompareExchange(ref _speaking, 1, 0) != 0)
            return false;

        try
        {
            var line = _queue.Next();
            if (line is null)
                return false;

            var settings = _settings();
            var choice = OutputDeviceChoice.Resolve(settings.OutputDeviceId, _devices);
            if (choice.FellBack && settings.OutputDeviceId is { } wanted && _fallbacksLogged.Add(wanted))
            {
                _log?.Invoke(
                    $"The chosen output device is not present; using the system default"
                    + (_devices.Default() is { } fallback ? $" ({fallback.Name})" : string.Empty)
                    + " until it is back.");
            }

            var clip = await Task.Run(() => synthesizer.Speak(line, settings.VoiceName), cancellationToken).ConfigureAwait(false);
            await _player.PlayAsync(clip.WithGain(settings.Gain), choice.Device, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"A line could not be spoken: {ex.GetType().Name}: {ex.Message}");
            return true;
        }
        finally
        {
            Volatile.Write(ref _speaking, 0);
        }
    }
}
