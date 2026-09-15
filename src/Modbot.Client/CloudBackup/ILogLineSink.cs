using Modbot.Client.LogReading;

namespace Modbot.Client.CloudBackup;

/// <summary>One line the log reader read, with what it made of it.</summary>
/// <param name="File">The log file's name, without its folder.</param>
/// <param name="Offset">Where the line starts in that file.</param>
/// <param name="IsReplay">Already in the file when the client started.</param>
/// <param name="Parsed">The line's timestamp and tag, when it has the usual shape.</param>
/// <param name="Event">What the client recognised in it, when anything.</param>
public sealed record ReadLogLine(
    string File,
    long Offset,
    string Text,
    bool IsReplay,
    VRChatLogLine? Parsed,
    VRChatLogEvent? Event);

/// <summary>
/// Somewhere the log reader hands every line it reads, as it reads them.
/// </summary>
/// <remarks>
/// <para>
/// There is one: <see cref="CloudLogBackup"/>. It is an interface so the reader does not depend on
/// how the backup works, and so a test can prove the reader never waits on it.
/// </para>
/// <para>
/// Nothing is sent from here: lines leave the machine only from <see cref="CloudLogBackup"/>.
/// <see cref="Offer"/> must return at once. It runs inside the log reader's turn, which also feeds
/// presence reporting to Modbot servers; anything slow in here would be slow there too.
/// </para>
/// </remarks>
public interface ILogLineSink
{
    /// <summary>False while there is nothing to hand over, so the reader need not build the list.</summary>
    bool WantsLines { get; }

    void Offer(IReadOnlyList<ReadLogLine> lines);
}
