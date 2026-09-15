using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.AI.Chat;
using Modbot.Api.Features.DiscordMembers;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Chat.Tools;

/// <summary>
/// Reading the Discord side: messages, members and the link between somebody's two accounts.
/// </summary>
/// <remarks>
/// Messages need <see cref="ModbotPermissions.ReadDiscordMessages"/>, the same permission the
/// Messages tab of the Discord popup needs, and for the same reason: reading everything a member
/// wrote, deleted messages included, is a bigger thing to hand out than seeing that they were
/// timed out. A deleted message is returned, marked, because the deleted one is often the one a
/// moderator is asking about.
/// </remarks>
internal static class DiscordMessageRows
{
    /// <summary>Message text is cut to this, so a result is rows a model can read rather than a dump.</summary>
    public const int TextLength = 400;

    /// <summary>Channel and thread names for a page of messages, in two queries rather than two per row.</summary>
    public static async Task<(Dictionary<string, string> Channels, Dictionary<string, string> Threads)> NamesAsync(
        ModbotContext db, IReadOnlyList<DiscordMessage> rows, CancellationToken ct)
    {
        var channelIds = rows.Select(r => r.ChannelId).Distinct(StringComparer.Ordinal).ToList();
        var channels = channelIds.Count == 0
            ? []
            : await db.DiscordChannels.AsNoTracking()
                .Where(c => channelIds.Contains(c.ChannelId))
                .ToDictionaryAsync(c => c.ChannelId, c => c.Name, StringComparer.Ordinal, ct);

        // Threads are not in the channel list; the read-back row is the one place their names are kept.
        var threadIds = rows.Where(r => r.ThreadId != null).Select(r => r.ThreadId!).Distinct(StringComparer.Ordinal).ToList();
        var threads = threadIds.Count == 0
            ? []
            : await db.DiscordReadBacks.AsNoTracking()
                .Where(t => threadIds.Contains(t.ChannelId))
                .ToDictionaryAsync(t => t.ChannelId, t => t.Name, StringComparer.Ordinal, ct);

        return (channels, threads);
    }

    public static object Row(
        DiscordMessage m,
        IReadOnlyDictionary<string, string> channels,
        IReadOnlyDictionary<string, string> threads) => new
        {
            messageId = m.MessageId,
            m.SentAt,
            m.ChannelId,
            channelName = channels.GetValueOrDefault(m.ChannelId),
            m.ThreadId,
            threadName = m.ThreadId is { } thread ? threads.GetValueOrDefault(thread) : null,
            authorId = m.AuthorId,
            authorName = m.AuthorName,
            text = m.Text.Length <= TextLength ? m.Text : string.Concat(m.Text.AsSpan(0, TextLength), "…"),
            m.EmbedCount,
            m.ReplyToId,
            m.EditedAt,
            deleted = m.DeletedAt is not null,
            m.DeletedAt,
        };

    public static IEnumerable<ChatReference> References(IEnumerable<DiscordMessage> rows)
    {
        foreach (var m in rows)
        {
            yield return new ChatReference(ChatReference.Message, m.MessageId, Label(m), m.AuthorId);
            yield return new ChatReference(ChatReference.DiscordPerson, m.AuthorId, m.AuthorName);
        }
    }

    /// <summary>What a message's chip says: who wrote it, and the first words of it.</summary>
    private static string Label(DiscordMessage m)
    {
        var words = m.Text.Length <= 40 ? m.Text : string.Concat(m.Text.AsSpan(0, 40), "…");
        return words.Length == 0 ? $"{m.AuthorName}: (no text)" : $"{m.AuthorName}: {words}";
    }
}

/// <summary>Stored Discord messages, by who wrote them, which channel they are in, or their words.</summary>
internal sealed class SearchDiscordMessagesTool : ReadTool
{
    /// <summary>How far back a search on words alone looks when no dates are given.</summary>
    private const int DaysForWordsAlone = 90;

    public override string Name => "search_discord_messages";

    public override string Label => "Search Discord messages";

    public override string Description =>
        "Search the Discord messages Modbot has stored. Filter by who wrote it (discordUserId), the "
        + "channel or thread it is in (channelId), words in it (text) and a date range. Newest "
        + "first. Deleted messages come back marked deleted. Searching on words alone looks at the "
        + "last 90 days unless a date range is given.";

    protected override string Schema => """
        {"type":"object","properties":{"discordUserId":{"type":"string","description":"The Discord account that wrote them."},"channelId":{"type":"string","description":"The channel or thread id."},"text":{"type":"string","description":"Words to look for anywhere in the message."},"from":{"type":"string","description":"Earliest date or time, ISO 8601."},"to":{"type":"string","description":"Latest date or time, ISO 8601."},"limit":{"type":"integer","minimum":1,"maximum":50,"description":"Default 20."}}}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ReadDiscordMessages;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var author = ChatArguments.Text(arguments, "discordUserId");
        var channel = ChatArguments.Text(arguments, "channelId");
        var text = ChatArguments.Text(arguments, "text");

        if (author is null && channel is null && text is null)
            return ChatToolResult.Problem("Give at least one of discordUserId, channelId or text.");

        var limit = ChatArguments.Number(arguments, "limit", 20, 1, 50);
        var from = ChatTime.Of(ChatArguments.Text(arguments, "from"));
        var to = ChatTime.Of(ChatArguments.Text(arguments, "to"));

        // Words alone would otherwise read every partition of a table that only has indexes on the
        // channel, the thread and the author.
        if (author is null && channel is null && from is null && to is null)
            from = Get<IModbotClock>(context).UtcNow.AddDays(-DaysForWordsAlone);

        var db = Get<ModbotContext>(context);
        var guildId = await DiscordMemberEndpoints.GuildIdAsync(db, ct);

        var query = db.DiscordMessages.AsNoTracking().Where(m => guildId == null || m.GuildId == guildId);

        if (author is not null)
            query = query.Where(m => m.AuthorId == author);

        if (channel is not null)
            query = query.Where(m => m.ChannelId == channel || m.ThreadId == channel);

        if (text is not null)
        {
            var pattern = Contains(text);
            query = query.Where(m => EF.Functions.ILike(m.Text, pattern, "\\"));
        }

        if (from is { } after)
            query = query.Where(m => m.SentAt >= after);

        if (to is { } before)
            query = query.Where(m => m.SentAt <= before);

        var (rows, more) = FirstOf(
            await query
                .OrderByDescending(m => m.SentAt)
                .ThenByDescending(m => m.MessageId)
                .Take(limit + 1)
                .ToListAsync(ct),
            limit);

        var (channels, threads) = await DiscordMessageRows.NamesAsync(db, rows, ct);

        return ChatToolResult.Json(
            new
            {
                count = rows.Count,
                more,
                searchedFrom = from,
                messages = rows.Select(m => DiscordMessageRows.Row(m, channels, threads)),
            },
            DiscordMessageRows.References(rows));
    }
}

/// <summary>The messages either side of one message, so a line can be read in its conversation.</summary>
internal sealed class DiscordMessagesAroundTool : ReadTool
{
    public override string Name => "discord_messages_around";

    public override string Label => "Around a message";

    public override string Description =>
        "The messages sent just before and just after one message, in the same channel or thread, "
        + "oldest first. Use it to read a quoted line in context. Deleted messages come back marked "
        + "deleted.";

    protected override string Schema => """
        {"type":"object","properties":{"messageId":{"type":"string","description":"The Discord message id."},"before":{"type":"integer","minimum":0,"maximum":25,"description":"How many messages before it. Default 5."},"after":{"type":"integer","minimum":0,"maximum":25,"description":"How many messages after it. Default 5."}},"required":["messageId"]}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ReadDiscordMessages;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        if (ChatArguments.Text(arguments, "messageId") is not { } messageId)
            return ChatToolResult.Problem("messageId is required.");

        var before = ChatArguments.Number(arguments, "before", 5, 0, 25);
        var after = ChatArguments.Number(arguments, "after", 5, 0, 25);

        var db = Get<ModbotContext>(context);

        var middle = await db.DiscordMessages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.MessageId == messageId, ct);

        if (middle is null)
            return ChatToolResult.Problem("No stored message has that id.");

        // The thread a message is in, or the channel when it is not in one: the same run of talk.
        var place = middle.ThreadId ?? middle.ChannelId;

        var inPlace = db.DiscordMessages.AsNoTracking()
            .Where(m => m.GuildId == middle.GuildId && (m.ThreadId == place || (m.ThreadId == null && m.ChannelId == place)));

        var earlier = before == 0
            ? []
            : await inPlace
                .Where(m => m.SentAt < middle.SentAt || (m.SentAt == middle.SentAt && string.Compare(m.MessageId, messageId, StringComparison.Ordinal) < 0))
                .OrderByDescending(m => m.SentAt)
                .ThenByDescending(m => m.MessageId)
                .Take(before)
                .ToListAsync(ct);

        var later = after == 0
            ? []
            : await inPlace
                .Where(m => m.SentAt > middle.SentAt || (m.SentAt == middle.SentAt && string.Compare(m.MessageId, messageId, StringComparison.Ordinal) > 0))
                .OrderBy(m => m.SentAt)
                .ThenBy(m => m.MessageId)
                .Take(after)
                .ToListAsync(ct);

        List<DiscordMessage> rows = [.. earlier.AsEnumerable().Reverse(), middle, .. later];

        var (channels, threads) = await DiscordMessageRows.NamesAsync(db, rows, ct);

        return ChatToolResult.Json(
            new
            {
                asked = messageId,
                count = rows.Count,
                messages = rows.Select(m => DiscordMessageRows.Row(m, channels, threads)),
            },
            DiscordMessageRows.References(rows));
    }
}

/// <summary>One member of the Discord server, current or past.</summary>
internal sealed class GetDiscordMemberTool : ReadTool
{
    public override string Name => "get_discord_member";

    public override string Label => "Open a Discord member";

    public override string Description =>
        "What Modbot has stored about one member of the Discord server: names, roles, when they "
        + "joined, whether they left, whether they are timed out, and the VRChat account they are "
        + "linked to when one is known.";

    protected override string Schema => """
        {"type":"object","properties":{"discordUserId":{"type":"string","description":"The Discord account id."}},"required":["discordUserId"]}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ViewMembers;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        if (ChatArguments.Text(arguments, "discordUserId") is not { } id)
            return ChatToolResult.Problem("discordUserId is required.");

        // The same split the popup makes: which VRChat account this is belongs to See profiles.
        var seesLinks = ChatToolRegistry.Allows(context.Held, ModbotPermissions.ViewProfile);
        var member = await DiscordMemberEndpoints.OneAsync(Get<ModbotContext>(context), id, seesLinks, ct);

        if (member is null)
            return ChatToolResult.Problem("That person has not been seen in the Discord server.");

        return ChatToolResult.Json(
            DiscordMemberRows.Row(member),
            DiscordMemberRows.References([member]));
    }
}

/// <summary>The Discord members page's search, including by role.</summary>
internal sealed class SearchDiscordMembersTool : ReadTool
{
    public override string Name => "search_discord_members";

    public override string Label => "Search Discord members";

    public override string Description =>
        "Search the Discord server's members by part of a name or an account id, and list everyone "
        + "holding one role. Call with listRoles true to get the role ids and names first.";

    protected override string Schema => """
        {"type":"object","properties":{"query":{"type":"string","description":"Part of a name, or an account id."},"roleId":{"type":"string","description":"Only members holding this role."},"status":{"type":"string","enum":["in-server","left","all"],"description":"Default in-server."},"limit":{"type":"integer","minimum":1,"maximum":50,"description":"Default 20."},"listRoles":{"type":"boolean","description":"Only list the server's roles, with their ids."}}}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ViewMembers;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var db = Get<ModbotContext>(context);
        var clock = Get<IModbotClock>(context);
        var seesLinks = ChatToolRegistry.Allows(context.Held, ModbotPermissions.ViewProfile);

        var roles = arguments.ValueKind == JsonValueKind.Object
                    && arguments.TryGetProperty("listRoles", out var list)
                    && list.ValueKind == JsonValueKind.True;

        var limit = roles ? 1 : ChatArguments.Number(arguments, "limit", 20, 1, 50);
        var status = ChatArguments.Text(arguments, "status") is "left" or "all" ? ChatArguments.Text(arguments, "status") : null;

        var page = await DiscordMemberEndpoints.ListAsync(
            db,
            clock,
            ChatArguments.Text(arguments, "query"),
            status,
            ChatArguments.Text(arguments, "roleId"),
            linked: null,
            seesLinks,
            page: 1,
            pageSize: limit,
            ct);

        if (roles)
            return ChatToolResult.Json(new { roles = page.Roles.Select(r => new { roleId = r.Id, r.Name }) });

        return ChatToolResult.Json(
            new
            {
                matching = page.Total,
                more = page.Total > page.Members.Count,
                membersInServer = page.Coverage.InServer,
                listedAt = page.Coverage.ListedAt,
                members = page.Members.Select(DiscordMemberRows.Row),
            },
            DiscordMemberRows.References(page.Members));
    }
}

/// <summary>Who left the Discord server, most recent first.</summary>
internal sealed class DiscordMembersLeftTool : ReadTool
{
    public override string Name => "discord_members_left";

    public override string Label => "Who left Discord";

    public override string Description =>
        "The people who most recently left the Discord server, newest first, with when they joined "
        + "and when they left.";

    protected override string Schema => """
        {"type":"object","properties":{"days":{"type":"integer","minimum":1,"maximum":365,"description":"Only those who left in this many days. Leave out for the most recent whenever they left."},"limit":{"type":"integer","minimum":1,"maximum":50,"description":"Default 20."}}}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ViewMembers;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var limit = ChatArguments.Number(arguments, "limit", 20, 1, 50);
        var days = ChatArguments.Number(arguments, "days", 0, 0, 365);

        var db = Get<ModbotContext>(context);
        var now = Get<IModbotClock>(context).UtcNow;
        var guildId = await DiscordMemberEndpoints.GuildIdAsync(db, ct);

        var query = db.DiscordMembers.AsNoTracking()
            .Where(m => m.LeftAt != null && (guildId == null || m.GuildId == guildId));

        if (days > 0)
        {
            var since = now.AddDays(-days);
            query = query.Where(m => m.LeftAt >= since);
        }

        var (rows, more) = FirstOf(
            await query
                .OrderByDescending(m => m.LeftAt)
                .ThenBy(m => m.UserId)
                .Take(limit + 1)
                .Select(m => new { m.UserId, m.DisplayName, m.Username, m.JoinedAt, m.LeftAt, m.IsBot })
                .ToListAsync(ct),
            limit);

        return ChatToolResult.Json(
            new
            {
                count = rows.Count,
                more,
                people = rows.Select(m => new
                {
                    discordUserId = m.UserId,
                    m.DisplayName,
                    m.Username,
                    m.JoinedAt,
                    m.LeftAt,
                    m.IsBot,
                }),
            },
            rows.Select(m => DiscordPerson(m.UserId, m.DisplayName)));
    }
}

/// <summary>Whether somebody's two accounts are linked, and which account the other one is.</summary>
internal sealed class AccountLinkTool : ReadTool
{
    public override string Name => "get_account_link";

    public override string Label => "Account link";

    public override string Description =>
        "Whether a person's VRChat and Discord accounts are linked in Modbot, and the other "
        + "account when they are. Give either a VRChat user id or a Discord account id. Also says "
        + "when a link was ended and by whom.";

    protected override string Schema => """
        {"type":"object","properties":{"userId":{"type":"string","description":"A VRChat user id."},"discordUserId":{"type":"string","description":"A Discord account id."}}}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ViewProfile;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var vrchatUserId = ChatArguments.Text(arguments, "userId");
        var discordUserId = ChatArguments.Text(arguments, "discordUserId");

        if (vrchatUserId is null && discordUserId is null)
            return ChatToolResult.Problem("Give either userId or discordUserId.");

        var db = Get<ModbotContext>(context);

        var rows = await db.DiscordAccountLinks.AsNoTracking()
            .Where(l => (vrchatUserId != null && l.VRChatUserId == vrchatUserId)
                        || (discordUserId != null && l.DiscordUserId == discordUserId))
            .OrderByDescending(l => l.LinkedAt)
            .Take(10)
            .ToListAsync(ct);

        var active = rows.Find(l => l.UnlinkedAt == null);

        return ChatToolResult.Json(
            new
            {
                linked = active is not null,
                link = active is null ? null : new
                {
                    active.VRChatUserId,
                    vrchatDisplayName = active.VRChatDisplayName,
                    discordUserId = active.DiscordUserId,
                    discordUsername = active.DiscordUsername,
                    active.LinkedAt,
                    startedFrom = active.StartedFrom.ToString(),
                    notInServerAt = active.NotInServerAt,
                },
                past = rows.Where(l => l.UnlinkedAt != null).Select(l => new
                {
                    l.VRChatUserId,
                    discordUserId = l.DiscordUserId,
                    l.LinkedAt,
                    l.UnlinkedAt,
                    endedBy = l.UnlinkedBy?.ToString(),
                }),
            },
            [
                .. rows.Select(l => Person(l.VRChatUserId, l.VRChatDisplayName)),
                .. rows.Select(l => DiscordPerson(l.DiscordUserId, l.DiscordUsername)),
            ]);
    }
}

/// <summary>A Discord member, as the model is shown them, and what they open.</summary>
internal static class DiscordMemberRows
{
    public static object Row(DiscordMemberView m) => new
    {
        discordUserId = m.UserId,
        m.Username,
        m.DisplayName,
        roles = m.Roles.Select(r => new { roleId = r.Id, r.Name }),
        m.JoinedAt,
        m.LeftAt,
        m.TimedOutUntil,
        m.IsBot,
        m.IsPending,
        m.BoostingSince,
        m.FirstSeenAt,
        linkedVRChat = m.LinkedVRChat is null
            ? null
            : new { m.LinkedVRChat.UserId, m.LinkedVRChat.DisplayName },
    };

    public static IEnumerable<ChatReference> References(IEnumerable<DiscordMemberView> members)
    {
        foreach (var m in members)
        {
            yield return new ChatReference(ChatReference.DiscordPerson, m.UserId, m.DisplayName);

            if (m.LinkedVRChat is { } linked)
                yield return new ChatReference(ChatReference.Person, linked.UserId, linked.DisplayName);
        }
    }
}

/// <summary>Reading a date or time argument without trusting the model to have sent one.</summary>
internal static class ChatTime
{
    public static DateTimeOffset? Of(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;
}
