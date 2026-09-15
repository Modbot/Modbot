using System.Globalization;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Tests.Messages;

/// <summary>Messages and channels shaped the way the gateway hands them over.</summary>
internal static class MessageTestData
{
    public const string Guild = "424242";

    public static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// One message. Ids count up with time, as Discord's do; the send time is a minute per id past
    /// <see cref="Start"/>, unless given.
    /// </summary>
    public static DiscordMessageSnapshot Message(
        long id,
        string channel = "500",
        string? thread = null,
        string author = "900",
        string text = "hello",
        DateTimeOffset? sentAt = null,
        DateTimeOffset? editedAt = null,
        bool bot = false)
        => new(
            id.ToString(CultureInfo.InvariantCulture),
            Guild,
            channel,
            thread,
            author,
            "Person " + author,
            bot,
            sentAt ?? Start.AddMinutes(id),
            editedAt,
            text,
            [new DiscordAttachmentSnapshot("cat.png", "image/png", 1234, "https://cdn.discordapp.com/cat.png")],
            EmbedCount: 0,
            ReplyToId: null,
            MentionCount: 0,
            Pinned: false);

    /// <summary><paramref name="count"/> messages in one channel, ids 1 to count.</summary>
    public static List<DiscordMessageSnapshot> History(int count, string channel = "500", string? thread = null)
        => Enumerable.Range(1, count).Select(i => Message(i, channel, thread)).ToList();

    public static async Task AddChannelAsync(
        ModbotContext db,
        string id,
        string name,
        string type = DiscordChannelTypes.Text,
        bool canRead = true,
        int position = 0,
        CancellationToken ct = default)
    {
        db.DiscordChannels.Add(new DiscordChannel
        {
            ChannelId = id,
            GuildId = Guild,
            Name = name,
            Type = type,
            Position = position,
            BotCanView = true,
            BotCanReadHistory = canRead,
            BotCanSend = true,
        });

        await db.SaveChangesAsync(ct);
    }
}
