using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Modbot.Companion.CloudBackup;
using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.LogReading;
using Modbot.Companion.Pairing;
using Modbot.Companion.Pipeline;
using Modbot.Companion.Presentation;
using Modbot.Companion.Startup;
using Modbot.Companion.Overlay;
using Modbot.Companion.Time;
using Modbot.Companion.Voice;
using Modbot.Companion.App.Voice;
using Modbot.Core;
using Modbot.Core.Time;
using Modbot.Overlay;
using Modbot.Overlay.Driving;
using Modbot.Overlay.OpenVr;
using Serilog;

namespace Modbot.Companion.App;

/// <summary>
/// The tray application.
/// </summary>
/// <remarks>
/// <para><strong>What this program reads.</strong> One folder: VRChat's own log directory, and
/// within it only the files VRChat names <c>output_log_*.txt</c>. It opens them for reading,
/// shared, and never writes to them. Anybody suspicious can open the same file in Notepad and see
/// exactly what Modbot is looking at.</para>
/// <para><strong>What it writes to your disk.</strong> Its own folder under your user profile,
/// holding three things: which servers you paired with and their tokens (the tokens encrypted to
/// your Windows account), observations queued to send, and the plain-English record of what has
/// been sent — and, once you turn the voice on, the downloaded voice under <c>voices</c>. Plus
/// one registry key under your own account saying that <c>modbot-companion://</c>
/// links open this program, which is how pairing from the browser reaches it, and — in an installed
/// copy, unless you turn it off — one value under your own account's startup list so Modbot starts
/// in the tray when you sign in. Nothing else on the machine is touched.</para>
/// <para><strong>What leaves the machine.</strong> Presence observations, to the Modbot servers
/// you paired with — and only for instances belonging to the group each of those servers manages;
/// a server is never sent a raw log line or anything about your private, friends-only or public
/// VRChat use. Separately, and whether or not anything is paired, the same presence events — for
/// every instance, private ones included, but never a raw log line — are backed up to Modbot Cloud,
/// unless you turn that off in <c>settings.json</c> or with <c>MODBOT_CLOUD_DISABLED</c> (see
/// <c>CloudEventBackup</c> and <c>CloudSettings</c>). And once, if you turn the voice on: one
/// download of the voice from GitHub, with nothing attached (see <c>VoiceDownload</c>); what the
/// voice then says is made and played on this PC and goes nowhere.
/// Never chat, screenshots, keystrokes, your friends list or a list of your processes.</para>
/// <para><strong>It never captures the screen.</strong> Not the desktop, not a window, not
/// VRChat's screenshot folder, not any other folder. Attaching evidence to a moderation case is a
/// deliberate human action taken in Modbot's web interface, in a browser, by choosing a file —
/// which is why this program needs no such capability and does not have one.</para>
/// <para><strong>It is always visible while it runs.</strong> Closing the window leaves a tray
/// icon; the program never becomes invisible, and pausing stops transmission immediately and shows
/// that it has.</para>
/// <para><strong>It runs once.</strong> Starting it again — which is what Windows does when a
/// browser opens a <c>modbot-companion://</c> link — hands the link to the copy already running and
/// exits. One tray icon, one log reader, one set of queues.</para>
/// </remarks>
internal sealed class ModbotCompanionApp : Application
{
    /// <summary>
    /// What this process was started with, if anything: a pairing link from the browser, or a
    /// request to show the window. Handled once the host is up.
    /// </summary>
    internal static string? StartupMessage { get; set; }

    /// <summary>Started by Windows at sign-in: stay in the tray and open no window.</summary>
    internal static bool StartHidden { get; set; }

    /// <summary>
    /// Avalonia draws a templated control -- a text box, a button, a check box -- only through a
    /// control theme, and an application with none draws nothing where those should be. The
    /// pairing token box was the first casualty anyone noticed: the card rendered its words and
    /// the box between them was simply absent. The colours and sizes on every control are still
    /// set by hand in Controls.cs; the theme supplies the templates those settings apply to.
    /// </summary>
    public override void Initialize()
    {
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;

        // The theme paints focus rings and selections in the machine's own accent colour, which
        // on a Windows set to red gave every focused box a red edge. Modbot's accent instead,
        // in the three shades the theme derives from it.
        var accent = Ui.T.Palette.Accent;
        Resources["SystemAccentColor"] = accent;
        Resources["SystemAccentColorDark1"] = accent;
        Resources["SystemAccentColorDark2"] = accent;
        Resources["SystemAccentColorDark3"] = accent;
        Resources["SystemAccentColorLight1"] = accent;
        Resources["SystemAccentColorLight2"] = accent;
        Resources["SystemAccentColorLight3"] = accent;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the window leaves the client reporting from the tray, which is the point of
            // it. Quitting is a deliberate act from the tray menu -- and quitting really does stop
            // reporting, rather than minimising to somewhere less visible.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Built here and not in a static initializer: the host owns the main window, and a
            // window can only exist once Avalonia's windowing platform is up. A static
            // initializer runs on the first touch of any static member of this class -- Main
            // setting StartupMessage was enough -- which is before Avalonia has started, and the
            // whole client then died at launch with "Unable to locate IWindowingPlatform".
            CrashGuard.InstallForUi();

            try
            {
                Host = new CompanionHost();

                // The lifetime shows its main window at start. A start from the startup entry has none,
                // so the client sits in the tray until the icon is clicked.
                if (!StartHidden)
                    desktop.MainWindow = Host.Window;
                Host.Start(desktop, StartupMessage);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "The companion could not start");
                throw;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static CompanionHost? Host { get; private set; }
}

/// <summary>
/// Holds the window, the state behind it, and the timer that refreshes it.
/// </summary>
/// <remarks>
/// The engine that reads the log and reports is constructed by whatever composes this application;
/// this type owns only what the moderator sees and the actions they can take. Keeping the two
/// apart is what lets the reading and reporting half stay a small library that can be audited
/// without reading any UI code.
/// </remarks>
internal sealed class CompanionHost : IOverlayListener
{
    /// <summary>
    /// What a second copy sends when it was started with no link: the person double-clicked the
    /// icon again, and wants the window.
    /// </summary>
    internal const string ShowCommand = "show";

    /// <summary>
    /// How often the voice is given a turn: to finish a download, to start the next line. A turn
    /// with nothing waiting costs a few comparisons.
    /// </summary>
    private readonly DispatcherTimer _voiceLoop = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>
    /// Writes the voice settings a moment after the last change, so a volume slider being dragged
    /// is one write rather than a hundred.
    /// </summary>
    private readonly DispatcherTimer _voiceSave = new() { Interval = TimeSpan.FromMilliseconds(500) };

    /// <summary>Which servers' token rejections the voice has already said, so each is said once.</summary>
    private readonly HashSet<string> _tokenRejectionsSpoken = new(StringComparer.Ordinal);

    private readonly IModbotClock _clock = new SystemModbotClock();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>
    /// How often the overlay loop is given a turn.
    /// </summary>
    /// <remarks>
    /// Short, because it is cheap: a turn with nothing to do reads nothing and draws nothing, and
    /// the reads inside it have their own intervals. What this rate actually buys is how quickly a
    /// finished long poll becomes a card on screen.
    /// </remarks>
    private readonly DispatcherTimer _overlayLoop = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>
    /// The controllers, looked at thirty times a second while a VR runtime is attached, so the
    /// cursor and a held panel move smoothly; a look costs nothing when none is attached.
    /// </summary>
    private readonly DispatcherTimer _inputLoop = new() { Interval = TimeSpan.FromMilliseconds(33) };

    /// <summary>Writes the panel's placement half a second after it last changed.</summary>
    private readonly DispatcherTimer _placementSave = new() { Interval = TimeSpan.FromMilliseconds(500) };

    /// <summary>
    /// How often the reading half is given a turn.
    /// </summary>
    /// <remarks>
    /// A turn with nothing to do costs one <c>FileStream</c> open on a file whose length has not
    /// changed. What this rate buys is how soon the overlay learns the moderator has walked into a
    /// different instance — VRChat writes the transition and the overlay should follow it within a
    /// second or two, not within a batch interval.
    /// </remarks>
    private readonly DispatcherTimer _engineLoop = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>One queue file per paired server, kept so unpairing can delete the right one.</summary>
    private readonly Dictionary<string, FileEventBuffer> _buffers = new(StringComparer.Ordinal);

    /// <summary>Stops the inbox that second copies drop pairing links into, when this copy quits.</summary>
    private readonly CancellationTokenSource _inboxStop = new();

    private string _directory = string.Empty;
    private CompanionAppState? _state;
    private PairingCoordinator? _pairing;
    private Journal.SentJournal? _journal;
    private CompanionEngine? _engine;
    private VRChatLogTail? _tail;
    private string? _lastLoggedFile;
    private long _lastLoggedLines;
    private int _consecutiveTickFailures;
    private TrayIcon? _tray;
    private HttpClient? _http;
    private IIngestTransport? _transport;
    private OverlayDriver? _overlay;
    private OverlayHost? _overlayHost;
    private OverlayPreviewWindow? _preview;
    private OverlaySample? _pinnedSample;
    private DateTimeOffset? _overlayAttachedAt;
    private DateTimeOffset? _overlayLastDrewAt;
    private int _overlayFramesSeen;
    private Updates? _updates;
    private CloudEventBackup? _cloudBackup;
    private VoiceHost? _voice;
    private bool _voiceTicking;
    private string _settingsPath = string.Empty;

    /// <summary>Stops the event backup's own task when this copy quits.</summary>
    private readonly CancellationTokenSource _backupStop = new();
    private bool _overlayTicking;
    private bool _engineTicking;

    public MainWindow Window { get; } = new();

    public void Start(IClassicDesktopStyleApplicationLifetime desktop, string? startupMessage)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _directory = Path.Combine(appData, "Modbot");

        _journal = new Journal.SentJournal(Path.Combine(_directory, "sent.jsonl"), _clock);
        _settingsPath = CompanionSettings.DefaultPath(appData);
        _state = new CompanionAppState(_clock, _journal, CompanionSettings.Load(_settingsPath));

        // MODBOT_DEBUG_MODE=1 adds the Debug page: the overlay's picture in a window, sample
        // screens to pin into it. Read once, at start, like the other environment switches.
        _state.DebugMode = Environment.GetEnvironmentVariable("MODBOT_DEBUG_MODE") is { } debug
            && (debug == "1" || debug.Equals("true", StringComparison.OrdinalIgnoreCase));
        if (_state.DebugMode)
            Log.Information("Debug mode is on (MODBOT_DEBUG_MODE); the window has a Debug page");

        // Only the token is encrypted; the rest of the file is left readable on purpose, so a
        // suspicious moderator can open it and see exactly which servers this client talks to.
        var store = new DpapiPairingStore(
            DpapiPairingStore.DefaultPath(appData),
            PairingSecretProtectors.ForThisMachine(appData));

        // One client, kept for the life of the process. A disposed-per-use HttpClient exhausts
        // sockets under any real traffic, and this one is also the single place pairing requests
        // leave from.
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        Window.Pictures = new GroupPictures(_http, Render);
        _pairing = new PairingCoordinator(new HttpPairingClient(_http), store);
        _transport = new HttpIngestTransport(_http);

        StartCloudBackup(appData);
        StartVoice();
        StartEngine();

        // A pairing whose token will not decrypt is shown by name rather than retried or hidden.
        // A token encrypted for a different Windows account is DPAPI working, not failing, and the
        // honest answer is "pair this one again".
        foreach (var pairing in store.Load())
        {
            if (pairing is { IsUsable: true, Pairing: { } usable })
                Connect(usable);
            else
                _state.UnusablePairings.Add(pairing);
        }

        StartOverlay();
        InstallTray(desktop);
        ListenForLinks();
        StartUpdateChecks();
        ApplyStartWithWindows();

        _refresh.Tick += (_, _) => CrashGuard.Run("refreshing the window", Render);
        _refresh.Start();
        Render();

        // Started by a browser link: the whole reason this process exists is to pair, so do that
        // now, in front of the moderator, rather than sitting in the tray waiting to be found.
        if (startupMessage is not null)
            _ = CrashGuard.RunAsync("handling the pairing link", () => HandleMessageAsync(startupMessage));
    }

    /// <summary>
    /// Brings up the event backup to Modbot Cloud, on its own task.
    /// </summary>
    /// <remarks>
    /// <para><strong>What it sends, and where.</strong> The presence events the client reports, for
    /// every instance, to <c>https://cloud.modbot.co</c> or the Cloud named in <c>settings.json</c> or
    /// <c>MODBOT_CLOUD_ENDPOINT</c>, unless <c>settings.json</c> or <c>MODBOT_CLOUD_DISABLED</c> on
    /// this PC turns it off. Paired servers have no say in it. Read once, at start. The details are on
    /// <see cref="CloudEventBackup"/> and <see cref="CloudSettings"/>.</para>
    /// <para><strong>What it writes to your disk.</strong> Its queue under
    /// <c>%APPDATA%\Modbot\cloud</c>, capped at 20 MB, and <c>cloud-installs.json</c> with this
    /// client's install id and its secret, encrypted to your Windows account.</para>
    /// <para>Its own task, so nothing it does — disk, compression, a slow or missing Cloud — ever
    /// holds up the reading loop, which also feeds presence reporting and the overlay.</para>
    /// </remarks>
    private void StartCloudBackup(string appData)
    {
        var cloud = _state!.Settings.Cloud;

        if (cloud.RejectedEndpoint is { } rejected)
            Log.Warning("Ignoring the Modbot Cloud address {Endpoint}: it must be an https address; using {Default}", rejected, cloud.Endpoint);

        _cloudBackup = new CloudEventBackup(new CloudBackupOptions(
            Path.Combine(_directory, "cloud"),
            _clock,
            new HttpCloudLogClient(_http!, _clock),
            new DpapiCloudInstallStore(
                DpapiCloudInstallStore.DefaultPath(appData),
                PairingSecretProtectors.ForThisMachine(appData, SecretPurposes.CloudSecret)),
            ModbotVersion.Release,
            Endpoint: cloud.Endpoint,
            Enabled: !cloud.Disabled,
            Journal: _journal));

        var backup = _cloudBackup;
        _ = Task.Run(() => backup.RunAsync(
            _backupStop.Token,
            ex => Log.Warning(ex, "The event backup to Modbot Cloud hit a problem; it carries on")));

        if (cloud.Disabled)
            Log.Information("Event backup to Modbot Cloud is off");
        else
            Log.Information("Event backup to Modbot Cloud is on, to {Endpoint}", cloud.Endpoint);
    }

    /// <summary>
    /// Makes Windows' startup entry match "Start Modbot Companion when my computer starts".
    /// </summary>
    /// <remarks>
    /// <para><strong>Only an installed copy does anything.</strong> The installer (asked in Updates.cs) says whether this copy was
    /// installed; one run from source or a plain folder shows no switch and never reads or writes
    /// the registry. See <see cref="StartWithWindows"/> for the rules and
    /// <see cref="StartupRegistration"/> for the one key it writes.</para>
    /// <para>Run on every start, so the first run after install turns it on, and a stale entry left
    /// by an earlier copy is rewritten.</para>
    /// </remarks>
    private void ApplyStartWithWindows()
    {
        if (_state is null || !OperatingSystem.IsWindows())
            return;

        var launcher = Updates.InstalledLauncherPath();
        _state.Startup = new StartWithWindows(new StartupRegistration())
            .Apply(launcher is not null, launcher, _state.Settings.StartWithWindows);
    }

    /// <summary>
    /// Points the log reader at a folder the person named, or back at the well-known places when
    /// they clear it. Takes effect on the next pass, with no restart: whatever log is already in
    /// the new folder is history and is not reported.
    /// </summary>
    private void SetLogFolder(string? folder)
    {
        if (_state is null || _tail is null)
            return;

        var configured = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
        if (string.Equals(configured, _state.Settings.VRChatLogFolder, StringComparison.Ordinal))
            return;

        _state.Settings = _state.Settings with { VRChatLogFolder = configured };

        if (!CompanionSettings.SaveText(_settingsPath, CompanionSettings.VRChatLogFolderField, configured))
            Log.Warning("Could not save the VRChat log folder to {Path}", _settingsPath);

        var resolved = VRChatLogFolders.Resolve(configured);
        _tail.Redirect(resolved);
        _state.LogFolder = resolved;
        _lastLoggedFile = null;
        Log.Information("Watching VRChat's log folder {Directory}", resolved);
        Render();
    }

    /// <summary>
    /// Brings up the voice: the sound output for this platform and the announcer the engine will
    /// feed. It says nothing until the settings say it may, and fetches the voice only then.
    /// </summary>
    /// <remarks>
    /// <para><strong>What leaves the machine.</strong> One download of the voice from GitHub, the
    /// first time the voice is turned on or tested, described on <see cref="VoiceDownload"/>.
    /// Nothing else: what is said is made and played on this PC.</para>
    /// <para>Built before the engine so the engine can be handed the announcer; the moderator's
    /// own id is read back from the engine, which exists by the time anything is observed.</para>
    /// </remarks>
    private void StartVoice()
    {
        _voice = new VoiceHost(
            _directory,
            _http!,
            _clock,
            () => _state!.Settings.Voice,
            () => _engine?.ModeratorId);

        _voiceLoop.Tick += async (_, _) => await CrashGuard.RunAsync("speaking", VoiceTickAsync);
        _voiceLoop.Start();

        _voiceSave.Tick += (_, _) =>
        {
            _voiceSave.Stop();
            if (_state is not null && !CompanionSettings.SaveVoice(_settingsPath, _state.Settings.Voice))
                Log.Warning("Could not save the voice settings to {Path}", _settingsPath);
        };
    }

    /// <summary>One turn of the voice, never overlapping itself: a line takes seconds to say.</summary>
    private async Task VoiceTickAsync()
    {
        if (_voice is null || _state is null || _voiceTicking)
            return;

        _voiceTicking = true;
        try
        {
            // Silent while any paired server is paused: pausing means "stop watching what I do",
            // and a voice narrating the instance would be watching.
            await _voice.TickAsync(_state.Connections.Any(c => c.IsPaused));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            _voiceTicking = false;
        }
    }

    /// <summary>The Voice card changed. Takes effect at once; the file is written a moment later.</summary>
    private void SetVoice(VoiceSettings voice)
    {
        if (_state is null || _voice is null || _state.Settings.Voice == voice)
            return;

        var before = _state.Settings.Voice;
        _state.Settings = _state.Settings with { Voice = voice };
        _voice.Apply(before, voice);

        _voiceSave.Stop();
        _voiceSave.Start();
        Render();
    }

    private void TestVoice()
    {
        _voice?.Test();
        Render();
    }

    /// <summary>
    /// A server rejected this device's token: reporting to it has stopped and will not restart on
    /// its own, which is the one problem worth hearing in the headset. Said once per server,
    /// whichever half of the companion noticed first.
    /// </summary>
    private void AnnounceTokenRejected(string serverId)
    {
        if (_voice is null || !_tokenRejectionsSpoken.Add(serverId))
            return;

        _voice.Announcer.Problem($"{serverId} rejected this device. Modbot has stopped reporting to it.");
    }

    private void NoticeTokenRejections()
    {
        foreach (var stopped in _state?.Connections.Where(c => c.State is ConnectionState.Stopped) ?? [])
            AnnounceTokenRejected(stopped.ServerId);
    }

    void IOverlayListener.AlertShown(FlaggedJoinAlert alert) => _voice?.Announcer.FlaggedJoin(alert.DisplayName);

    void IOverlayListener.TokenRejected(string label) => AnnounceTokenRejected(label);

    /// <summary>The Events page's filter bar changed. Remembered in settings.json so the page opens the way it was left.</summary>
    private void SetEventsFilters(EventFilterSet filters)
    {
        if (_state is null || _state.Settings.EventsFilters.Equals(filters))
            return;

        _state.Settings = _state.Settings with { EventsFilters = filters };

        if (!CompanionSettings.SaveEventsFilters(_settingsPath, filters))
            Log.Warning("Could not save the Events filters to {Path}", _settingsPath);
    }

    private void SetStartWithWindows(bool on)
    {
        if (_state is null || _state.Settings.StartWithWindows == on && _state.Startup.On == on)
            return;

        _state.Settings = _state.Settings with { StartWithWindows = on };

        if (!CompanionSettings.SaveSwitch(_settingsPath, CompanionSettings.StartWithWindowsField, on))
            Log.Warning("Could not save the start-with-Windows switch to {Path}", _settingsPath);

        ApplyStartWithWindows();
        Render();
    }

    /// <summary>
    /// Brings up the half that reads VRChat's log and reports what it sees.
    /// </summary>
    /// <remarks>
    /// <para><strong>One log, read once.</strong> A moderator staffing several groups runs one
    /// client, not one per group; everything after the read is per-server and separate — its own
    /// token, its own queue, its own pause switch.</para>
    /// <para><strong>It runs whether or not anything is paired.</strong> Nothing is transmitted
    /// until a server is, but the reading has to be happening for the client to know where the
    /// moderator is standing the moment they do pair.</para>
    /// </remarks>
    private void StartEngine()
    {
        var folder = VRChatLogFolders.Resolve(_state!.Settings.VRChatLogFolder);
        _tail = new VRChatLogTail(folder);
        _state.LogFolder = folder;
        Log.Information("Watching VRChat's log folder {Directory}", folder);

        var observer = new PresenceObserver(_tail, _clock);
        _engine = new CompanionEngine(
            observer,
            _clock,
            timeProbe: new HttpServerTimeProbe(_http!, _clock),
            backup: _cloudBackup,
            journal: _journal,
            voice: _voice?.Announcer);

        _engineLoop.Tick += async (_, _) => await CrashGuard.RunAsync("reading VRChat's log", EngineTickAsync);
        _engineLoop.Start();
    }

    /// <summary>
    /// One turn of the reading loop, never overlapping itself.
    /// </summary>
    /// <remarks>
    /// A turn can hold an outbound batch open for as long as the network takes, and stacking those
    /// on a timer would put several copies of the same batch in flight on a machine that is also
    /// running a game.
    /// </remarks>
    private async Task EngineTickAsync()
    {
        if (_engine is null || _engineTicking)
            return;

        _engineTicking = true;
        try
        {
            var tick = await _engine.TickAsync();
            _consecutiveTickFailures = 0;
            if (_state is not null)
                _state.ReadingFault = null;

            DescribeTick(tick);
            NoticeTokenRejections();
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // This used to escape the timer's handler, and an exception that escapes an async
            // event handler ends the process -- silently, for a windowed program. Now it is
            // written down with everything known about where the reader was, shown in the window,
            // and the loop carries on; a fault that repeats stops the reader rather than the client.
            _consecutiveTickFailures++;
            var health = _engine.LogHealth;

            Log.Error(
                ex,
                "Reading VRChat's log failed (failure {Count} in a row). File {File}; lines read {Lines}; "
                + "behaviour lines {Behaviour}; recognised events {Recognised}; last line at {LastLine}",
                _consecutiveTickFailures, _tail?.CurrentFile, health.LinesRead, health.BehaviourLines,
                health.RecognisedEvents, health.LastLineAt);

            if (_state is not null)
                _state.ReadingFault = $"{ex.GetType().Name}: {ex.Message}";

            if (_consecutiveTickFailures >= 5)
            {
                _engineLoop.Stop();
                CrashGuard.Report(
                    new InvalidOperationException(
                        "Reading VRChat's log failed five times in a row, so the reader has been stopped. "
                        + "Restart the companion once the cause is fixed. Last error: " + ex.Message, ex),
                    "reading VRChat's log",
                    fatal: false);
            }
        }
        finally
        {
            _engineTicking = false;
        }
    }

    private void DescribeTick(EngineTick tick)
    {
        var file = _tail?.CurrentFile;
        if (!string.Equals(file, _lastLoggedFile, StringComparison.OrdinalIgnoreCase))
        {
            Log.Information("Reading VRChat log {File}", file ?? "(none yet)");
            _lastLoggedFile = file;
        }

        var health = _engine!.LogHealth;

        // The first pass over an existing log is history: it produces no observations, and it is
        // also the pass that reads the most and is likeliest to hit something unexpected. So
        // lines read is reported on its own whenever it moves, observations or not.
        if (health.LinesRead != _lastLoggedLines)
        {
            Log.Verbose(
                "Read {Lines} lines from VRChat's log this tick ({Total} so far: {Behaviour} behaviour lines, {Recognised} recognised events)",
                health.LinesRead - _lastLoggedLines, health.LinesRead, health.BehaviourLines, health.RecognisedEvents);
            _lastLoggedLines = health.LinesRead;
        }

        // Quiet ticks are the normal state and are not worth a line each; a tick that did
        // anything says exactly what.
        if (tick.Observed == 0 && tick.BatchesSent == 0)
            return;

        Log.Verbose(
            "Tick: {Observed} observations, {Routed} routed, {Dropped} dropped, {Sent} sent to servers; "
            + "totals: {Lines} lines read, {Behaviour} behaviour lines, {Recognised} recognised events",
            tick.Observed, tick.Routed, tick.Dropped, tick.BatchesSent,
            health.LinesRead, health.BehaviourLines, health.RecognisedEvents);
    }

    /// <summary>
    /// Gives one paired server everything it needs to be reported to, separately from every other.
    /// </summary>
    /// <remarks>
    /// <para><strong>What this puts on your disk.</strong> One queue file per server, under your
    /// own profile folder, holding observations that have not been sent yet. It is bounded by both
    /// size and age, and unpairing deletes it.</para>
    /// <para><strong>What it sends, and where.</strong> Those observations, to that one server's
    /// address and nowhere else, and only ever for instances belonging to the group that server
    /// declared it manages.</para>
    /// <para><strong>The machine's own timezone is supplied here</strong>, because VRChat's
    /// timestamps carry no offset at all and something has to say which instant <c>20:27:14</c>
    /// names. It is read from Windows, not asked of any server, and it is applied per server
    /// alongside that server's separately measured clock offset.</para>
    /// </remarks>
    private void Connect(ServerPairing pairing)
    {
        if (_engine is null || _state is null)
            return;

        var serverClock = new ServerClock(_clock);
        var buffer = new FileEventBuffer(QueuePath(pairing.ServerId), _clock);
        _buffers[pairing.ServerId] = buffer;

        var connection = new ServerConnection(
            pairing,
            buffer,
            new PresenceEventMapper(new LogTimestampConverter(), serverClock),
            serverClock,
            _transport!,
            _clock,
            ModbotVersion.Release,
            journal: _journal);

        _engine.Add(connection);
        _state.Connections.Add(connection);
    }

    /// <summary>
    /// Where one server's unsent observations wait.
    /// </summary>
    /// <remarks>
    /// The server id is the host name from the pairing token, so it is filtered down to characters
    /// a path can hold rather than trusted — a name is not a file name, and treating one as the
    /// other is how a stray character becomes a write somewhere unintended.
    /// </remarks>
    private string QueuePath(string serverId)
    {
        var safe = new string([.. serverId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '_')]);

        return Path.Combine(_directory, "queue", $"{safe}.jsonl");
    }

    /// <summary>
    /// Brings up the headset overlay, if this machine has one.
    /// </summary>
    /// <remarks>
    /// <para>No SteamVR is the ordinary case, not a fault: presence coverage comes from moderators
    /// reporting, and only some of them wear a headset. The loop runs either way -- it keeps the
    /// local cache warm and costs nothing when there is nothing to draw -- and the runtime simply
    /// reports that there is no headset to show it on.</para>
    /// <para>A failure to create the Direct3D surface is not allowed to take the client down with
    /// it. Reporting presence is the job that cannot be filled in later; the overlay is the one that
    /// can wait for a restart.</para>
    /// </remarks>
    private void StartOverlay()
    {
        try
        {
            _overlayHost = OverlayHost.Create(placement: _state?.Settings.Overlay);
            _overlayHost.KeepLastFrame = _state?.DebugMode is true;
            AttachOverlay();
        }
        catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException or NotSupportedException)
        {
            Log.Information(ex, "The overlay could not be set up on this machine; presence reporting is unaffected");
            _overlayHost = null;
            return;
        }

        _overlay = new OverlayDriver(
            _overlayHost, new HttpOverlayReadClient(_http!, _clock), _clock, listener: this, sockets: new ClientLiveSocketFactory());

        foreach (var connection in _state?.Connections ?? [])
            _overlay.Add(connection.Pairing, connection.ServerId);

        _overlayLoop.Tick += async (_, _) => await CrashGuard.RunAsync("drawing the overlay", OverlayTickAsync);
        _overlayLoop.Start();

        // A controller's doing goes to the drive loop (taps, scrolling) and to settings (where
        // the panel was left), so it is where it was left next time.
        _overlayHost.Tapped += target => _overlay?.Tap(target);
        _overlayHost.RosterScrolled += rows => _overlay?.ScrollRoster(rows);
        // Saved once a change has settled rather than on every tick of a drag or a held grip: a
        // panel being moved changes thirty times a second, and the file needs the last one.
        _overlayHost.PlacementChanged += placement =>
        {
            if (_state is null)
                return;

            _state.Settings = _state.Settings with { Overlay = placement };
            _placementSave.Stop();
            _placementSave.Start();
        };
        _placementSave.Tick += (_, _) =>
        {
            _placementSave.Stop();
            if (_state is null)
                return;

            if (!CompanionSettings.SaveOverlay(_settingsPath, _state.Settings.Overlay))
                Log.Warning("The panel's placement could not be saved to {Path}", _settingsPath);
        };

        _inputLoop.Tick += (_, _) => CrashGuard.Run(
            "reading the controllers",
            () => _overlayHost.PollInput(TimeSpan.FromMilliseconds(Environment.TickCount64)));
        _inputLoop.Start();
    }

    /// <summary>
    /// One turn of the overlay loop, never overlapping itself.
    /// </summary>
    /// <remarks>
    /// A turn holds a long poll open across many timer ticks, so without this guard the timer
    /// would stack requests on a machine that is also running a game.
    /// </remarks>
    /// <summary>How often the companion looks for a SteamVR that was not running last time.</summary>
    private static readonly TimeSpan OverlayAttachInterval = TimeSpan.FromSeconds(10);

    private DateTimeOffset _overlayAttachTriedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Attaches to SteamVR if it is running, and says so once. Never launches it: a program that
    /// starts with the computer must not start SteamVR too.
    /// </summary>
    private void AttachOverlay()
    {
        if (_overlayHost is null)
            return;

        _overlayAttachTriedAt = _clock.UtcNow;
        var before = _overlayHost.Status;
        var status = _overlayHost.Start();

        if (status.State == before.State && status.Detail == before.Detail)
            return;

        switch (status.State)
        {
            case OverlayRuntimeState.Running:
                _overlayAttachedAt = _clock.UtcNow;
                Log.Information("The overlay is attached: {Detail}", status.Detail);
                break;
            case OverlayRuntimeState.NoRuntime:
                Log.Information("No VR runtime on this machine, so no overlay: {Detail}", status.Detail);
                break;
            case OverlayRuntimeState.Refused:
                Log.Warning("The VR runtime refused the overlay: {Detail}", status.Detail);
                break;
            default:
                Log.Information("No VR runtime is running; the overlay will attach when one is: {Detail}", status.Detail);
                break;
        }
    }

    private async Task OverlayTickAsync()
    {
        if (_overlay is null || _overlayHost is null || _overlayTicking)
            return;

        _overlayTicking = true;
        try
        {
            // SteamVR closing detaches the overlay; a SteamVR started since the last look is picked
            // up here, a few seconds after the moderator starts it.
            var wasRunning = _overlayHost.Status.State is OverlayRuntimeState.Running;
            _overlayHost.Poll();
            if (wasRunning && _overlayHost.Status.State is not OverlayRuntimeState.Running)
            {
                _overlayAttachedAt = null;
                Log.Information("The VR runtime closed; the overlay has let go and will attach again when it is back: {Detail}", _overlayHost.Status.Detail);
            }

            if (_overlayHost.Status.State is OverlayRuntimeState.NotStarted
                && _clock.UtcNow - _overlayAttachTriedAt >= OverlayAttachInterval)
            {
                AttachOverlay();
            }

            // The instance the log reader last understood. The overlay follows the moderator: the
            // server that manages this instance is the only one it reads from or speaks for.
            _overlay.EnteredInstance(CurrentInstance);
            await _overlay.TickAsync();
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            _overlayTicking = false;
        }
    }

    /// <summary>
    /// Where the moderator is, as far as the log has said.
    /// </summary>
    /// <remarks>
    /// <para>Read from the engine rather than pushed into it, so there is exactly one answer and
    /// one place that decides it. Null means "not known", which covers VRChat not running, VRChat
    /// having stopped writing and being presumed gone, and the moderator standing in a public,
    /// friends-only or private instance — all of which correctly produce the idle screen and no
    /// contact with any server.</para>
    /// <para>Nothing is asked of a server to obtain it. It is the same parse of the same log lines
    /// the reporting half already made.</para>
    /// </remarks>
    public InstanceLocation? CurrentInstance => _engine?.CurrentInstance;

    /// <summary>
    /// The tray icon, which is present for the whole life of the process.
    /// </summary>
    /// <remarks>
    /// Not decoration. Silent, windowless, tray-less background software is scored as hostile by
    /// antivirus heuristics — correctly — and is also precisely the thing a moderator is being
    /// asked to trust this program not to be. The same decision satisfies both, which is a good
    /// sign that neither is theatre.
    /// </remarks>
    private void InstallTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var open = new NativeMenuItem("Open Modbot");
        open.Click += (_, _) => ShowWindow();

        var quit = new NativeMenuItem("Quit — stops reporting");
        quit.Click += (_, _) =>
        {
            // Quitting really does stop reporting: the log stops being read at this line, not when
            // the process eventually exits.
            _engineLoop.Stop();
            _overlayLoop.Stop();
            _inputLoop.Stop();
            _voiceLoop.Stop();
            _updates?.Stop();
            _inboxStop.Cancel();
            _backupStop.Cancel();
            _overlay?.Dispose();
            _overlayHost?.Dispose();
            _voice?.Dispose();
            desktop.Shutdown();
        };

        _tray = new TrayIcon
        {
            Icon = Brand.Icon(),
            ToolTipText = "Modbot: reporting presence for your groups",
            IsVisible = true,
            Menu = [open, quit],
        };

        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(Application.Current!, [_tray]);
    }

    /// <summary>
    /// Starts asking the release feed for newer versions, unless the moderator has turned that
    /// off. What arrives is downloaded and announced in the window; it is installed at the next
    /// start and never underneath a running session.
    /// </summary>
    private void StartUpdateChecks()
    {
        if (_state?.Settings.CheckForUpdates is not true)
        {
            Log.Information("Update checks are turned off in settings.json; this companion will not look for newer versions");
            return;
        }

        _updates = new Updates(_state);
        _updates.Start();
    }

    /// <summary>
    /// Makes this the copy that browser links reach.
    /// </summary>
    /// <remarks>
    /// Two steps. The registry key tells Windows that <c>modbot-companion://</c> opens this
    /// executable; the inbox is where the copy Windows then starts drops the link before exiting.
    /// Either can fail on a locked-down machine, and neither failing stops the client doing its
    /// job — the moderator pairs by pasting the token instead.
    /// </remarks>
    private void ListenForLinks()
    {
        if (OperatingSystem.IsWindows() && Environment.ProcessPath is { Length: > 0 } executable)
            UrlSchemeRegistration.Register(executable);

        _ = new PairingLinkInbox().ListenAsync(
            message => Dispatcher.UIThread.InvokeAsync(() => CrashGuard.RunAsync("handling a pairing link", () => HandleMessageAsync(message))),
            _inboxStop.Token);
    }

    /// <summary>
    /// What arrives from a second copy of this program, or from this one's own command line: a
    /// pairing link, or a request to show the window.
    /// </summary>
    /// <remarks>
    /// Anything that is not the show command is treated as a pairing link and checked as strictly
    /// as a pasted token — it came from a browser, through Windows, and none of that is trusted.
    /// </remarks>
    private async Task HandleMessageAsync(string message)
    {
        ShowWindow();

        if (string.Equals(message, ShowCommand, StringComparison.Ordinal))
            return;

        await PairAsync(message);
    }

    private void ShowWindow()
    {
        Window.Show();
        Window.WindowState = WindowState.Normal;
        Window.Activate();
    }

    private void Render()
    {
        if (_state is null)
            return;

        if (_engine is not null)
            _state.LogHealth = _engine.LogHealth;

        _state.Overlay = DescribeOverlay();
        _state.LiveWords = _overlay?.LiveWords() ?? _state.LiveWords;

        if (_voice is not null)
            _state.Voice = _voice.Status();

        if (_overlayHost is not null)
            _preview?.Refresh(_overlayHost);

        Window.Render(
            _state.Snapshot(),
            new MainWindowActions(
                TogglePause, Unpair, PairAsync, OpenPairingPageAsync, SetStartWithWindows, SetLogFolder,
                AttachSteamVr, ShowOverlayWindow, PinOverlaySample, PlaceOverlay, AnchorOverlay, SetVoice, TestVoice)
            {
                SetEventsFilters = SetEventsFilters,
            });
    }

    /// <summary>The overlay in the window's words: whether it is up, what it shows, how often it has drawn.</summary>
    private OverlayStatus DescribeOverlay()
    {
        if (_overlayHost is null)
            return OverlayStatus.None;

        if (_overlayHost.FramesDrawn != _overlayFramesSeen)
        {
            _overlayFramesSeen = _overlayHost.FramesDrawn;
            _overlayLastDrewAt = _clock.UtcNow;
        }

        var status = _overlayHost.Status;
        var screen = _overlayHost.Showing;
        var roster = screen.Roster;

        return new OverlayStatus(
            status.State is OverlayRuntimeState.Running,
            status.State switch
            {
                OverlayRuntimeState.Running => "attached",
                OverlayRuntimeState.NoRuntime => "SteamVR not installed",
                OverlayRuntimeState.Refused => "refused",
                _ => "SteamVR not running",
            },
            status.Detail ?? "",
            _overlayAttachedAt,
            _overlayHost.FramesDrawn,
            _overlayLastDrewAt,
            screen.GroupLabel ?? "Not in a group instance",
            roster.Value?.Members.Count ?? 0,
            roster.Describe(),
            screen.Alert is { } alert ? alert.DisplayName ?? alert.SubjectId : null,
            screen.Health,
            _overlay?.CurrentServer?.GroupLabel,
            _pinnedSample is { } sample ? OverlaySamples.Name(sample) : null,
            _overlayHost.Placement,
            _overlayHost.Holding switch
            {
                Modbot.Overlay.Interaction.Hand.Left => "left hand",
                Modbot.Overlay.Interaction.Hand.Right => "right hand",
                _ => null,
            });
    }

    /// <summary>The SteamVR page moving the panel: a size, an opacity, a curve, or back in front of the head.</summary>
    private void PlaceOverlay(OverlayPlacement placement)
    {
        if (_overlayHost is null)
            return;

        _overlayHost.Place(placement);
        Render();
    }

    /// <summary>The SteamVR page fixing the panel to the head, a hand or the room.</summary>
    private void AnchorOverlay(OverlayAnchor anchor)
    {
        if (_overlayHost is null)
            return;

        _overlayHost.Anchor(anchor);
        Render();
    }

    /// <summary>The Debug page's "Attach to SteamVR now", and the SteamVR page's.</summary>
    private void AttachSteamVr()
    {
        AttachOverlay();
        Render();
    }

    /// <summary>Opens, or brings back, the window that shows the overlay's last frame.</summary>
    private void ShowOverlayWindow()
    {
        if (_overlayHost is null)
            return;

        if (_preview is null)
        {
            _preview = new OverlayPreviewWindow();
            _preview.Closed += (_, _) => _preview = null;
            _preview.Refresh(_overlayHost);
            _preview.Show();
        }

        _preview.Activate();
    }

    /// <summary>Pins a sample screen into the overlay, or, with null, lets the live screen back.</summary>
    private void PinOverlaySample(OverlaySample? sample)
    {
        if (_overlayHost is null)
            return;

        _pinnedSample = sample;
        _overlayHost.Pinned = sample is { } chosen ? OverlaySamples.Build(chosen) : null;
        Render();
    }

    private void TogglePause(string serverId)
    {
        if (_state?.Connections.FirstOrDefault(c => c.ServerId == serverId) is not { } connection)
            return;

        // Immediate. Pausing stops Modbot observing what you do from now on; it does not save it
        // up to report when you resume.
        connection.IsPaused = !connection.IsPaused;
        Render();
    }

    private void Unpair(string serverId)
    {
        if (_state is null || _pairing is null)
            return;

        _pairing.Unpair(serverId);
        Disconnect(serverId);
        _state.UnusablePairings.RemoveAll(p => p.ServerId == serverId);
        Render();
    }

    /// <summary>
    /// Takes one server out of the reporting loop: its connection, its queue file and its place in
    /// the overlay all go together, so nothing of that group's data is left behind.
    /// </summary>
    private void Disconnect(string serverId)
    {
        if (_state is null)
            return;

        _overlay?.Remove(serverId);

        if (_state.Connections.FirstOrDefault(c => c.ServerId == serverId) is { } connection)
        {
            _state.Connections.Remove(connection);

            if (_engine is not null && _buffers.TryGetValue(serverId, out var buffer))
                _engine.Remove(connection, buffer);

            _buffers.Remove(serverId);
        }
    }

    /// <summary>
    /// Pairs from a pairing link or a pasted token, and says how it went in the pairing card.
    /// </summary>
    /// <remarks>
    /// The same path whichever way the token arrived, and one request per token — a link is
    /// single-use, so nothing here retries. Pairing a server this client already has replaces the
    /// old pairing rather than adding a second one: the new token is the one the operator just
    /// issued, and two connections to one server would report everything twice.
    /// </remarks>
    private async Task<PairingAttemptResult> PairAsync(string linkOrToken)
    {
        if (_pairing is null || _state is null)
            return new PairingAttemptResult(false, "Not ready yet.");

        _state.LastPairing = new PairingNotice(PairingNoticeKind.Working, "Pairing…");
        Render();

        var result = await _pairing.PairAsync(linkOrToken);

        // A server paired mid-session starts being reported to and becomes visible to the overlay
        // immediately, so a moderator who pairs while already standing in that group's instance
        // does not have to restart to be covered or to see its roster.
        if (result is { Succeeded: true, Pairing: { } pairing })
        {
            Disconnect(pairing.ServerId);
            _state.UnusablePairings.RemoveAll(p => p.ServerId == pairing.ServerId);
            Connect(pairing);
            _overlay?.Add(pairing, pairing.ServerId);
        }

        _state.LastPairing = new PairingNotice(
            result.Succeeded ? PairingNoticeKind.Succeeded : PairingNoticeKind.Failed,
            result.Message);
        Render();

        return result;
    }

    /// <summary>
    /// Opens the pairing page in the moderator's browser.
    /// </summary>
    /// <remarks>
    /// Through the UI toolkit's own "open this link" facility, which hands the address to Windows
    /// the same way clicking a link in any program does. The client does not start, inspect or
    /// attach to any process itself. If Windows cannot open a browser, the address is shown so the
    /// moderator can type it.
    /// </remarks>
    private async Task OpenPairingPageAsync()
    {
        if (_state is null)
            return;

        var page = _state.Settings.PairingPage;
        var opened = await Window.Launcher.LaunchUriAsync(page);

        _state.LastPairing = opened
            ? new PairingNotice(
                PairingNoticeKind.Working,
                "Your browser is open. Sign in there, press \"Open in Modbot\", and come back here.")
            : new PairingNotice(
                PairingNoticeKind.Failed,
                $"Could not open your browser. Open {page} yourself, sign in, and press \"Open in Modbot\" "
                + "— or copy the pairing token from that page and paste it below.");
        Render();
    }
}

internal static class Program
{
    /// <summary>
    /// Keeps the client to one running copy per Windows account. Named under <c>Local\</c> so two
    /// people signed in to the same PC each get their own.
    /// </summary>
    private const string SingleInstanceName = @"Local\Modbot.Companion";

    [STAThread]
    public static int Main(string[] args)
    {
        // First, before the log or the single-instance check: the installer starts this exe with
        // a flag while installing, updating or uninstalling, and this does what the flag asks and
        // exits. An ordinary start comes straight back.
        Updates.RunInstallerHooks(args);

        // Windows starts a fresh copy of this program to deliver a modbot-companion:// link. The
        // link is the first argument that looks like one; anything else on the command line is
        // Avalonia's business.
        var link = args.FirstOrDefault(PairingToken.LooksLikeLink);
        var startHidden = StartWithWindows.StartsHidden(args);

        CompanionLog.Start(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        CrashGuard.Install();

        using var single = new Mutex(initiallyOwned: true, SingleInstanceName, out var firstCopy);
        if (!firstCopy)
        {
            // Windows starting a second copy at sign-in wants nothing from the first one.
            if (startHidden && link is null)
                return 0;

            Log.Information("Another copy of the companion is running; handing it {What} and leaving",
                link is null ? "a request to show its window" : "the pairing link");
            // Another copy owns the tray icon and the log. Hand it the link -- or, with no link,
            // ask it to show its window, which is what somebody double-clicking the icon again
            // wanted -- and leave. A few tries, because the other copy may still be starting.
            var message = link ?? CompanionHost.ShowCommand;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (PairingLinkInbox.TrySendAsync(message, timeout: TimeSpan.FromSeconds(1)).GetAwaiter().GetResult())
                    break;
            }

            return 0;
        }

        // Only the copy that will own the tray icon installs a waiting update, and only here,
        // before the window exists: nothing is being recorded yet, so the restart costs nothing,
        // and a second copy started by a browser link never swaps the files under the first.
        Updates.InstallDownloadedUpdate(args);

        ModbotCompanionApp.StartupMessage = link;
        ModbotCompanionApp.StartHidden = startHidden && link is null;

        try
        {
            OverlayHost.ConfigureAvalonia<ModbotCompanionApp>()
                .UsePlatformDetect()
                .ConfigureFonts(Brand.RegisterFonts)
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Reported here, once, rather than rethrown into the runtime's own crash dialog: the
            // moderator gets one box that names the log file, not two that name nothing.
            CrashGuard.Report(ex, "while starting", fatal: true);
            return 1;
        }
        finally
        {
            Log.Information("Modbot Companion exiting");
            CompanionLog.Stop();
        }

        return 0;
    }
}
