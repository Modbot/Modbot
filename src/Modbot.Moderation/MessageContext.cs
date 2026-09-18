using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Moderation;

/// <summary>One message sent to the model as context, never as the thing being judged.</summary>
/// <param name="Author">The name the author had when the message was stored. Untrusted text.</param>
/// <param name="ReplyTarget">This is the message the checked one replied to, not a message before it.</param>
public sealed record ContextMessage(string MessageId, string Author, string Text, bool ReplyTarget);

/// <summary>
/// The few messages before the one being checked (AI moderation design §16).
/// </summary>
/// <remarks>
/// <para>
/// A reply, a joke and a quote all read differently on their own, and a rule that judges the last
/// message alone flags all three. So the messages just before it in the same channel or thread go
/// with it, plus the message it replied to when there is one, and the model is told in so many
/// words to judge only the last one.
/// </para>
/// <para>
/// Context never widens what can be flagged: the quote check still runs against the checked
/// message alone, so a model that answers with a line from the context has invented the flag and
/// the hit is thrown away (design §15.2).
/// </para>
/// </remarks>
public static class MessageContext
{
    /// <summary>The most characters one context message contributes. Longer ones are clipped.</summary>
    public const int MostTextPerMessage = 500;

    /// <summary>
    /// The messages before <paramref name="messageId"/> in its own channel or thread, oldest
    /// first, with the message it replied to in front of them.
    /// </summary>
    /// <param name="count">How many messages before it, from the rule. Zero reads nothing.</param>
    public static async Task<IReadOnlyList<ContextMessage>> BeforeAsync(
        ModbotContext db, string channelId, string messageId, int count, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (count <= 0)
            return [];

        // The checked message may not be stored yet -- the engine runs as it arrives -- so the
        // messages before it are found by time, from the row if there is one and from the id's
        // own ordering otherwise. Discord ids sort by time, and string ordering matches numeric
        // ordering only at equal length, which every id of one era is.
        var self = await db.DiscordMessages.AsNoTracking()
            .Where(m => m.MessageId == messageId)
            .Select(m => new { m.SentAt, m.ChannelId, m.ThreadId, m.ReplyToId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var before = self?.SentAt;

        // A thread is its own conversation: a message in one is surrounded by the thread, not by
        // the channel the thread hangs off.
        var thread = self?.ThreadId;
        var channel = self?.ChannelId ?? channelId;

        var query = db.DiscordMessages.AsNoTracking().Where(m => m.MessageId != messageId);

        query = thread is null
            ? query.Where(m => m.ChannelId == channel && m.ThreadId == null)
            : query.Where(m => m.ThreadId == thread);

        if (before is { } at)
            query = query.Where(m => m.SentAt <= at);

        var rows = await query
            .OrderByDescending(m => m.SentAt)
            .ThenByDescending(m => m.MessageId)
            .Take(count)
            .Select(m => new { m.MessageId, m.AuthorName, m.Text, m.SentAt })
            .ToListAsync(ct).ConfigureAwait(false);

        var messages = rows
            .OrderBy(r => r.SentAt).ThenBy(r => r.MessageId, StringComparer.Ordinal)
            .Where(r => !string.IsNullOrWhiteSpace(r.Text))
            .Select(r => new ContextMessage(r.MessageId, r.AuthorName, Clip(r.Text), ReplyTarget: false))
            .ToList();

        if (self?.ReplyToId is not { Length: > 0 } replyTo || messages.Any(m => m.MessageId == replyTo))
            return messages;

        var replied = await db.DiscordMessages.AsNoTracking()
            .Where(m => m.MessageId == replyTo)
            .Select(m => new { m.MessageId, m.AuthorName, m.Text })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (replied is null || string.IsNullOrWhiteSpace(replied.Text))
            return messages;

        // In front of the rest: it is the thing being replied to, whenever it was posted.
        messages.Insert(0, new ContextMessage(replied.MessageId, replied.AuthorName, Clip(replied.Text), ReplyTarget: true));
        return messages;
    }

    private static string Clip(string text)
        => text.Length <= MostTextPerMessage ? text : text[..MostTextPerMessage];
}
