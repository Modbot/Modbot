using Serilog;
using Serilog.Configuration;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;

namespace Modbot.Core.Logging;

/// <summary>
/// The console half of every Modbot program's log: which of the three shapes it is written in, at
/// what level, and the enrichment every event carries.
/// </summary>
/// <remarks>
/// <para>
/// This lives in Modbot.Shared rather than Modbot.Core because all five programs need it — the
/// server, my.modbot.co, Modbot Cloud, the landing page and the companion — and Modbot.Core
/// carries Entity Framework, Npgsql and data protection, which three of them have no use for and
/// the client refuses on principle (see Modbot.Shared.csproj). Modbot.Shared gains four small
/// Serilog packages and no framework of any kind.
/// </para>
/// <para>
/// Two environment variables, read by every program:
/// </para>
/// <list type="bullet">
///   <item><c>CONSOLE_LOG_MODE</c> — <c>serilog</c>, <c>json</c> or <c>railway_json</c>. See
///   <see cref="ConsoleLogMode"/>.</item>
///   <item><c>LOG_LEVEL</c> — Verbose, Debug, Information, Warning, Error or Fatal.</item>
/// </list>
/// <para>
/// Both are read case-insensitively, spaces, hyphens and underscores are ignored, and anything that
/// is not recognised is the default rather than a startup failure. A log setting is never a reason
/// for a deployment not to come up.
/// </para>
/// </remarks>
public static class ModbotConsoleLog
{
    public const string ModeVariable = "CONSOLE_LOG_MODE";
    public const string LevelVariable = "LOG_LEVEL";

    /// <summary>The name of the program, on every event, so one Seq or log explorer can hold all of them.</summary>
    public const string ServiceProperty = "Service";

    /// <summary>The release this event came from, YYYY.M.PATCH.</summary>
    public const string VersionProperty = "Version";

    /// <summary>The readable console line, unchanged from what the server has always printed.</summary>
    public const string Template = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>Reads <c>CONSOLE_LOG_MODE</c>.</summary>
    /// <param name="get">Where to read from. Defaults to the process environment.</param>
    public static ConsoleLogMode ReadMode(Func<string, string?>? get = null) =>
        ParseMode((get ?? Environment.GetEnvironmentVariable)(ModeVariable));

    /// <summary>
    /// The mode a value names. Case, spaces, hyphens and underscores do not matter; anything else
    /// is <see cref="ConsoleLogMode.Serilog"/>.
    /// </summary>
    public static ConsoleLogMode ParseMode(string? value) => Tidy(value) switch
    {
        "json" => ConsoleLogMode.Json,
        "railwayjson" => ConsoleLogMode.RailwayJson,
        _ => ConsoleLogMode.Serilog,
    };

    /// <summary>Reads <c>LOG_LEVEL</c>, falling back to <paramref name="fallback"/>.</summary>
    public static LogEventLevel ReadLevel(LogEventLevel fallback, Func<string, string?>? get = null) =>
        ParseLevel((get ?? Environment.GetEnvironmentVariable)(LevelVariable), fallback);

    /// <summary>The level a value names, or <paramref name="fallback"/> when it names none.</summary>
    /// <remarks>
    /// The six names and nothing else. <c>Enum.TryParse</c> would also accept a number, and
    /// <c>LOG_LEVEL=11</c> parsing into a level that does not exist would silence the whole log.
    /// </remarks>
    public static LogEventLevel ParseLevel(string? value, LogEventLevel fallback) => Tidy(value) switch
    {
        "verbose" => LogEventLevel.Verbose,
        "debug" => LogEventLevel.Debug,
        "information" => LogEventLevel.Information,
        "warning" => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        "fatal" => LogEventLevel.Fatal,
        _ => fallback,
    };

    /// <summary>
    /// The formatter a mode is written with, or null for <see cref="ConsoleLogMode.Serilog"/>,
    /// which is a text template rather than a formatter.
    /// </summary>
    public static ITextFormatter? Formatter(ConsoleLogMode mode) => mode switch
    {
        ConsoleLogMode.Json => new CompactJsonFormatter(),
        ConsoleLogMode.RailwayJson => new RailwayJsonFormatter(),
        _ => null,
    };

    /// <summary>Adds the console sink for a mode.</summary>
    /// <param name="minimum">
    /// The lowest level the console shows. Separate from the logger's own minimum so that a
    /// program whose files are recording Debug can still keep its terminal readable.
    /// </param>
    public static LoggerConfiguration ModbotConsole(
        this LoggerSinkConfiguration sink,
        ConsoleLogMode mode,
        LogEventLevel minimum)
    {
        ArgumentNullException.ThrowIfNull(sink);

        return Formatter(mode) is { } formatter
            ? sink.Console(formatter, restrictedToMinimumLevel: minimum)
            : sink.Console(outputTemplate: Template, restrictedToMinimumLevel: minimum);
    }

    /// <summary>
    /// The start every Modbot logger shares: the minimum level, enrichment from context, and the
    /// name and version of the program. The caller adds the sinks, because the server has six log
    /// files and the landing page has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ASP.NET Core narrates four Information lines for every request — request starting, endpoint
    /// chosen, endpoint finished, request finished — and a container's health probe is a request
    /// every thirty seconds. It is what you want when you asked for detail and noise when you did
    /// not, and the framework writes it through <c>ILogger</c> at a level Modbot cannot change one
    /// event at a time, so the only instrument available is this floor: Warning unless
    /// <c>LOG_LEVEL</c> asks for Debug or Verbose, and then every line of it, at the level ASP.NET
    /// Core chose. Modbot's own one-line-per-request record (<see cref="ModbotRequestLog"/>) is
    /// what stands in for it the rest of the time.
    /// </para>
    /// <para>
    /// <strong>Entity Framework is not held here, and used to be (2026-09-18).</strong> EF does
    /// have the instrument ASP.NET Core lacks: a level can be set for one event where the context
    /// is configured, so the line per statement can be filed under Debug instead of being thrown
    /// away. That is what <c>DatabaseLogLevels</c> in Modbot.Core does, and its twin in
    /// Modbot.Cloud. With the noise moved, a floor over the whole of <c>Microsoft.EntityFrameworkCore</c>
    /// only does harm: it cannot tell one event from another, and EF writes more than SQL — the
    /// migrations it applies, the retries, the mistakes it warns about — so holding the lot at
    /// Warning threw those away as well, for a deployment where they are the first thing an
    /// operator would want to read.
    /// </para>
    /// </remarks>
    public static LoggerConfiguration Start(string service, LogEventLevel level)
    {
        var config = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext()
            .Enrich.WithProperty(ServiceProperty, service)
            .Enrich.WithProperty(VersionProperty, ModbotVersion.Release);

        if (level > LogEventLevel.Debug)
            config.MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning);

        return config;
    }

    private static string Tidy(string? value) =>
        value is null
            ? ""
            : string.Concat(value.Where(c => !char.IsWhiteSpace(c) && c is not '-' and not '_'))
                .ToLowerInvariant();
}
