using System.Runtime.InteropServices;
using Modbot.Core.Configuration;
using Serilog;

namespace Modbot.Server.Startup;

/// <summary>
/// The last thing that runs when the process is about to die for a reason nothing caught.
/// </summary>
/// <remarks>
/// <para>
/// An exception thrown on a thread nobody is awaiting -- a timer callback, a background loop that
/// lost its try -- does not reach the <c>catch</c> around the host. .NET ends the process for it,
/// and the log sinks are still holding whatever they had not written yet: the file sink buffers,
/// and the database sink batches. So the one line that says why is the line that gets lost.
/// </para>
/// <para>
/// This writes that line and then flushes everything, which is all that can be done from inside a
/// process that is going away. <strong>It is not a safety net.</strong> The process still dies; it
/// dies having said why.
/// </para>
/// <para>
/// It cannot help with a fatal runtime error at all -- a bad memory access ends the process
/// without running anything, managed or otherwise. <see cref="CrashDumps"/> is the answer to
/// those, and it is the runtime's to write, not Modbot's.
/// </para>
/// <para>
/// What it can still do for a death it cannot explain is say what kind of death it was not. See
/// <see cref="WatchStopSignals"/>.
/// </para>
/// </remarks>
public static class CrashGuard
{
    // Held for the life of the process. A PosixSignalRegistration stops listening when it is
    // collected, and a registration nobody keeps is collected at the first convenient moment --
    // which is to say, long before the signal it was made for arrives.
    private static readonly List<IDisposable> Signals = [];

    public static void Watch()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var problem = e.ExceptionObject as Exception;

            if (problem is not null)
                Log.Fatal(problem, "Modbot is stopping: nothing handled this");
            else
                Log.Fatal("Modbot is stopping: nothing handled {Problem}", e.ExceptionObject);

            // Only when the process is really going. Flushing closes the sinks, and closing them
            // while Modbot carries on would lose everything written afterwards.
            if (e.IsTerminating)
                Log.CloseAndFlush();
        };

        // A task nobody awaited, whose exception was therefore never looked at. It does not end
        // the process on .NET, so this is a record rather than a farewell -- but a background loop
        // that has quietly stopped doing its job looks like nothing at all without it.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "A background task failed and nobody was waiting for it");
            e.SetObserved();
        };

        WatchStopSignals();
    }

    /// <summary>
    /// Says, in one line, that the host asked Modbot to stop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not about shutting down -- the host already does that, and does it properly. It is
    /// about telling two deaths apart afterwards, from the log alone.
    /// </para>
    /// <para>
    /// A process that is <em>asked</em> to go gets one of these signals first, and the shutdown
    /// that follows is orderly and noisy. A process that is <em>killed</em> -- SIGKILL from a
    /// platform, an out-of-memory kill, a fault in native code -- gets no signal and no chance to
    /// say anything, and the log simply stops mid-sentence. Without this line the two look the
    /// same at the top of the next boot, and the first question after a crash ("did something
    /// stop us, or did we fall over?") has no answer in the record.
    /// </para>
    /// <para>
    /// Nothing here cancels the signal, so the host's own shutdown runs exactly as it did before.
    /// </para>
    /// </remarks>
    private static void WatchStopSignals()
    {
        Listen(PosixSignal.SIGTERM);
        Listen(PosixSignal.SIGINT);
        Listen(PosixSignal.SIGQUIT);

        static void Listen(PosixSignal signal)
        {
            try
            {
                Signals.Add(PosixSignalRegistration.Create(signal, context =>
                    Log.Warning("Modbot was told to stop by the host ({Signal})", context.Signal)));
            }
            catch (Exception e) when (e is PlatformNotSupportedException or ArgumentOutOfRangeException)
            {
                // SIGQUIT is not a thing on Windows, and a platform may refuse any of them. Losing
                // the line costs a little clarity in a log; refusing to start would cost the host.
            }
        }
    }

    /// <summary>
    /// Makes the folder a crash dump would be written into, so that the runtime writing one
    /// finds somewhere to put it.
    /// </summary>
    public static void PrepareDumpFolder(CrashDumps dumps)
    {
        ArgumentNullException.ThrowIfNull(dumps);

        if (!dumps.On || dumps.Folder is not { Length: > 0 } folder)
            return;

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Error(
                exception,
                "Crash dumps are switched on but {Folder} could not be made, so a crash will leave no dump",
                folder);
        }
    }
}
