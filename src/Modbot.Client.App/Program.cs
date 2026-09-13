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
/// been sent. Nothing else on the machine is touched.</para>
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
/// </remarks>
internal sealed class ModbotClientApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the window leaves the client reporting from the tray, which is the point of
            // it. Quitting is a deliberate act from the tray menu -- and quitting really does stop
            // reporting, rather than minimising to somewhere less visible.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = Host.Window;
            Host.Start(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static ClientHost Host { get; } = new();
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

    private string _directory = string.Empty;
    private ClientAppState? _state;
    private PairingCoordinator? _pairing;
    private Journal.SentJournal? _journal;
    private ClientEngine? _engine;
    private TrayIcon? _tray;
    private HttpClient? _http;
    private IIngestTransport? _transport;
    private OverlayDriver? _overlay;
    private OverlayHost? _overlayHost;
    private bool _overlayTicking;
    private bool _engineTicking;

    public MainWindow Window { get; } = new();

    public void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _directory = Path.Combine(appData, "Modbot");

        _journal = new Journal.SentJournal(Path.Combine(_directory, "sent.jsonl"), _clock);
        _state = new ClientAppState(_clock, _journal);

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

        _refresh.Tick += (_, _) => Render();
        _refresh.Start();
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
        var observer = new PresenceObserver(new VRChatLogTail(VRChatLogTail.DefaultDirectory), _clock);
        _engine = new ClientEngine(observer, _clock, timeProbe: new HttpServerTimeProbe(_http!, _clock));

        _engineLoop.Tick += async (_, _) => await EngineTickAsync();
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
            await _engine.TickAsync();
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            _engineTicking = false;
        }
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
    /// The server id is the host name the moderator typed, so it is filtered down to characters a
    /// path can hold rather than trusted — a name is not a file name, and treating one as the other
    /// is how a typo becomes a write somewhere unintended.
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
    /// it. Reporting presence is the job that cannot be backfilled; the overlay is the one that
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

        _overlayLoop.Tick += async (_, _) => await OverlayTickAsync();
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
            new MainWindowActions(TogglePause, Unpair, PairAsync));
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
        _overlay?.Remove(serverId);

        // The token, the queued observations and the connection all go together. Unpairing leaves
        // nothing of that group's data behind, and needs nothing from its operator.
        if (_state.Connections.FirstOrDefault(c => c.ServerId == serverId) is { } connection)
        {
            _state.Connections.Remove(connection);

            if (_engine is not null && _buffers.TryGetValue(serverId, out var buffer))
                _engine.Remove(connection, buffer);

            _buffers.Remove(serverId);
        }

        _state.UnusablePairings.RemoveAll(p => p.ServerId == serverId);
        Render();
    }

    private async Task<PairingAttemptResult> PairAsync(string address, string code, string deviceName)
    {
        if (_pairing is null)
            return new PairingAttemptResult(false, "Not ready yet.");

        var result = await _pairing.PairAsync(address, code, deviceName);

        // A server paired mid-session starts being reported to and becomes visible to the overlay
        // immediately, so a moderator who pairs while already standing in that group's instance
        // does not have to restart to be covered or to see its roster.
        if (result is { Succeeded: true, Pairing: { } pairing })
        {
            Connect(pairing);
            _overlay?.Add(pairing, pairing.ServerId);
        }

        Render();

        return result;
    }
}

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
        => OverlayHost.ConfigureAvalonia<ModbotClientApp>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
}
