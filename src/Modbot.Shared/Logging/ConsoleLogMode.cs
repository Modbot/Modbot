namespace Modbot.Core.Logging;

/// <summary>
/// How a Modbot program writes its log to the console, from <c>CONSOLE_LOG_MODE</c>.
/// </summary>
/// <remarks>
/// Only the console changes. Log files, Seq and the levels events are written at are the same in
/// every mode: this says who is reading the terminal — a person, or a machine.
/// </remarks>
public enum ConsoleLogMode
{
    /// <summary>
    /// A line of readable text per event, the way Modbot has always printed it. The default, and
    /// what an unset or unrecognised <c>CONSOLE_LOG_MODE</c> means.
    /// </summary>
    Serilog,

    /// <summary>
    /// One JSON object per line in Serilog's compact format: the message template, its properties,
    /// the level, the time and the exception.
    /// </summary>
    Json,

    /// <summary>
    /// One JSON object per line in the shape Railway reads: a <c>message</c>, a <c>level</c> of
    /// <c>debug</c>, <c>info</c>, <c>warn</c> or <c>error</c>, and every other property as a
    /// top-level key Railway turns into a searchable attribute.
    /// </summary>
    RailwayJson,
}
