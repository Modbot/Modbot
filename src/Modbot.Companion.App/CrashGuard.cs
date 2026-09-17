using System.Runtime.InteropServices;
using Avalonia.Threading;
using Serilog;

namespace Modbot.Companion.App;

/// <summary>
/// What happens when something in the client throws and nothing was there to catch it: the
/// error is written to the client log in full, and a plain message box tells the moderator that
/// something went wrong and where to look. Nothing about the error leaves the machine.
/// </summary>
/// <remarks>
/// <para>
/// A windowed program has no console, so an unhandled exception used to look like the tray icon
/// disappearing. Three doors are covered here: the UI thread's dispatcher (timer ticks, button
/// clicks, anything posted to the window), the app domain (threads nobody awaits -- fatal, the
/// process ends after the handler), and tasks nobody awaited (not fatal; logged). On top of that,
/// every timer tick and fire-and-forget call in the client goes through <see cref="Run"/> or
/// <see cref="RunAsync"/>, so an error in one of them is reported and the loop carries on.
/// </para>
/// <para>
/// The message box is Windows' own, called directly, because it works even when Avalonia's UI
/// thread is the thing that just died. Non-fatal boxes are rate limited to one a minute: a timer
/// that throws every second would otherwise bury the desktop in dialogs.
/// </para>
/// </remarks>
internal static class CrashGuard
{
    private const uint MessageBoxOk = 0x00000000;
    private const uint MessageBoxIconError = 0x00000010;
    private const uint MessageBoxIconWarning = 0x00000030;
    private const uint MessageBoxTopmost = 0x00040000;

    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMinutes(1);
    private static readonly object Gate = new();
    private static long _lastNonFatalBoxAt = long.MinValue;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    /// <summary>
    /// Hooks the two doors that exist before the UI does. Call once, first thing in Main.
    /// </summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report(e.ExceptionObject as Exception ?? new Exception("Unknown error"), "in the background", fatal: true);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "A background task failed and nobody was waiting for it");
            e.SetObserved();
        };

    }

    /// <summary>
    /// Hooks the UI thread's door. Call once Avalonia has initialised, and not a moment sooner:
    /// touching <see cref="Dispatcher.UIThread"/> before the platform is chosen creates a
    /// dispatcher with a stand-in behind it, and the real main loop then refuses to start with
    /// "Operation is not supported on this platform" -- the client's own log caught exactly that.
    /// </summary>
    public static void InstallForUi()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            // Handled: the window and the tray keep working. Whatever threw has been logged and
            // announced; killing the whole client over it would lose the presence it is recording.
            Report(e.Exception, "in the window", fatal: false);
            e.Handled = true;
        };
    }

    /// <summary>Runs one step; if it throws, reports and returns instead of propagating.</summary>
    public static void Run(string doing, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Report(ex, doing, fatal: false);
        }
    }

    /// <summary>The async twin of <see cref="Run"/>, for timer ticks and fire-and-forget work.</summary>
    public static async Task RunAsync(string doing, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Shutting down; not an error.
        }
        catch (Exception ex)
        {
            Report(ex, doing, fatal: false);
        }
    }

    /// <summary>
    /// Writes the error to the log and shows the moderator a message box saying what happened,
    /// whether the client is still running, and where the details are.
    /// </summary>
    public static void Report(Exception ex, string doing, bool fatal)
    {
        if (fatal)
            Log.Fatal(ex, "The client is stopping because of an error {Doing}", doing);
        else
            Log.Error(ex, "An error {Doing}; the client is still running", doing);

        if (!fatal)
        {
            lock (Gate)
            {
                // Uptime, not the clock: this is a spacing between two dialogs, and nothing else
                // in the client reads wall time directly.
                var now = Environment.TickCount64;
                if (now - _lastNonFatalBoxAt < QuietPeriod.TotalMilliseconds)
                    return;

                _lastNonFatalBoxAt = now;
            }
        }

        var text =
            (fatal
                ? "Modbot ran into a problem it could not recover from and has to close.\n\n"
                : "Modbot ran into a problem " + doing + ". It is still running, but that part may not be working.\n\n")
            + "What happened:\n" + ex.GetType().Name + ": " + ex.Message + "\n\n"
            + "The full details are in the client's log:\n" + (ClientLog.Folder ?? "%APPDATA%\\Modbot\\logs")
            + "\n\nPlease send that log file when you report this.";

        if (fatal)
            Log.CloseAndFlush();

        if (OperatingSystem.IsWindows())
        {
            MessageBoxW(
                IntPtr.Zero,
                text,
                fatal ? "Modbot has stopped" : "Modbot ran into a problem",
                MessageBoxOk | (fatal ? MessageBoxIconError : MessageBoxIconWarning) | MessageBoxTopmost);
        }
    }
}
