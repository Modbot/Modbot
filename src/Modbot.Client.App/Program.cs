using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Modbot.Client.Ingest;
using Modbot.Client.Instances;
using Modbot.Client.LogReading;
using Modbot.Client.Pairing;
using Modbot.Client.Pipeline;
using Modbot.Client.Presentation;
using Modbot.Client.Overlay;
using Modbot.Client.Time;
using Modbot.Core;
using Modbot.Core.Time;
using Modbot.Overlay;
using Modbot.Overlay.Driving;
using Modbot.Overlay.OpenVr;
using Serilog;

namespace Modbot.Client.App;

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
/// been sent. Plus one registry key under your own account saying that <c>modbot-client://</c>
/// links open this program, which is how pairing from the browser reaches it. Nothing else on the
/// machine is touched.</para>
/// <para><strong>What leaves the machine.</strong> Presence observations, to the Modbot servers
/// you paired with and to nowhere else — and only for instances belonging to the group each of
/// those servers manages. Never a raw log line, never anything about your private, friends-only or
/// public VRChat use, and never chat, screenshots, keystrokes, your friends list or a list of your
/// processes.</para>
/// <para><strong>It never captures the screen.</strong> Not the desktop, not a window, not
/// VRChat's screenshot folder, not any other folder. Attaching evidence to a moderation case is a
/// deliberate human action taken in Modbot's web interface, in a browser, by choosing a file —
/// which is why this program needs no such capability and does not have one.</para>
/// <para><strong>It is always visible while it runs.</strong> Closing the window leaves a tray
/// icon; the program never becomes invisible, and pausing stops transmission immediately and shows
/// that it has.</para>
/// <para><strong>It runs once.</strong> Starting it again — which is what Windows does when a
/// browser opens a <c>modbot-client://</c> link — hands the link to the copy already running and
/// exits. One tray icon, one log reader, one set of queues.</para>
/// </remarks>
internal sealed class ModbotClientApp : Application
{
    /// <summary>
    /// What this process was started with, if anything: a pairing link from the browser, or a
    /// request to show the window. Handled once the host is up.
    /// </summary>
    internal static string? StartupMessage { get; set; }

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
                Host = new ClientHost();
                desktop.MainWindow = Host.Window;
                Host.Start(desktop, StartupMessage);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "The client could not start");
                throw;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static ClientHost? Host { get; private set; }
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
internal sealed class ClientHost
{
    /// <summary>
    /// What a second copy sends when it was started with no link: the person double-clicked the
    /// icon again, and wants the window.
    /// </summary>
    internal const string ShowCommand = "show";

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
    private ClientAppState? _state;
    private PairingCoordinator? _pairing;
    private Journal.SentJournal? _journal;
    private ClientEngine? _engine;
    private VRChatLogTail? _tail;
    private string? _lastLoggedFile;
    private long _lastLoggedLines;
    private int _consecutiveTickFailures;
    private TrayIcon? _tray;
    private HttpClient? _http;
    private IIngestTransport? _transport;
    private OverlayDriver? _overlay;
    private OverlayHost? _overlayHost;
    private bool _overlayTicking;
    private bool _engineTicking;

    public MainWindow Window { get; } = new();

    public void Start(IClassicDesktopStyleApplicationLifetime desktop, string? startupMessage)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _directory = Path.Combine(appData, "Modbot");

        _journal = new Journal.SentJournal(Path.Combine(_directory, "sent.jsonl"), _clock);
        _state = new ClientAppState(_clock, _journal, ClientSettings.Load(ClientSettings.DefaultPath(appData)));

        // Only the token is encrypted; the rest of the file is left readable on purpose, so a
        // suspicious moderator can open it and see exactly which servers this client talks to.
        var store = new DpapiPairingStore(
            DpapiPairingStore.DefaultPath(appData),
            new DpapiSecretProtector());

        // One client, kept for the life of the process. A disposed-per-use HttpClient exhausts
        // sockets under any real traffic, and this one is also the single place pairing requests
        // leave from.
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _pairing = new PairingCoordinator(new HttpPairingClient(_http), store);
        _transport = new HttpIngestTransport(_http);

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

        _refresh.Tick += (_, _) => CrashGuard.Run("refreshing the window", Render);
        _refresh.Start();
        Render();

        // Started by a browser link: the whole reason this process exists is to pair, so do that
        // now, in front of the moderator, rather than sitting in the tray waiting to be found.
        if (startupMessage is not null)
            _ = CrashGuard.RunAsync("handling the pairing link", () => HandleMessageAsync(startupMessage));
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
        _tail = new VRChatLogTail(VRChatLogTail.DefaultDirectory);
        Log.Information("Watching VRChat's log folder {Directory}", VRChatLogTail.DefaultDirectory);

        var observer = new PresenceObserver(_tail, _clock);
        _engine = new ClientEngine(observer, _clock, timeProbe: new HttpServerTimeProbe(_http!, _clock));

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
                        + "Restart the client once the cause is fixed. Last error: " + ex.Message, ex),
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
            _overlayHost = OverlayHost.Create();
            _overlayHost.Start();
        }
        catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException or NotSupportedException)
        {
            _overlayHost = null;
            return;
        }

        _overlay = new OverlayDriver(_overlayHost, new HttpOverlayReadClient(_http!, _clock), _clock);

        foreach (var connection in _state?.Connections ?? [])
            _overlay.Add(connection.Pairing, connection.ServerId);

        _overlayLoop.Tick += async (_, _) => await CrashGuard.RunAsync("drawing the overlay", OverlayTickAsync);
        _overlayLoop.Start();
    }

    /// <summary>
    /// One turn of the overlay loop, never overlapping itself.
    /// </summary>
    /// <remarks>
    /// A turn holds a long poll open across many timer ticks, so without this guard the timer
    /// would stack requests on a machine that is also running a game.
    /// </remarks>
    private async Task OverlayTickAsync()
    {
        if (_overlay is null || _overlayTicking)
            return;

        _overlayTicking = true;
        try
        {
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
            _inboxStop.Cancel();
            _overlay?.Dispose();
            _overlayHost?.Dispose();
            desktop.Shutdown();
        };

        _tray = new TrayIcon
        {
            ToolTipText = "Modbot — reporting presence for your groups",
            IsVisible = true,
            Menu = [open, quit],
        };

        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(Application.Current!, [_tray]);
    }

    /// <summary>
    /// Makes this the copy that browser links reach.
    /// </summary>
    /// <remarks>
    /// Two steps. The registry key tells Windows that <c>modbot-client://</c> opens this
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

        Window.Render(
            _state.Snapshot(),
            new MainWindowActions(TogglePause, Unpair, PairAsync, OpenPairingPageAsync));
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
    private const string SingleInstanceName = @"Local\Modbot.Client";

    [STAThread]
    public static int Main(string[] args)
    {
        // Windows starts a fresh copy of this program to deliver a modbot-client:// link. The
        // link is the first argument that looks like one; anything else on the command line is
        // Avalonia's business.
        var link = args.FirstOrDefault(PairingToken.LooksLikeLink);

        ClientLog.Start(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        CrashGuard.Install();

        using var single = new Mutex(initiallyOwned: true, SingleInstanceName, out var firstCopy);
        if (!firstCopy)
        {
            Log.Information("Another copy of the client is running; handing it {What} and leaving",
                link is null ? "a request to show its window" : "the pairing link");
            // Another copy owns the tray icon and the log. Hand it the link -- or, with no link,
            // ask it to show its window, which is what somebody double-clicking the icon again
            // wanted -- and leave. A few tries, because the other copy may still be starting.
            var message = link ?? ClientHost.ShowCommand;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (PairingLinkInbox.TrySendAsync(message, timeout: TimeSpan.FromSeconds(1)).GetAwaiter().GetResult())
                    break;
            }

            return 0;
        }

        ModbotClientApp.StartupMessage = link;

        try
        {
            OverlayHost.ConfigureAvalonia<ModbotClientApp>()
                .UsePlatformDetect()
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
            Log.Information("Modbot client exiting");
            ClientLog.Stop();
        }

        return 0;
    }
}
