using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Messages;
using Modbot.Core.Data;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;
using Npgsql;
using NpgsqlTypes;

namespace Modbot.Discord.Messages;

/// <summary>What storing an edit did.</summary>
/// <param name="Stored">The message was not stored before, and now is.</param>
/// <param name="PreviousText">The text before the edit, when the text changed. Null otherwise.</param>
public sealed record DiscordEditOutcome(bool Stored, string? PreviousText);

/// <summary>
/// Writes Discord messages into <c>discord_message</c>: new ones, edits and deletes (M5 spec §5.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Storing twice is harmless.</strong> The same message arrives live, again on the page a
/// read-back reads, and again when catching up; every insert is <c>ON CONFLICT DO NOTHING</c> on
/// the message id and its send time, and says which messages were new. That answer is what the
/// read-back's stop rule counts.
/// </para>
/// <para>
/// A message older than the message retention setting is not stored: retention would drop its
/// month the next day.
/// </para>
/// </remarks>
public sealed class DiscordMessageStore
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly MessagePartitionMaintainer _partitions;

    public DiscordMessageStore(ModbotContext db, IModbotClock clock, MessagePartitionMaintainer partitions)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(partitions);

        _db = db;
        _clock = clock;
        _partitions = partitions;
    }

    /// <summary>
    /// Stores every message not already stored, and returns the ones that were new, in the order given.
    /// </summary>
    public async Task<IReadOnlyList<DiscordMessageSnapshot>> StoreAsync(
        IReadOnlyList<DiscordMessageSnapshot> messages, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var cutoff = await RetentionCutoffAsync(ct).ConfigureAwait(false);

        var keep = messages
            .Where(m => cutoff is null || m.SentAt >= cutoff)
            .DistinctBy(m => m.Id, StringComparer.Ordinal)
            .ToList();

        if (keep.Count == 0)
            return [];

        await _partitions.EnsureForAsync(keep.Select(m => m.SentAt), ct).ConfigureAwait(false);

        var now = _clock.UtcNow;

        // One statement for the page: unnest the columns side by side. RETURNING lists only the
        // rows that went in, which is exactly "the messages that were new".
        const string Sql = """
            INSERT INTO discord_message (
                message_id, sent_at, guild_id, channel_id, thread_id, author_id, author_name,
                author_is_bot, text, attachments, embed_count, reply_to_id, mention_count, pinned,
                edited_at, deleted_at, stored_at)
            SELECT m.message_id, m.sent_at, m.guild_id, m.channel_id, m.thread_id, m.author_id,
                   m.author_name, m.author_is_bot, m.text, m.attachments::jsonb, m.embed_count,
                   m.reply_to_id, m.mention_count, m.pinned, m.edited_at, NULL, @now
            FROM unnest(
                @ids, @sent, @guilds, @channels, @threads, @authors, @names, @bots, @texts,
                @attachments, @embeds, @replies, @mentions, @pinned, @edited)
                AS m(message_id, sent_at, guild_id, channel_id, thread_id, author_id, author_name,
                     author_is_bot, text, attachments, embed_count, reply_to_id, mention_count,
                     pinned, edited_at)
            ON CONFLICT (message_id, sent_at) DO NOTHING
            RETURNING message_id AS "Value"
            """;

        var parameters = new NpgsqlParameter[]
        {
            Array("ids", NpgsqlDbType.Text, keep.Select(m => m.Id)),
            new("sent", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = keep.Select(m => m.SentAt.UtcDateTime).ToArray() },
            Array("guilds", NpgsqlDbType.Text, keep.Select(m => m.GuildId)),
            Array("channels", NpgsqlDbType.Text, keep.Select(m => m.ChannelId)),
            Array("threads", NpgsqlDbType.Text, keep.Select(m => m.ThreadId)),
            Array("authors", NpgsqlDbType.Text, keep.Select(m => m.AuthorId)),
            Array("names", NpgsqlDbType.Text, keep.Select(m => m.AuthorName)),
            new("bots", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { Value = keep.Select(m => m.AuthorIsBot).ToArray() },
            Array("texts", NpgsqlDbType.Text, keep.Select(m => Clean(m.Text))),
            Array("attachments", NpgsqlDbType.Text, keep.Select(m => AttachmentsJson(m.Attachments))),
            new("embeds", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = keep.Select(m => m.EmbedCount).ToArray() },
            Array("replies", NpgsqlDbType.Text, keep.Select(m => m.ReplyToId)),
            new("mentions", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = keep.Select(m => m.MentionCount).ToArray() },
            new("pinned", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { Value = keep.Select(m => m.Pinned).ToArray() },
            new("edited", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
            {
                Value = keep.Select(m => m.EditedAt?.UtcDateTime).ToArray(),
            },
            new("now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime },
        };

#pragma warning disable EF1002 // Constant SQL; every value is a parameter.
        var inserted = await _db.Database.SqlQueryRaw<string>(Sql, parameters).ToListAsync(ct).ConfigureAwait(false);
#pragma warning restore EF1002

        var fresh = inserted.ToHashSet(StringComparer.Ordinal);
        return keep.Where(m => fresh.Contains(m.Id)).ToList();
    }

    /// <summary>
    /// Brings a stored message up to date with an edit, keeping its earlier text. A message not
    /// stored yet is stored as it now reads.
    /// </summary>
    public async Task<DiscordEditOutcome> EditAsync(DiscordMessageSnapshot after, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(after);

        var row = await _db.DiscordMessages
            .FirstOrDefaultAsync(m => m.MessageId == after.Id && m.SentAt == after.SentAt, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            var stored = await StoreAsync([after], ct).ConfigureAwait(false);
            return new DiscordEditOutcome(stored.Count > 0, null);
        }

        var text = Clean(after.Text);
        string? previous = null;

        // Without the Message Content intent every message arrives with no text; that is not an
        // edit that emptied it, and the stored text is not thrown away over it.
        var textChanged = after.EditedAt is not null
            && !string.Equals(row.Text, text, StringComparison.Ordinal)
            && !(text.Length == 0 && after.Attachments.Count == 0 && after.EmbedCount == 0 && row.Text.Length > 0);

        if (textChanged)
        {
            previous = row.Text;

            _db.DiscordMessageEdits.Add(new Core.Data.Entities.DiscordMessageEdit
            {
                MessageId = row.MessageId,
                SentAt = row.SentAt,
                Text = row.Text,
                ReplacedAt = after.EditedAt ?? _clock.UtcNow,
            });

            row.Text = text;
            row.EditedAt = after.EditedAt;
        }

        row.Attachments = AttachmentsJson(after.Attachments);
        row.EmbedCount = after.EmbedCount;
        row.MentionCount = after.MentionCount;
        row.Pinned = after.Pinned;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new DiscordEditOutcome(false, previous);
    }

    /// <summary>Marks messages deleted. The rows stay. Returns how many were stored and not already marked.</summary>
    public async Task<int> DeleteAsync(IReadOnlyList<string> messageIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messageIds);

        if (messageIds.Count == 0)
            return 0;

        var now = _clock.UtcNow;
        var ids = messageIds.ToArray();

        return await _db.DiscordMessages
            .Where(m => ids.Contains(m.MessageId) && m.DeletedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.DeletedAt, now), ct)
            .ConfigureAwait(false);
    }

    private async Task<DateTimeOffset?> RetentionCutoffAsync(CancellationToken ct)
    {
        var days = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => (int?)s.DiscordMessageRetentionDays)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false) ?? 0;

        return days > 0 ? _clock.UtcNow.AddDays(-days) : null;
    }

    private static NpgsqlParameter Array(string name, NpgsqlDbType type, IEnumerable<string?> values)
        => new(name, NpgsqlDbType.Array | type) { Value = values.ToArray() };

    /// <summary>PostgreSQL text cannot hold a NUL character, and Discord will pass one through.</summary>
    private static string Clean(string? text) => (text ?? string.Empty).Replace("\0", string.Empty, StringComparison.Ordinal);

    private static string AttachmentsJson(IReadOnlyList<DiscordAttachmentSnapshot> attachments)
        => JsonSerializer.Serialize(attachments.Select(a => new
        {
            name = Clean(a.Name),
            type = a.Type,
            size = a.Size,
            url = a.Url,
        }));
}
