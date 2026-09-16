using System.Runtime.InteropServices;
using Modbot.Core;
using Modbot.Core.Logging;
using Serilog;
using Serilog.Events;

namespace Modbot.Client.App;

/// <summary>
/// The client's own log: what it is doing, in detail, and why it stopped if it stopped.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this reads and writes.</strong> Nothing is read. Log lines are written to your
/// disk at <c>%APPDATA%\Modbot\logs\client-&lt;date&gt;.log</c>, one file a day, the last seven kept,
/// and echoed to the terminal the client was started from when there is one. Nothing in these
/// files leaves the machine: Modbot never uploads its own log. It exists so that when the client
/// misbehaves, the moderator -- or whoever they ask for help -- can open a text file and see what
/// happened, rather than watching a tray icon vanish.
/// </para>
/// <para>
/// The level is Verbose while the client is young. Each tick that read anything from VRChat's log
/// is recorded, and so is every exception with its full stack, because the failure this exists to
/// catch is the one where the client dies on the first log file it meets and there is nothing to
/// go on. <c>MODBOT_CLIENT_LOG_LEVEL</c> (Verbose, Debug, Information, Warning) turns it down.
/// </para>
/// <para>
/// <c>CONSOLE_LOG_MODE</c> changes the shape of the terminal output, and only the terminal output:
/// <c>serilog</c> (the default) for readable lines, <c>json</c> or <c>railway_json</c> for one JSON
/// object per line. See <see cref="ConsoleLogMode"/>. The files are always text.
/// </para>
/// <para>
/// The client is a windowed program, so it has no console of its own; it attaches to the one of
/// the process that started it, which is how <c>dotnet run</c> from a terminal shows output.
/// </para>
/// </remarks>
internal static class ClientLog
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    /// <summary>Where the log files are, once <see cref="Start"/> has run.</summary>
    public static string? Folder { get; private set; }

    public static string LogFolder(string appData) => Path.Combine(appData, "Modbot", "logs");

    public static void Start(string appData)
    {
        // Before anything touches System.Console, or the attached console is never picked up.
        if (OperatingSystem.IsWindows())
            AttachConsole(AttachParentProcess);

        var level = Environment.GetEnvironmentVariable("MODBOT_CLIENT_LOG_LEVEL") switch
        {
            { } text when Enum.TryParse<LogEventLevel>(text, ignoreCase: true, out var parsed) => parsed,
            _ => LogEventLevel.Verbose,
        };

        Folder = LogFolder(appData);

        // The console shape is the same choice every Modbot program offers, so a client started
        // from a script that reads JSON gets JSON. The files are always text: they are opened by a
        // person, on their own machine, at an hour when they will not install jq.
        var consoleMode = ModbotConsoleLog.ReadMode();

        const string template = "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext()
            .Enrich.WithProperty(ModbotConsoleLog.ServiceProperty, "Modbot.Client")
            .Enrich.WithProperty(ModbotConsoleLog.VersionProperty, ModbotVersion.Release)
            .WriteTo.ModbotConsole(consoleMode, level)
            .WriteTo.File(
                Path.Combine(LogFolder(appData), "client-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                outputTemplate: template)
            .CreateLogger();

        Log.Information(
            "Modbot client {Version} starting; log level {Level}; console {ConsoleMode}; log files in {Directory}",
            ModbotVersion.Release, level, consoleMode, LogFolder(appData));
    }

    public static void Stop() => Log.CloseAndFlush();
}
