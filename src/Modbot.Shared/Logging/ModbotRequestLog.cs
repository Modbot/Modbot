using Serilog.Events;

namespace Modbot.Core.Logging;

/// <summary>
/// One line per HTTP request, and what level it is written at.
/// </summary>
/// <remarks>
/// <para>
/// ASP.NET Core narrates four Information lines per request by itself, which is why every Modbot
/// service holds <c>Microsoft.AspNetCore</c> at Warning. This is what replaces them: a single
/// event carrying the method, the path, the status and how long it took, as properties rather than
/// as a sentence, so <c>@StatusCode:500</c> is a filter in Seq or a Railway log explorer.
/// </para>
/// <para>
/// The policy lives here, and each service spends four lines wiring it to
/// <c>UseSerilogRequestLogging</c>. It is not a shared extension method because the method would
/// need Serilog.AspNetCore, which carries a reference to the whole ASP.NET Core framework, and
/// Modbot.Shared is also what the desktop client is built on — a tray application should not ship a
/// web server (see Modbot.Shared.csproj).
/// </para>
/// </remarks>
public static class ModbotRequestLog
{
    public const string MessageTemplate =
        "{RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0} ms";

    /// <summary>
    /// Information for an ordinary request, Warning for a server error, and Debug for a health
    /// check.
    /// </summary>
    /// <remarks>
    /// The health check is the reason this function exists. A container probes <c>/health</c> every
    /// thirty seconds forever, which is close to three thousand lines a day saying that nothing is
    /// wrong — enough to bury the one line that says something is.
    /// </remarks>
    public static LogEventLevel LevelFor(string path, int statusCode, bool failed)
    {
        if (failed || statusCode >= 500) return LogEventLevel.Warning;

        return path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
            ? LogEventLevel.Debug
            : LogEventLevel.Information;
    }
}
