using Modbot.Companion.Presentation;
using Modbot.Companion.Voice;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Companion.App.Voice;

/// <summary>
/// Owns the voice for the life of the process: the sound output for this platform, the engine
/// once it is loaded, the one download that fetches the voice, and the announcer that ties them
/// together.
/// </summary>
/// <remarks>
/// <para><strong>What this reads and writes.</strong> The companion's own <c>voices</c> folder,
/// through <see cref="VoiceModel"/> (to see whether the voice is there), <see cref="VoiceDownload"/>
/// (to put it there, once, and to take an older voice away afterwards) and
/// <see cref="SherpaVoice"/> (to load it). Nothing else.</para>
/// <para><strong>What leaves the machine.</strong> The one download, described on
/// <see cref="VoiceDownload"/>, and only when the voice is turned on or tested and is not yet on the
/// disk. Nothing about what is said, or to whom, goes anywhere.</para>
/// <para><strong>No output is not a fault.</strong> A PC with no sound device, or a Linux box
/// without OpenAL's library, keeps every other part of the companion running; the settings page
/// says the voice has nothing to play through, and that is all.</para>
/// <para><strong>The engine is loaded only to say something, and let go of afterwards.</strong>
/// It costs about 400 MB while it is held, which is most of what the whole client uses, so it is
/// loaded when there is a line waiting and let go of once the voice has been quiet for a while
/// (<see cref="VoiceUnloadRule"/>). With the voice switched off nothing queues a line, so nothing
/// loads at all, and switching it off lets go of an engine that was already loaded. Both the
/// loading and the letting go happen on a worker thread: neither may hold up the window.</para>
/// </remarks>
internal sealed class VoiceHost : IDisposable
{
    /// <summary>How often the device list is re-read when nothing has said it changed.</summary>
    private static readonly TimeSpan DeviceListMaxAge = TimeSpan.FromSeconds(10);

    private readonly string _voicesFolder;
    private readonly HttpClient _http;
    private readonly IModbotClock _clock;
    private readonly Func<VoiceSettings> _settings;
    private readonly VoiceModel _model = VoiceModel.Default;
    private readonly IOutputDevices _devices;
    private readonly IDisposable? _output;
    private readonly Lock _gate = new();
    private readonly VoiceUnloadRule _unload;

    private IVoiceSynthesizer? _synthesizer;
    private Task<IVoiceSynthesizer>? _loading;
    private bool _loadFailed;
    private bool _present;

    /// <summary>
    /// True when an older voice is sitting in the folder. Read once at start-up, and cleared when
    /// the new voice lands, because the download takes the old one away with it.
    /// </summary>
    private bool _older;

    private Task<VoiceDownloadResult>? _download;
    private bool _downloadFailed;
    private double _progress;
    private string? _problem;

    private IReadOnlyList<OutputDevice> _deviceList = [];
    private OutputDevice? _default;
    private DateTimeOffset _devicesListedAt = DateTimeOffset.MinValue;
    private volatile bool _devicesChanged = true;

    /// <param name="companionFolder">Modbot's own folder under the user profile; the voice lives in <c>voices</c> below it.</param>
    /// <param name="moderatorId">The moderator's own VRChat id, as the log last said.</param>
    /// <param name="names">How names are spoken. The default strips decoration lightly; the real normaliser plugs in here.</param>
    /// <param name="filters">The Notifications card's Voice column. Null leaves the voice's own three switches deciding.</param>
    public VoiceHost(
        string companionFolder,
        HttpClient http,
        IModbotClock clock,
        Func<VoiceSettings> settings,
        Func<string?> moderatorId,
        ISpokenName? names = null,
        Func<NotificationFilters>? filters = null)
    {
        _voicesFolder = VoiceModel.VoicesFolder(companionFolder);
        _http = http;
        _clock = clock;
        _settings = settings;
        _unload = new VoiceUnloadRule(clock);
        _present = _model.IsPresent(_voicesFolder);
        _older = !_present && _model.AnotherIsPresent(_voicesFolder);

        (_devices, var player, _output) = OpenOutput();
        HasOutput = _output is not null;
        Player = player;

        Announcer = new VoiceAnnouncer(
            new AnnouncementQueue(clock),
            names ?? new PlainSpokenName(),
            settings,
            moderatorId,
            Synthesizer,
            player,
            _devices,
            line => Log.Information("Voice: {Line}", line),
            filters);

        if (OperatingSystem.IsWindows() && _output is WindowsVoiceOutput windows)
            windows.Changed += () => _devicesChanged = true;

        Log.Information(
            "Voice: {State}; output {Output}",
            _present ? "downloaded" : _older ? "an older voice is here and will be replaced" : "not downloaded",
            HasOutput ? "available" : "not available on this PC");
    }

    public VoiceAnnouncer Announcer { get; }

    /// <summary>False when this PC has nothing to play through. The voice then cannot speak.</summary>
    public bool HasOutput { get; }

    /// <summary>
    /// This PC's sound output, so the notification bleep plays through the same one rather than
    /// opening a second. Plays nothing when the PC has no output at all.
    /// </summary>
    public IVoicePlayer Player { get; }

    /// <summary>The output devices this PC has, as the voice sees them.</summary>
    public IOutputDevices Devices => _devices;

    /// <summary>
    /// The settings changed. Turning the voice on fetches it if it is not here; turning it off
    /// drops whatever was waiting to be said and lets go of the engine.
    /// </summary>
    /// <remarks>
    /// A switched-off feature holds nothing, so the 400 MB goes back at the next turn rather than
    /// at the end of the window — but not in the middle of a line, which is why it is the next
    /// turn and not this line of code.
    /// </remarks>
    public void Apply(VoiceSettings before, VoiceSettings after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (after.On && !before.On)
            Retry();

        if (!after.On)
        {
            Announcer.Clear();
            _unload.DropWhenQuiet();
        }
    }

    /// <summary>The Test button: one line, fetching the voice first if it is not here.</summary>
    public void Test()
    {
        Retry();
        Announcer.Test();
    }

    /// <summary>
    /// One turn: finish a download that completed, start one that is due, say the next line, and
    /// let go of the engine once it has been quiet long enough. Safe on a timer.
    /// </summary>
    public async Task TickAsync(bool paused, CancellationToken cancellationToken = default)
    {
        Announcer.Paused = paused;

        if (_download is { IsCompleted: true } finished)
        {
            _download = null;
            await FinishDownloadAsync(finished).ConfigureAwait(false);
        }

        if (_loading is { IsCompleted: true } loaded)
        {
            _loading = null;
            FinishLoad(loaded);
        }

        var wanted = _settings().On || Announcer.Waiting > 0;
        if (wanted && !_present && _download is null && !_downloadFailed && HasOutput)
            StartDownload();

        if (!HasOutput)
        {
            Announcer.Clear();
            return;
        }

        if (await Announcer.SpeakNextAsync(cancellationToken).ConfigureAwait(false))
            _unload.Used();

        UnloadIfDue();
    }

    /// <summary>The voice as the settings page shows it.</summary>
    public VoiceStatus Status()
    {
        RefreshDevices();

        var state = _download is not null ? (_older ? VoiceState.Replacing : VoiceState.Downloading)
            : _downloadFailed || _loadFailed ? VoiceState.Failed
            : _present ? VoiceState.Ready
            : VoiceState.NotDownloaded;

        return new VoiceStatus(
            _settings(), _deviceList, _default, HasOutput, state, _progress, _problem, _model.Size, _model.Voices);
    }

    public void Dispose()
    {
        // Taken under the lock and cleared, so the worker thread letting go of an engine and this
        // cannot both be holding the same one.
        IVoiceSynthesizer? going;
        lock (_gate)
        {
            going = _synthesizer;
            _synthesizer = null;
        }

        going?.Dispose();
        _output?.Dispose();
    }

    /// <summary>Forgets a failure so the next tick tries again. Only a person's action does this.</summary>
    private void Retry()
    {
        _downloadFailed = false;
        _loadFailed = false;
        _problem = null;
    }

    private (IOutputDevices Devices, IVoicePlayer Player, IDisposable? Output) OpenOutput()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var windows = new WindowsVoiceOutput();
                return (windows, windows, windows);
            }

            var openAl = new OpenAlVoiceOutput();
            return (openAl, openAl, openAl);
        }
        catch (Exception ex) when (ex is DllNotFoundException or FileNotFoundException or InvalidOperationException
                                       or NotSupportedException or TypeInitializationException or EntryPointNotFoundException)
        {
            Log.Information(ex, "The voice has nothing to play through on this PC; everything else is unaffected");
            var none = new NoOutput();
            return (none, none, null);
        }
    }

    private void StartDownload()
    {
        _progress = 0;
        _problem = null;
        Log.Information("Downloading the voice {Voice} from {Url} ({Size:N0} bytes)", _model.Name, _model.Url, _model.Size);

        var progress = new Progress<double>(p => _progress = p);
        _download = Task.Run(() => new VoiceDownload(_http).RunAsync(_model, _voicesFolder, progress));
    }

    private async Task FinishDownloadAsync(Task<VoiceDownloadResult> finished)
    {
        VoiceDownloadResult result;
        try
        {
            result = await finished.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _downloadFailed = true;
            _problem = $"The voice could not be downloaded: {ex.Message}";
            Log.Warning(ex, "The voice download failed");
            return;
        }

        if (result.Ready)
        {
            _present = true;
            // The download takes any older voice away once the new one is in place, so there is
            // nothing left to be replacing.
            _older = false;
            _progress = 1;
            Log.Information("The voice {Voice} is ready ({Outcome})", _model.Name, result.Outcome);
            return;
        }

        _downloadFailed = true;
        _problem = result.Outcome switch
        {
            VoiceDownloadOutcome.WrongFile => $"The download was not the expected file and was thrown away. {result.Detail}",
            VoiceDownloadOutcome.Unreachable => $"The voice could not be downloaded. {result.Detail}",
            _ => $"The voice could not be set up. {result.Detail}",
        };
        Log.Warning("The voice download failed: {Outcome} {Detail}", result.Outcome, result.Detail);
    }

    /// <summary>
    /// The engine, once loaded; null while it is loading, not downloaded, or broken. Loading
    /// starts here, on a worker thread, whenever a line is waiting and there is no engine — the
    /// first time, and again after the engine has been let go of.
    /// </summary>
    /// <remarks>
    /// The line that asked for it waits in the queue while the load runs, and the queue drops a
    /// join or a leave that has waited more than a few seconds. A reload takes about two thirds of
    /// a second, so the line is normally still there; on a machine slow enough that it is not, the
    /// name is dropped rather than said about the wrong moment, which is the queue's own rule and
    /// was already what the very first load did.
    /// </remarks>
    private IVoiceSynthesizer? Synthesizer()
    {
        lock (_gate)
        {
            if (_synthesizer is not null)
                return _synthesizer;

            if (!_present || _loadFailed || _loading is not null)
                return null;

            _loading = Task.Run(IVoiceSynthesizer () => SherpaVoice.Load(_model, _voicesFolder));
            return null;
        }
    }

    private void FinishLoad(Task<IVoiceSynthesizer> loaded)
    {
        try
        {
            lock (_gate)
                _synthesizer = loaded.GetAwaiter().GetResult();

            // The window starts here as well as at the end of a line, so an engine that loaded for
            // a line the queue had already dropped does not sit there until something else arrives.
            _unload.Used();
            Log.Information("The voice engine is loaded");
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            _problem = $"The voice engine could not start: {ex.Message}";
            Log.Warning(ex, "The voice engine could not start");
        }
    }

    /// <summary>
    /// Lets go of the engine once the voice has been quiet long enough, giving its memory back.
    /// </summary>
    /// <remarks>
    /// <para>Never while a line is waiting or being said, and never while a load is in flight: the
    /// first would lose the line and the second would throw away the load. The queue and the
    /// "speaking" flag are both asked, because a line taken from the queue is no longer in it.</para>
    /// <para>Disposing is the engine handing a few hundred megabytes back to the operating system.
    /// It is quick — measured at about 20 milliseconds — but it is the audio library's work, not
    /// the window's, so it happens on a worker thread like the loading does.</para>
    /// </remarks>
    private void UnloadIfDue()
    {
        IVoiceSynthesizer going;

        lock (_gate)
        {
            var busy = Announcer.IsSpeaking || Announcer.Waiting > 0 || _loading is not null;
            if (!_unload.ShouldUnload(_synthesizer is not null, busy))
                return;

            going = _synthesizer!;
            _synthesizer = null;
        }

        Log.Information("The voice engine is let go of until the next line");

        _ = Task.Run(() =>
        {
            try
            {
                going.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "The voice engine could not be let go of cleanly");
            }
        });
    }

    private void RefreshDevices()
    {
        if (!_devicesChanged && _clock.UtcNow - _devicesListedAt < DeviceListMaxAge)
            return;

        _devicesChanged = false;
        _devicesListedAt = _clock.UtcNow;

        try
        {
            var before = _default?.Id;
            _deviceList = _devices.List();
            _default = _devices.Default();

            if (!string.Equals(before, _default?.Id, StringComparison.Ordinal) && _default is not null)
                Log.Information("The system default output is now {Device}", _default.Name);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not list the output devices");
        }
    }

    /// <summary>A PC with nothing to play through: no devices, and a player that plays nothing.</summary>
    private sealed class NoOutput : IOutputDevices, IVoicePlayer
    {
        public IReadOnlyList<OutputDevice> List() => [];

        public OutputDevice? Default() => null;

        public Task PlayAsync(VoiceClip clip, OutputDevice? device, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
