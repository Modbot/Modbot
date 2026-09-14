using System.Reflection;
using Avalonia.Threading;
using Modbot.Client.Presentation;
using Serilog;
using Velopack;
using Velopack.Sources;

namespace Modbot.Client.App;

/// <summary>
/// How an installed copy of the client gets newer versions of itself.
/// </summary>
/// <remarks>
/// <para><strong>What this reads and writes.</strong> The installer's own folder,
/// <c>%LOCALAPPDATA%\Modbot</c>: the installed program files, and a <c>packages</c> folder where
/// a downloaded update waits until it is installed. Nothing under <c>%APPDATA%\Modbot</c> — the
/// pairings, the queues, the record of what has been sent, the logs — is touched by an update,
/// which is why updating keeps every pairing.</para>
/// <para><strong>What leaves the machine.</strong> A request to the release feed (by default the
/// project's GitHub releases) asking what the newest version is, and then, if there is one, the
/// download. GitHub sees your IP address, as it does for any download. Nothing about you, your
/// pairings or VRChat is sent, and the request does not identify this install.</para>
/// <para><strong>It never restarts Modbot while Modbot is running.</strong> A newer version is
/// downloaded in the background and the window says so. It is installed the next time Modbot
/// starts, whenever that is — quitting from the tray icon and opening Modbot again is enough.
/// Nothing is swapped underneath a running session that may be recording presence.</para>
/// <para><strong>Turning it off.</strong> <c>"checkForUpdates": false</c> in
/// <c>%APPDATA%\Modbot\settings.json</c> stops the client asking the feed at all. A copy run from
/// source or from a plain folder is not an installed copy and never checks.</para>
/// <para><strong>This is the one file in the client that talks to Velopack</strong>, the
/// installer and updater the client ships with, and the source guard holds it to being the only
/// one. Velopack is also the one thing in the client that starts another program: its own
/// <c>Update.exe</c>, from Modbot's own install folder, to swap the files while Modbot is not
/// running. The client itself starts, inspects and attaches to nothing.</para>
/// </remarks>
internal sealed class Updates
{
    /// <summary>
    /// Long enough after start that the log reader and the window are up first; an update check
    /// is the least urgent thing the client does at launch.
    /// </summary>
    private static readonly TimeSpan FirstCheckAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A client that runs for days with VRChat should still learn about a release within the day.
    /// Every few hours is that, without leaning on GitHub's unauthenticated rate limit.
    /// </summary>
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(4);

    /// <summary>The build property the release workflow sets; see Modbot.Client.App.csproj.</summary>
    private const string FeedMetadataKey = "ModbotUpdateFeed";

    private readonly ClientAppState _state;
    private readonly DispatcherTimer _timer = new() { Interval = FirstCheckAfter };
    private readonly UpdateManager? _manager;
    private bool _checking;

    public Updates(ClientAppState state)
    {
        _state = state;
        _manager = CreateManager();
    }

    /// <summary>
    /// Where updates come from. Baked in by the release build so an installed client and the
    /// workflow that published it agree, with the project's own repository as the default.
    /// </summary>
    public static string Feed { get; } =
        typeof(Updates).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == FeedMetadataKey)?.Value
        ?? "https://github.com/binn/Modbot";

    /// <summary>
    /// Velopack's installer hooks. The first thing <c>Main</c> does, before the log, the
    /// single-instance check or anything else.
    /// </summary>
    /// <remarks>
    /// The installer starts this exe with a flag while installing, updating or uninstalling; this
    /// call does what that flag asks and exits the process. An ordinary start returns straight
    /// away. Applying a downloaded update at start is turned off here and done by
    /// <see cref="InstallDownloadedUpdate"/> instead, once this copy knows it is the only one —
    /// Velopack's default would also fire in the second copy a browser link starts, and swap the
    /// files under the copy that is running.
    /// </remarks>
    public static void RunInstallerHooks(string[] args)
        => VelopackApp.Build()
            .SetArgs(args)
            .SetAutoApplyOnStartup(false)
            .Run();

    /// <summary>
    /// Installs an update that was downloaded on an earlier run, and reopens the client. Called
    /// once this copy owns the single-instance mutex and before the window exists, so nothing is
    /// being recorded yet and the restart costs nothing. Returns at once when there is nothing
    /// waiting; does not return when there is.
    /// </summary>
    public static void InstallDownloadedUpdate(string[] args)
    {
        var manager = CreateManager();
        if (manager is null || !manager.IsInstalled || manager.UpdatePendingRestart is not { } ready)
            return;

        Log.Information(
            "Installing Modbot {Version}, downloaded earlier, before starting. The client reopens when it is done",
            ready.Version);
        ClientLog.Stop();

        // Exits this process, swaps the files, and starts the new version with the same
        // arguments -- so a pairing link that started this copy still arrives.
        manager.ApplyUpdatesAndRestart(ready, args);
    }

    /// <summary>Starts checking: once shortly after launch, then every few hours.</summary>
    public void Start()
    {
        if (_manager is null)
            return;

        if (!_manager.IsInstalled)
        {
            Log.Information("Not an installed copy of the client (run from source or a plain folder), so not checking for updates");
            return;
        }

        Log.Information("Installed as Modbot {Version}; checking {Feed} for newer versions every {Hours} hours",
            _manager.CurrentVersion, Feed, CheckEvery.TotalHours);

        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = CheckEvery;
            await CheckAsync();
        };
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    /// <summary>
    /// One check, never overlapping itself. A failure is a log line and a retry a few hours
    /// later, never a dialog: a feed that is unreachable, private or empty is an ordinary
    /// condition for a client that works offline and must not interrupt anybody.
    /// </summary>
    private async Task CheckAsync()
    {
        if (_manager is null || _checking)
            return;

        _checking = true;
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null)
            {
                Log.Debug("Checked for updates: {Version} is the newest", _manager.CurrentVersion);
                return;
            }

            var version = update.TargetFullRelease.Version.ToString();
            if (string.Equals(_state.UpdateReady, version, StringComparison.Ordinal))
                return;

            Log.Information("Modbot {Version} is available; downloading it in the background", version);
            await _manager.DownloadUpdatesAsync(update);

            _state.UpdateReady = version;
            Log.Information("Modbot {Version} is downloaded and will be installed the next time Modbot starts", version);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not check for updates at {Feed}; trying again in {Hours} hours", Feed, CheckEvery.TotalHours);
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>
    /// GitHub releases when the feed is a GitHub repository, a plain folder of release files at
    /// an HTTPS address otherwise — the shape a group mirroring the feed would host.
    /// </summary>
    private static UpdateManager? CreateManager()
    {
        try
        {
            IUpdateSource source = Uri.TryCreate(Feed, UriKind.Absolute, out var uri)
                                   && uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase)
                ? new GithubSource(Feed, accessToken: null, prerelease: false)
                : new SimpleWebSource(Feed);

            return new UpdateManager(source);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The update feed {Feed} could not be used, so this client will not check for updates", Feed);
            return null;
        }
    }
}
