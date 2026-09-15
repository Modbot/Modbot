namespace Modbot.Core.Data.Entities;

/// <summary>
/// One message posted in the Discord server, stored in full. The table is <c>discord_message</c>,
/// partitioned monthly by <see cref="SentAt"/>.
/// </summary>
/// <remarks>
/// <para>
/// M5 spec §5.1, changed 2026-09-15: messages used to be counted and never stored. They are now
/// kept whole -- text, attachments, what they replied to -- because the server analytics are built
/// from them and AI moderation reads them.
/// </para>
/// <para>
/// <strong>Not a fact.</strong> A message is edited and deleted, and the row follows it: an edit
/// moves the old text to <see cref="DiscordMessageEdit"/> and replaces <see cref="Text"/>; a delete
/// sets <see cref="DeletedAt"/> and keeps everything else, because the deleted message is often the
/// one a moderator needs to read.
/// </para>
/// <para>
/// Partitioned by month for the same reason the fact log is: messages have their own retention
/// setting, and dropping a month's table is instant where deleting its rows is hours of vacuum.
/// The key is <c>(message_id, sent_at)</c> because PostgreSQL wants the partition key in every
/// unique constraint. A message's send time never changes, so the pair is as unique as the id.
/// </para>
/// </remarks>
public class DiscordMessage
{
    /// <summary>Discord's id for the message. Text, never parsed.</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>When it was posted, as Discord reports it.</summary>
    public DateTimeOffset SentAt { get; set; }

    public string GuildId { get; set; } = string.Empty;

    /// <summary>The channel it was posted in. For a message in a thread, the thread's parent channel.</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>The thread it was posted in, or null for a message straight in the channel.</summary>
    public string? ThreadId { get; set; }

    public string AuthorId { get; set; } = string.Empty;

    /// <summary>The author's name in the server when the message was stored. Untrusted text.</summary>
    public string AuthorName { get; set; } = string.Empty;

    /// <summary>Posted by a bot or a webhook. Kept, but left out of the member counts.</summary>
    public bool AuthorIsBot { get; set; }

    /// <summary>
    /// The text as it reads now. Empty when the message has none, and empty for every message when
    /// the Message Content intent is off -- Discord then sends the rest of the message without it.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// The files attached, as a <c>jsonb</c> array of <c>{name, type, size, url}</c>. The files
    /// themselves are not downloaded.
    /// </summary>
    public string Attachments { get; set; } = "[]";

    public int EmbedCount { get; set; }

    /// <summary>The message this one replied to, or null.</summary>
    public string? ReplyToId { get; set; }

    /// <summary>People and roles mentioned, plus one for @everyone or @here.</summary>
    public int MentionCount { get; set; }

    public bool Pinned { get; set; }

    /// <summary>When the text was last changed, or null if it never was.</summary>
    public DateTimeOffset? EditedAt { get; set; }

    /// <summary>When the message was deleted, or null while it is still there.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>When Modbot stored the row, from <c>IModbotClock</c>. Drives the daily totals.</summary>
    public DateTimeOffset StoredAt { get; set; }
}

/// <summary>
/// An earlier text of an edited message. The table is <c>discord_message_edit</c>, partitioned by
/// the message's <see cref="SentAt"/> so a month of messages and their edits are dropped together.
/// </summary>
public class DiscordMessageEdit
{
    public long Id { get; set; }

    public string MessageId { get; set; } = string.Empty;

    /// <summary>The message's own send time: the partition key, copied from the message.</summary>
    public DateTimeOffset SentAt { get; set; }

    /// <summary>The text before this edit.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>When this text was replaced -- the edit's time, or when Modbot saw it if Discord gave none.</summary>
    public DateTimeOffset ReplacedAt { get; set; }
}

/// <summary>
/// How far the bot has read back through one channel's or thread's history. The table is
/// <c>discord_read_back</c>.
/// </summary>
/// <remarks>
/// <para>
/// A channel is read newest first, a page of a hundred at a time, and stops at the channel's first
/// message or once <see cref="StoredPagesInARow"/> reaches three (M5 spec §5.1). Everything needed
/// to carry on after a restart is here: the oldest message read so far is where the next page
/// starts.
/// </para>
/// <para>
/// Separate from <see cref="DiscordChannel"/> because threads are read back too, and threads are not
/// in the channel list.
/// </para>
/// </remarks>
public class DiscordReadBack
{
    /// <summary>The channel or thread being read.</summary>
    public string ChannelId { get; set; } = string.Empty;

    public string GuildId { get; set; } = string.Empty;

    /// <summary>For a thread, the channel it sits in. Null for a channel.</summary>
    public string? ParentChannelId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The oldest message read so far -- the next page is the hundred before it. Null before the first page.</summary>
    public string? OldestReadId { get; set; }

    /// <summary>How many pages in a row held only messages already stored. Three stops the channel.</summary>
    public int StoredPagesInARow { get; set; }

    public int PagesRead { get; set; }

    /// <summary>Messages this read-back stored that were not stored before.</summary>
    public long MessagesStored { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When reading back stopped for good, or null while there is more to read.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>One of <see cref="DiscordReadBackStops"/> once finished.</summary>
    public string? StoppedBecause { get; set; }

    /// <summary>The last thing that went wrong reading this channel, as a sentence. Cleared by a page that works.</summary>
    public string? LastError { get; set; }
}

/// <summary>Why a channel's read-back stopped, as stored in <see cref="DiscordReadBack.StoppedBecause"/>.</summary>
public static class DiscordReadBackStops
{
    /// <summary>The channel's first message was reached.</summary>
    public const string Start = "start";

    /// <summary>Three pages in a row held only messages already stored.</summary>
    public const string AlreadyStored = "already-stored";

    /// <summary>The bot may not read the channel's history, or the channel is gone.</summary>
    public const string NoAccess = "no-access";
}
