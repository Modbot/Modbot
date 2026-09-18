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
/// One line per request is still one line per request. On a deployment with a Discord bot, a
/// companion polling and a browser open on a dashboard, that is the whole log: the record of what
/// Modbot <em>did</em> — a sync ran, a ban landed, a report was filed — is scrolled off the page by
/// traffic that only says the web server is working. So an ordinary request is Debug, and the
/// people who want it ask for it with <c>LOG_LEVEL=Debug</c>. What is not ordinary — a request that
/// threw, a 5xx — stays at Warning, where it always was.
/// </para>
/// <para>
/// The policy lives here, and each service spends four lines wiring it to
/// <c>UseSerilogRequestLogging</c>. It is not a shared extension method because the method would
/// need Serilog.AspNetCore, which carries a reference to the whole ASP.NET Core framework, and
/// Modbot.Shared is also what the companion is built on — a tray application should not ship a
/// web server (see Modbot.Shared.csproj).
/// </para>
/// </remarks>
public static class ModbotRequestLog
{
    public const string MessageTemplate =
        "{RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0} ms";

    /// <summary>
    /// Warning for a request that threw or answered 5xx, Debug for every other request, the health
    /// check included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The health check is the reason this function exists. A container probes <c>/health</c> every
    /// thirty seconds forever, which is close to three thousand lines a day saying that nothing is
    /// wrong — enough to bury the one line that says something is. Ordinary requests turned out to
    /// be the same problem at a larger scale, so they joined it at Debug (2026-09-18); the two
    /// answers agree today, and the probe is still named here because it is the case that must
    /// never quietly climb back to Information.
    /// </para>
    /// <para>
    /// <paramref name="path"/> is kept for that reason and is not read while both answers are
    /// Debug.
    /// </para>
    /// </remarks>
    public static LogEventLevel LevelFor(string path, int statusCode, bool failed) =>
        failed || statusCode >= 500 ? LogEventLevel.Warning : LogEventLevel.Debug;
}
