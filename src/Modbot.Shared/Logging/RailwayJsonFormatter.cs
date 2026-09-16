using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Json;

namespace Modbot.Core.Logging;

/// <summary>
/// Writes each event as the one-line JSON object Railway parses into a structured log.
/// </summary>
/// <remarks>
/// <para>
/// Railway reads two keys and shows everything else as a searchable attribute
/// (<a href="https://docs.railway.com/observability/logs#structured-logs">Railway — Structured
/// logs</a>):
/// </para>
/// <list type="bullet">
///   <item><c>message</c> — required, the text of the line. Railway also accepts <c>msg</c> and
///   renames it; this writes <c>message</c>, so nothing has to be renamed.</item>
///   <item><c>level</c> — <c>debug</c>, <c>info</c>, <c>warn</c> or <c>error</c>. Railway lowercases
///   whatever it is given and matches it to the closest of those four, and colours the line by
///   it.</item>
/// </list>
/// <para>
/// Serilog has six levels and Railway has four, so Verbose arrives as <c>debug</c> and Fatal as
/// <c>error</c>. The exact Serilog level is kept as well, under <c>logLevel</c>, because losing the
/// difference between an error and the one that stopped the program is the difference that matters
/// at the moment you are reading the log. Query it as <c>@logLevel:Fatal</c>.
/// </para>
/// <para>
/// Everything else — the properties attached to the event, plus the service name and version — is a
/// top-level key, which is what makes <c>@Port:8080</c> or <c>@Service:Modbot.Cloud</c> a filter in
/// the log explorer. The whole object is written on one line, without which Railway does not parse
/// it at all.
/// </para>
/// </remarks>
public sealed class RailwayJsonFormatter : ITextFormatter
{
    /// <summary>The key Railway reads the text of the line from.</summary>
    public const string MessageKey = "message";

    /// <summary>The key Railway reads the severity from, and colours the line by.</summary>
    public const string LevelKey = "level";

    /// <summary>When the event was written, ISO 8601 with its offset.</summary>
    public const string TimestampKey = "timestamp";

    /// <summary>Serilog's own level, kept because Railway's four cannot hold Serilog's six.</summary>
    public const string LogLevelKey = "logLevel";

    /// <summary>The exception, including its stack, as one string.</summary>
    public const string ExceptionKey = "exception";

    private static readonly string[] Reserved =
        [MessageKey, LevelKey, TimestampKey, LogLevelKey, ExceptionKey];

    /// <summary>Serilog's six levels as the four words Railway matches against.</summary>
    public static string RailwayLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "debug",
        LogEventLevel.Debug => "debug",
        LogEventLevel.Information => "info",
        LogEventLevel.Warning => "warn",
        _ => "error",
    };

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        output.Write("{\"");
        output.Write(LevelKey);
        output.Write("\":\"");
        output.Write(RailwayLevel(logEvent.Level));

        output.Write("\",\"");
        output.Write(LogLevelKey);
        output.Write("\":\"");
        output.Write(logEvent.Level);

        output.Write("\",\"");
        output.Write(TimestampKey);
        output.Write("\":\"");
        output.Write(logEvent.Timestamp.ToString("O"));
        output.Write("\",\"");

        output.Write(MessageKey);
        output.Write("\":");
        JsonValueFormatter.WriteQuotedJsonString(logEvent.RenderMessage(), output);

        foreach (var property in logEvent.Properties)
        {
            // A property called "message" or "level" would overwrite the keys Railway reads, and a
            // line whose message is not the message is worse than a missing property.
            if (Reserved.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
                continue;

            output.Write(',');
            JsonValueFormatter.WriteQuotedJsonString(property.Key, output);
            output.Write(':');
            ValueFormatter.Format(property.Value, output);
        }

        if (logEvent.Exception is not null)
        {
            output.Write(",\"");
            output.Write(ExceptionKey);
            output.Write("\":");
            JsonValueFormatter.WriteQuotedJsonString(logEvent.Exception.ToString(), output);
        }

        output.Write('}');
        output.Write('\n');
    }

    // Serilog's own JSON writer, so a list of strings stays a JSON array rather than becoming a
    // rendered sentence -- Railway can filter on an array element, but only if it is one.
    private static readonly JsonValueFormatter ValueFormatter = new(typeTagName: null);
}
