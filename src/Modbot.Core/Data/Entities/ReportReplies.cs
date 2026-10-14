using System.Text.Json;

namespace Modbot.Core.Data.Entities;

/// <summary>One ready-made message staff can send the reporter when they close a report.</summary>
/// <param name="Name">What the button says, so staff can tell the messages apart.</param>
/// <param name="Text">What the reporter is sent.</param>
public sealed record ReportReply(string Name, string Text);

/// <summary>
/// Reads and writes <see cref="Settings.MemberReportReplies"/>: the server's ready-made messages for
/// reporters. A stored value that cannot be read is treated as no messages rather than an error,
/// because closing a report must never fail on a settings column.
/// </summary>
public static class ReportReplies
{
    /// <summary>Most messages a server may keep.</summary>
    public const int MostKept = 10;

    public const int MaxNameLength = 40;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<ReportReply> Read(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<ReportReply>>(stored, Options)?
                .Where(r => r is { Name: { Length: > 0 }, Text: { Length: > 0 } })
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Write(IEnumerable<ReportReply> replies)
    {
        ArgumentNullException.ThrowIfNull(replies);
        return JsonSerializer.Serialize(replies, Options);
    }

    /// <summary>Why a list cannot be saved, in one sentence, or null when it can.</summary>
    public static string? Problem(IReadOnlyList<ReportReply> replies)
    {
        ArgumentNullException.ThrowIfNull(replies);

        if (replies.Count > MostKept)
            return $"At most {MostKept} messages.";

        foreach (var reply in replies)
        {
            if (string.IsNullOrWhiteSpace(reply.Name) || string.IsNullOrWhiteSpace(reply.Text))
                return "Every message needs a name and some text.";
            if (reply.Name.Trim().Length > MaxNameLength)
                return $"A name is at most {MaxNameLength} characters.";
            if (reply.Text.Trim().Length > MemberReport.MaxReporterMessageLength)
                return $"A message is at most {MemberReport.MaxReporterMessageLength} characters.";
        }

        if (replies.Select(r => r.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != replies.Count)
            return "Two messages have the same name.";

        return null;
    }
}
