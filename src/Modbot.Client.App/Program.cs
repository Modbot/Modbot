using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Modbot.Client.Pairing;
using Modbot.Client.Presentation;
using Modbot.Core.Time;
using Modbot.Overlay;

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

    private ClientAppState? _state;
    private PairingCoordinator? _pairing;
    private TrayIcon? _tray;
    private HttpClient? _http;

    public MainWindow Window { get; } = new();

    public void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var directory = Path.Combine(appData, "Modbot");

        var journal = new Journal.SentJournal(Path.Combine(directory, "sent.jsonl"), _clock);
        _state = new ClientAppState(_clock, journal);

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

        // A pairing whose token will not decrypt is shown by name rather than retried or hidden.
        // A token encrypted for a different Windows account is DPAPI working, not failing, and the
        // honest answer is "pair this one again".
        foreach (var pairing in store.Load())
        {
            if (!pairing.IsUsable)
                _state.UnusablePairings.Add(pairing);
        }

        InstallTray(desktop);

        _refresh.Tick += (_, _) => Render();
        _refresh.Start();
        Render();
    }

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
        quit.Click += (_, _) => desktop.Shutdown();

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

        // The token, the queued observations and the connection all go together. Unpairing leaves
        // nothing of that group's data behind, and needs nothing from its operator.
        if (_state.Connections.FirstOrDefault(c => c.ServerId == serverId) is { } connection)
            _state.Connections.Remove(connection);

        _state.UnusablePairings.RemoveAll(p => p.ServerId == serverId);
        Render();
    }

    private async Task<PairingAttemptResult> PairAsync(string address, string code, string deviceName)
    {
        if (_pairing is null)
            return new PairingAttemptResult(false, "Not ready yet.");

        var result = await _pairing.PairAsync(address, code, deviceName);
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
