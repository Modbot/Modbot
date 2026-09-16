namespace Modbot.Core.Data.Entities;

/// <summary>
/// One line of Modbot's own log, kept in the database so it can be read in the app.
/// </summary>
/// <remarks>
/// <para>
/// The console, the files and Seq are unchanged; this is a fourth destination, for the operator who
/// has none of them — a container whose disk is thrown away, no Seq, and a hosting dashboard that
/// shows the last few hundred lines and no way to search them. Foundation spec 4.4.1 lists the
/// three streams; this one carries the same events as the main stream.
/// </para>
/// <para>
/// <strong>Outbound API traffic is not stored here.</strong> The main log file excludes it for
/// readability; this table excludes it for size. A busy sync makes tens of thousands of API lines a
/// day, and a table holding six months of them would be the largest thing in the database by a wide
/// margin, for a stream that is already in <c>modbot_log_http_*.jsonl</c> and in Seq.
/// </para>
/// <para>
/// Written only by <c>DatabaseLogSink</c>, which batches and drops rather than blocking. Nothing
/// updates a row: a log line is what was written at the time.
/// </para>
/// </remarks>
public class LogEntry
{
    public long Id { get; set; }

    /// <summary>When the line was written, on <c>IModbotClock</c>'s clock through Serilog.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>
    /// <c>Verbose</c>, <c>Debug</c>, <c>Information</c>, <c>Warning</c>, <c>Error</c> or
    /// <c>Fatal</c>. Text, so a new Serilog level never renumbers the ones already written.
    /// </summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>The line as a person reads it, with the values filled in.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// The line before the values were filled in, e.g. <c>Deleted {Count} row(s)</c>. Kept so that
    /// every occurrence of one log line can be found together, however different the numbers are.
    /// </summary>
    public string? Template { get; set; }

    /// <summary>
    /// Where it came from: Serilog's <c>SourceContext</c>, which is the full name of the class that
    /// wrote it. Null when the writer did not say.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>Which stream it belongs to (<see cref="Logging.LogArea"/>), when it said.</summary>
    public string? Area { get; set; }

    /// <summary>The exception, as Serilog renders it. Null when there was none.</summary>
    public string? Exception { get; set; }

    /// <summary>
    /// Everything else the line carried, as a JSON object. Property values whose name looks like a
    /// secret are replaced before they get here — see <c>LogPropertyRedaction</c>.
    /// </summary>
    public string Properties { get; set; } = "{}";
}
