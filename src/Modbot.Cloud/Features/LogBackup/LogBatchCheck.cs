using System.Text;
using System.Text.Json;
using Modbot.Cloud.Engine;

namespace Modbot.Cloud.Features.LogBackup;

/// <summary>One line, checked and ready to store.</summary>
public sealed record CheckedLine(
    string File,
    long Offset,
    string Text,
    DateTime? LoggedAt,
    short? UtcOffsetMinutes,
    CheckedEvent? Event);

/// <summary>One parsed event, named the way Cloud names it.</summary>
public sealed record CheckedEvent(string Type, string? TypeRaw, string Data);

/// <summary>A batch, checked.</summary>
public sealed record CheckedBatch(
    string ClientVersion,
    DateTimeOffset SentAt,
    long? ClockOffsetMs,
    string ClockConfidence,
    string? ModbotServerId,
    IReadOnlyList<CheckedLine> Lines);

/// <summary>
/// Decides whether a batch is well formed, and tidies what it may.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Refused outright</strong> (a <c>400</c>): no lines, a line with no file, no offset or no
/// text, a file name with a folder in it, a negative offset, no <c>sentAt</c>. A client that sends
/// these is broken and resending will not fix it.
/// </para>
/// <para>
/// <strong>Tidied, not refused:</strong> text is cut to <see cref="LogLine.MaxTextLength"/>
/// characters and any NUL character (which PostgreSQL text cannot hold) is replaced; event data over
/// <see cref="LogEvent.MaxDataBytes"/>, or not a JSON object, is stored as <c>{}</c>; an event with no
/// type is dropped and its line kept.
/// </para>
/// <para>
/// VRChat ids inside lines and events are never checked for shape (foundation 3.1.1).
/// </para>
/// </remarks>
public static class LogBatchCheck
{
    /// <summary>UTC offsets that exist anywhere on Earth run from −12:00 to +14:00.</summary>
    private const int MaxUtcOffsetMinutes = 14 * 60;

    public static (CheckedBatch? Batch, string? Problem) Check(LogBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Lines is not { Count: > 0 } lines)
            return (null, "A batch needs at least one line.");

        if (lines.Count > LogBackupLimits.MaxLinesPerBatch)
            return (null, $"A batch may carry at most {LogBackupLimits.MaxLinesPerBatch} lines.");

        if (batch.SentAt is not { } sentAt)
            return (null, "sentAt is required.");

        var parsedBy = Common.ClientText.Clean(batch.ClientVersion, LogEvent.MaxParsedByLength - "client/".Length) ?? "unknown";
        var checkedLines = new List<CheckedLine>(lines.Count);

        foreach (var line in lines)
        {
            if (line is null)
                return (null, "A line is empty.");

            if (line.File is not { Length: > 0 and <= LogFile.MaxNameLength } file
                || file.Contains('/') || file.Contains('\\') || file.Any(char.IsControl))
            {
                return (null, "Every line needs a file name of at most 128 characters, with no folder.");
            }

            if (line.Offset is not >= 0)
                return (null, "Every line needs an offset of zero or more.");

            if (line.Text is null)
                return (null, "Every line needs text.");

            short? utcOffset = line.UtcOffsetMinutes is { } minutes && Math.Abs(minutes) <= MaxUtcOffsetMinutes
                ? (short)minutes
                : null;

            DateTime? loggedAt = line.LoggedAt is { } logged ? DateTime.SpecifyKind(logged, DateTimeKind.Unspecified) : null;

            checkedLines.Add(new CheckedLine(file, line.Offset.Value, Tidy(line.Text), loggedAt, utcOffset, CheckEvent(line.Event)));
        }

        var confidence = batch.ClockConfidence is "good" or "fair" or "poor" ? batch.ClockConfidence : "unknown";

        return (new CheckedBatch(
            $"client/{parsedBy}",
            sentAt.ToUniversalTime(),
            batch.ClockOffsetMs,
            confidence,
            Common.ClientText.Clean(batch.ModbotServerId, Installs.Install.MaxServerIdLength),
            checkedLines), null);
    }

    private static string Tidy(string text)
    {
        if (text.Length > LogLine.MaxTextLength)
            text = text[..LogLine.MaxTextLength];

        return text.Contains('\0', StringComparison.Ordinal) ? text.Replace('\0', '�') : text;
    }

    private static CheckedEvent? CheckEvent(LogBatchEvent? logEvent)
    {
        if (logEvent?.Type is not { Length: > 0 } clientType)
            return null;

        var (type, typeRaw) = LogEventTypes.Classify(clientType);
        return new CheckedEvent(type, typeRaw, Data(logEvent.Data));
    }

    private static string Data(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element)
            return "{}";

        var text = element.GetRawText();

        // jsonb refuses the NUL escape, and one refused row would fail the whole batch.
        if (Encoding.UTF8.GetByteCount(text) > LogEvent.MaxDataBytes || text.Contains("\\u0000", StringComparison.Ordinal))
            return "{}";

        return text;
    }
}
