namespace Modbot.Discord.Gateway;

/// <summary>A file attached to a message. Only described; the file is never downloaded.</summary>
/// <param name="Type">The MIME type Discord reports, or null when it gives none.</param>
public sealed record DiscordAttachmentSnapshot(string Name, string? Type, long Size, string Url);

/// <summary>One message as the bot read it, in the shape Modbot stores.</summary>
/// <param name="ChannelId">The channel it is in. For a message in a thread, the thread's parent.</param>
/// <param name="ThreadId">The thread it is in, or null.</param>
/// <param name="AuthorIsBot">A bot or a webhook.</param>
/// <param name="Text">Empty when the message has no text, or the Message Content intent is off.</param>
/// <param name="EditedAt">When the text was last changed, or null if never.</param>
public sealed record DiscordMessageSnapshot(
    string Id,
    string GuildId,
    string ChannelId,
    string? ThreadId,
    string AuthorId,
    string AuthorName,
    bool AuthorIsBot,
    DateTimeOffset SentAt,
    DateTimeOffset? EditedAt,
    string Text,
    IReadOnlyList<DiscordAttachmentSnapshot> Attachments,
    int EmbedCount,
    string? ReplyToId,
    int MentionCount,
    bool Pinned)
{
    /// <summary>The channel or thread the message was read from: where a read-back page continues.</summary>
    public string ReadFrom => ThreadId ?? ChannelId;
}

/// <summary>
/// One page of a channel's history.
/// </summary>
/// <param name="Messages">
/// Newest first, whichever direction was read. Discord's own system lines are left out, so this
/// can hold fewer than the page did.
/// </param>
/// <param name="OldestId">The oldest message on the page, system lines included: where the next page back starts.</param>
/// <param name="NewestId">The newest message on the page, system lines included: where the next page forward starts.</param>
/// <param name="Full">
/// Discord gave a whole page. A short page means there is nothing further in that direction: the
/// channel's first message going back, or the newest going forward.
/// </param>
/// <param name="Error">What went wrong, as a sentence, or null.</param>
/// <param name="NoAccess">
/// True when reading can never work as things stand: the channel is gone, or the bot may not read
/// its history. The reader stops the channel instead of trying again.
/// </param>
public sealed record DiscordMessagePage(
    IReadOnlyList<DiscordMessageSnapshot> Messages,
    string? OldestId,
    string? NewestId,
    bool Full,
    string? Error = null,
    bool NoAccess = false)
{
    /// <summary>How many messages Discord gives in one request, at most.</summary>
    public const int Size = 100;

    public static DiscordMessagePage Failed(string error, bool noAccess) => new([], null, null, false, error, noAccess);
}

/// <summary>A thread the bot can read, open or archived.</summary>
public sealed record DiscordThreadSnapshot(string Id, string ParentChannelId, string Name, bool Archived);
