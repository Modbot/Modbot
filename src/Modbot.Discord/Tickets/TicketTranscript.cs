using System.Globalization;
using System.Text;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Tickets;

/// <summary>
/// Writes a closed ticket as plain text for the transcript file. Pure: the times come in, and
/// nothing is read or sent from here.
/// </summary>
public static class TicketTranscript
{
    /// <summary>What stands where a private image was.</summary>
    public const string ImageRemoved = "[image removed]";

    /// <summary>The line at the top when Discord sent no message text.</summary>
    public const string TextMissing = "The text of the messages is missing: Modbot cannot read message text on this server.";

    /// <summary>Most characters in a transcript, so the file stays under what Discord accepts.</summary>
    public const int MostLength = 2_000_000;

    private const string Cut = "[the transcript was cut here]";

    /// <param name="messagesOldestFirst">Every message in the thread, oldest first.</param>
    /// <param name="holdsPrivateImages">
    /// The ticket's type holds photos or documents that must not be kept: no attachment is named
    /// or linked, however it looks.
    /// </param>
    /// <param name="textMissing">Discord gave no message text because the Message Content intent is off.</param>
    public static string Build(
        string typeName,
        string openedById,
        string? openedByName,
        DateTimeOffset openedAt,
        string closedById,
        DateTimeOffset closedAt,
        IReadOnlyList<DiscordMessageSnapshot> messagesOldestFirst,
        bool holdsPrivateImages,
        bool textMissing)
    {
        ArgumentNullException.ThrowIfNull(messagesOldestFirst);

        var text = new StringBuilder();

        text.Append("Ticket: ").AppendLine(typeName);
        text.Append("Opened by ").Append(Who(openedByName, openedById)).Append(" on ").AppendLine(Time(openedAt));
        text.Append("Closed by ").Append(closedById).Append(" on ").AppendLine(Time(closedAt));

        if (textMissing)
            text.AppendLine(TextMissing);

        text.AppendLine();

        foreach (var message in messagesOldestFirst)
        {
            text.Append('[').Append(Time(message.SentAt)).Append("] ")
                .Append(Who(message.AuthorName, message.AuthorId)).Append(": ")
                .AppendLine(message.Text);

            foreach (var attachment in message.Attachments)
            {
                text.Append("    ").AppendLine(
                    holdsPrivateImages
                        ? ImageRemoved
                        : $"[attachment: {attachment.Name}] {attachment.Url}");
            }

            if (text.Length > MostLength)
                break;
        }

        if (text.Length <= MostLength)
            return text.ToString();

        return text.ToString(0, MostLength) + Environment.NewLine + Cut;
    }

    private static string Who(string? name, string id)
        => string.IsNullOrWhiteSpace(name) ? id : $"{name} ({id})";

    private static string Time(DateTimeOffset when)
        => when.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
}
