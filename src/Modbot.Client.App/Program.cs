using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Modbot.Client.Ingest;
using Modbot.Client.Journal;
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
/// <para><strong>It is always visible while it runs.</strong> The window can be closed to the tray
/// but the program never becomes invisible, and pausing stops transmission immediately and shows
/// that it has.</para>
/// </remarks>
internal sealed class ModbotClientApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // ShutdownMode matters here: closing the window leaves the client reporting from the
            // tray, which is the point of it. Quitting is a deliberate act from the tray menu.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = Host.Window;
            Host.Start();
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
/// this type owns only what the moderator sees. Keeping the two apart is what lets the reading and
/// reporting half stay a small library that can be audited without reading any UI code.
/// </remarks>
internal sealed class ClientHost
{
    private readonly IModbotClock _clock = new SystemModbotClock();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };

    private ClientAppState? _state;

    public MainWindow Window { get; } = new();

    public void Start()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Modbot");

        var journal = new SentJournal(Path.Combine(directory, "sent.jsonl"), _clock);
        _state = new ClientAppState(_clock, journal);

        // Pairings are loaded, and any that cannot be decrypted are shown by name rather than
        // being retried or hidden. A token encrypted for a different Windows account is DPAPI
        // working, not failing, and the honest answer is "pair this one again".
        var store = new DpapiPairingStore(
            DpapiPairingStore.DefaultPath(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            new DpapiSecretProtector());

        foreach (var pairing in store.Load())
        {
            if (!pairing.IsUsable)
                _state.UnusablePairings.Add(pairing);
        }

        _refresh.Tick += (_, _) => Render();
        _refresh.Start();
        Render();
    }

    private void Render()
    {
        if (_state is null)
            return;

        Window.Render(_state.Snapshot(), TogglePause);
    }

    private void TogglePause(string serverId)
    {
        if (_state?.Connections.FirstOrDefault(c => c.ServerId == serverId) is not { } connection)
            return;

        // Immediate, and the effect is visible on the next refresh. Pausing stops Modbot reporting
        // what you do from now on; it does not save it up to report later.
        connection.IsPaused = !connection.IsPaused;
        Render();
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
