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
/// (to put it there, once) and <see cref="SherpaVoice"/> (to load it). Nothing else.</para>
/// <para><strong>What leaves the machine.</strong> The one download, described on
/// <see cref="VoiceDownload"/>, and only when the voice is turned on or tested and is not yet on the
/// disk. Nothing about what is said, or to whom, goes anywhere.</para>
/// <para><strong>No output is not a fault.</strong> A PC with no sound device, or a Linux box
/// without OpenAL's library, keeps every other part of the companion running; the settings page
/// says the voice has nothing to play through, and that is all.</para>
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

    private IVoiceSynthesizer? _synthesizer;
    private Task<IVoiceSynthesizer>? _loading;
    private bool _loadFailed;
    private bool _present;
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
    public VoiceHost(
        string companionFolder,
        HttpClient http,
        IModbotClock clock,
        Func<VoiceSettings> settings,
        Func<string?> moderatorId,
        ISpokenName? names = null)
    {
        _voicesFolder = VoiceModel.VoicesFolder(companionFolder);
        _http = http;
        _clock = clock;
        _settings = settings;
        _present = _model.IsPresent(_voicesFolder);

        (_devices, var player, _output) = OpenOutput();
        HasOutput = _output is not null;

        Announcer = new VoiceAnnouncer(
            new AnnouncementQueue(clock),
            names ?? new PlainSpokenName(),
            settings,
            moderatorId,
            Synthesizer,
            player,
            _devices,
            line => Log.Information("Voice: {Line}", line));

        if (OperatingSystem.IsWindows() && _output is WindowsVoiceOutput windows)
            windows.Changed += () => _devicesChanged = true;

        Log.Information(
            "Voice: {State}; output {Output}",
            _present ? "downloaded" : "not downloaded",
            HasOutput ? "available" : "not available on this PC");
    }

    public VoiceAnnouncer Announcer { get; }

    /// <summary>False when this PC has nothing to play through. The voice then cannot speak.</summary>
    public bool HasOutput { get; }

    /// <summary>
    /// The settings changed. Turning the voice on fetches it if it is not here; turning it off
    /// drops whatever was waiting to be said.
    /// </summary>
    public void Apply(VoiceSettings before, VoiceSettings after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (after.On && !before.On)
            Retry();

        if (!after.On)
            Announcer.Clear();
    }

    /// <summary>The Test button: one line, fetching the voice first if it is not here.</summary>
    public void Test()
    {
        Retry();
        Announcer.Test();
    }

    /// <summary>
    /// One turn: finish a download that completed, start one that is due, and say the next line.
    /// Safe on a timer.
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

        await Announcer.SpeakNextAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The voice as the settings page shows it.</summary>
    public VoiceStatus Status()
    {
        RefreshDevices();

        var state = _download is not null ? VoiceState.Downloading
            : _downloadFailed || _loadFailed ? VoiceState.Failed
            : _present ? VoiceState.Ready
            : VoiceState.NotDownloaded;

        return new VoiceStatus(_settings(), _deviceList, _default, HasOutput, state, _progress, _problem);
    }

    public void Dispose()
    {
        _synthesizer?.Dispose();
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
    /// starts here, on a worker thread, the first time a line is waiting for it.
    /// </summary>
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

            Log.Information("The voice engine is loaded");
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            _problem = $"The voice engine could not start: {ex.Message}";
            Log.Warning(ex, "The voice engine could not start");
        }
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
