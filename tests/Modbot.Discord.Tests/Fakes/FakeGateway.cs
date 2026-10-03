using Modbot.Discord.Gateway;

namespace Modbot.Discord.Tests.Fakes;

/// <summary>
/// A gateway that records what the bot asked of it and never opens a socket. Tests raise its
/// events by hand.
/// </summary>
public sealed class FakeGateway : IDiscordGateway
{
    private readonly Queue<DiscordPostOutcome> _outcomes = new();

    public DiscordGatewayState State { get; set; } = DiscordGatewayState.Disconnected;

    public event Func<Task>? Ready;

    public event Func<Task>? Resumed;

    public event Func<DiscordDisconnect, Task>? Disconnected;

    public event Func<DiscordCommandCall, Task>? CommandReceived;

    public event Func<string, DiscordChannelSnapshot, Task>? ChannelChanged;

    public event Func<string, string, Task>? ChannelRemoved;

    public event Func<string, Task>? ServerChanged;

    public event Func<DiscordMemberJoin, Task>? MemberJoined;

    public Task RaiseMemberJoinedAsync(DiscordMemberJoin member)
        => MemberJoined?.Invoke(member) ?? Task.CompletedTask;

    /// <summary>What the session was made with.</summary>
    public DiscordGatewayOptions Options { get; set; } = new();

    /// <summary>Every direct message sent, in order.</summary>
    public List<(string UserId, string Text, IReadOnlyList<DiscordLinkButton> Links)> DirectMessages { get; } = [];

    /// <summary>Every mention posted, in order.</summary>
    public List<(string ChannelId, string UserId, string Text, IReadOnlyList<DiscordLinkButton> Links)> Mentions { get; } = [];

    /// <summary>Set to make direct messages fail as closed DMs do.</summary>
    public bool DirectMessagesClosed { get; set; }

    /// <summary>Every role change, in order: added or removed, member, role.</summary>
    public List<(bool Added, string GuildId, string UserId, string RoleId)> RoleChanges { get; } = [];

    /// <summary>Members Discord will say are not in the server.</summary>
    public HashSet<string> NotInServer { get; } = [];

    /// <summary>Set to make every role change fail with this sentence.</summary>
    public string? RoleError { get; set; }

    public Task<DiscordPostOutcome> SendDirectMessageAsync(
        string userId, string text, IReadOnlyList<DiscordLinkButton>? links, CancellationToken ct)
    {
        if (DirectMessagesClosed)
        {
            return Task.FromResult(new DiscordPostOutcome(
                false, "That member does not accept direct messages.", Permanent: true, DirectMessagesClosed: true));
        }

        DirectMessages.Add((userId, text, links ?? []));
        return Task.FromResult(DiscordPostOutcome.Posted((_nextMessageId++).ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    public Task<DiscordPostOutcome> MentionAsync(
        string channelId, string userId, string text, IReadOnlyList<DiscordLinkButton>? links, CancellationToken ct)
    {
        var outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : null;
        if (outcome is { Sent: false })
            return Task.FromResult(outcome);

        Mentions.Add((channelId, userId, text, links ?? []));
        return Task.FromResult(DiscordPostOutcome.Posted((_nextMessageId++).ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>The action buttons on each direct message and mention, by the text it was sent with.</summary>
    public List<(string UserId, string Text, IReadOnlyList<DiscordActionButton> Actions)> ActionMessages { get; } = [];

    public async Task<DiscordPostOutcome> SendDirectMessageAsync(
        string userId,
        string text,
        IReadOnlyList<DiscordLinkButton>? links,
        IReadOnlyList<DiscordActionButton>? actions,
        CancellationToken ct)
    {
        var outcome = await SendDirectMessageAsync(userId, text, links, ct);
        if (outcome.Sent)
            ActionMessages.Add((userId, text, actions ?? []));

        return outcome;
    }

    public async Task<DiscordPostOutcome> MentionAsync(
        string channelId,
        string userId,
        string text,
        IReadOnlyList<DiscordLinkButton>? links,
        IReadOnlyList<DiscordActionButton>? actions,
        CancellationToken ct)
    {
        var outcome = await MentionAsync(channelId, userId, text, links, ct);
        if (outcome.Sent)
            ActionMessages.Add((userId, text, actions ?? []));

        return outcome;
    }

    /// <summary>Every message posted or rewritten with action buttons: channel, message, text, buttons.</summary>
    public List<(string ChannelId, string MessageId, string? Text, IReadOnlyList<DiscordActionButton> Actions)> ActionPosts { get; } = [];

    public Task<DiscordPostOutcome> PostWithActionsAsync(
        string channelId,
        string? text,
        IReadOnlyList<DiscordEmbedContent> embeds,
        IReadOnlyList<DiscordLinkButton>? links,
        IReadOnlyList<DiscordActionButton>? actions,
        CancellationToken ct)
    {
        var outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : null;
        if (outcome is { Sent: false })
            return Task.FromResult(outcome);

        var messageId = (_nextMessageId++).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Posts.Add((channelId, embeds));
        ActionPosts.Add((channelId, messageId, text, actions ?? []));
        return Task.FromResult(DiscordPostOutcome.Posted(messageId));
    }

    public Task<DiscordPostOutcome> EditWithActionsAsync(
        string channelId,
        string messageId,
        string? text,
        IReadOnlyList<DiscordEmbedContent> embeds,
        IReadOnlyList<DiscordLinkButton>? links,
        IReadOnlyList<DiscordActionButton>? actions,
        CancellationToken ct)
    {
        var outcome = _editOutcomes.Count > 0 ? _editOutcomes.Dequeue() : null;
        if (outcome is { Sent: false })
            return Task.FromResult(outcome);

        ActionPosts.Add((channelId, messageId, text, actions ?? []));
        return Task.FromResult(DiscordPostOutcome.Posted(messageId));
    }

    /// <summary>Every invite pause asked for: server and until when.</summary>
    public List<(string GuildId, DateTimeOffset Until)> InvitePauses { get; } = [];

    /// <summary>Set to make pausing invites fail with this sentence.</summary>
    public string? PauseInvitesError { get; set; }

    public Task<DiscordPostOutcome> PauseInvitesAsync(string guildId, DateTimeOffset until, CancellationToken ct)
    {
        if (PauseInvitesError is { } error)
            return Task.FromResult(DiscordPostOutcome.Failed(error, permanent: true));

        InvitePauses.Add((guildId, until));
        return Task.FromResult(DiscordPostOutcome.Ok);
    }

    public Task<DiscordRoleOutcome> AddRoleAsync(string guildId, string userId, string roleId, CancellationToken ct)
        => RoleAsync(true, guildId, userId, roleId);

    public Task<DiscordRoleOutcome> RemoveRoleAsync(string guildId, string userId, string roleId, CancellationToken ct)
        => RoleAsync(false, guildId, userId, roleId);

    public Task<DiscordRoleOutcome> ChangeRoleAsync(
        string guildId, string userId, string roleId, bool add, string reason, CancellationToken ct)
    {
        RoleReasons.Add(reason);
        return RoleAsync(add, guildId, userId, roleId);
    }

    /// <summary>The reason given for each role change made through <see cref="ChangeRoleAsync"/>.</summary>
    public List<string> RoleReasons { get; } = [];

    /// <summary>The bot's own Discord account id, as the sync reads it back off its own actions.</summary>
    public string? BotUserId { get; set; } = "999000999";

    /// <summary>Who owns the server, as the session sees it.</summary>
    public string? OwnerId { get; set; }

    public string? GuildOwnerId(string guildId) => OwnerId;

    /// <summary>Every ban, unban and removal asked for, in order.</summary>
    public List<(string Action, string GuildId, string UserId, string Reason)> Moderation { get; } = [];

    /// <summary>Set to make every ban, unban and removal fail as a missing permission does.</summary>
    public string? ModerationRefused { get; set; }

    /// <summary>Set to make every ban, unban and removal fail for some other reason.</summary>
    public string? ModerationError { get; set; }

    /// <summary>People Discord will say are already banned, or were never banned.</summary>
    public HashSet<string> NothingToDo { get; } = [];

    public Task<DiscordModerationOutcome> BanAsync(
        string guildId, string userId, string reason, int deleteMessageDays, CancellationToken ct)
        => ModerationAsync("ban", guildId, userId, reason);

    public Task<DiscordModerationOutcome> UnbanAsync(string guildId, string userId, string reason, CancellationToken ct)
        => ModerationAsync("unban", guildId, userId, reason);

    public Task<DiscordModerationOutcome> RemoveAsync(string guildId, string userId, string reason, CancellationToken ct)
        => ModerationAsync("remove", guildId, userId, reason);

    /// <summary>Who the Discord server has banned. Null makes the ban list unreadable.</summary>
    public List<string>? Banned { get; set; } = [];

    /// <summary>The reason each banned id carries in the ban list, where a test gives one.</summary>
    public Dictionary<string, string> BanReasons { get; } = [];

    /// <summary>How many times the whole ban list was read.</summary>
    public int BanListReads { get; private set; }

    public Task<IReadOnlyList<DiscordBanSnapshot>?> ReadBansAsync(string guildId, CancellationToken ct)
    {
        BanListReads++;
        return Task.FromResult<IReadOnlyList<DiscordBanSnapshot>?>(Banned?
            .Select(id => new DiscordBanSnapshot(id, $"user{id}", Reason: BanReasons.GetValueOrDefault(id)))
            .ToList());
    }

    /// <summary>Members as a live read sees them, by id, for <see cref="RemoveCheckedAsync"/>. Missing: no roles.</summary>
    public Dictionary<string, DiscordMemberSnapshot> LiveMembers { get; } = new(StringComparer.Ordinal);

    public async Task<DiscordCheckedRemoval> RemoveCheckedAsync(
        string guildId, string userId, string reason, Func<DiscordMemberSnapshot, bool> mayRemove, CancellationToken ct)
    {
        if (NotInServer.Contains(userId))
            return new DiscordCheckedRemoval(DiscordModerationOutcome.Already);

        var member = LiveMembers.GetValueOrDefault(userId)
                     ?? new DiscordMemberSnapshot(userId, "member", "member", null, false, null, [], null);

        if (!mayRemove(member))
            return new DiscordCheckedRemoval(DiscordModerationOutcome.Already, Kept: true, Member: member);

        return new DiscordCheckedRemoval(await ModerationAsync("remove", guildId, userId, reason), Member: member);
    }

    private Task<DiscordModerationOutcome> ModerationAsync(string action, string guildId, string userId, string reason)
    {
        if (ModerationRefused is { } refused)
            return Task.FromResult(DiscordModerationOutcome.Refused(refused));

        if (ModerationError is { } error)
            return Task.FromResult(DiscordModerationOutcome.Failed(error));

        if (NothingToDo.Contains(userId))
            return Task.FromResult(DiscordModerationOutcome.Already);

        Moderation.Add((action, guildId, userId, reason));
        return Task.FromResult(DiscordModerationOutcome.Ok);
    }

    private Task<DiscordRoleOutcome> RoleAsync(bool added, string guildId, string userId, string roleId)
    {
        if (NotInServer.Contains(userId))
            return Task.FromResult(DiscordRoleOutcome.MemberNotInServer);

        if (RoleError is { } error)
            return Task.FromResult(DiscordRoleOutcome.Failed(error));

        RoleChanges.Add((added, guildId, userId, roleId));
        return Task.FromResult(DiscordRoleOutcome.Ok);
    }

    /// <summary>What <see cref="ReadServer"/> answers. Null means the bot is not in the server.</summary>
    public DiscordServerSnapshot? Server { get; set; }

    public int ReadServerCalls { get; private set; }

    public DiscordServerSnapshot? ReadServer(string guildId)
    {
        ReadServerCalls++;
        return Server is { } server && server.GuildId == guildId ? server : null;
    }

    public Task RaiseChannelChangedAsync(string guildId, DiscordChannelSnapshot channel)
        => ChannelChanged?.Invoke(guildId, channel) ?? Task.CompletedTask;

    public Task RaiseChannelRemovedAsync(string guildId, string channelId)
        => ChannelRemoved?.Invoke(guildId, channelId) ?? Task.CompletedTask;

    public Task RaiseServerChangedAsync(string guildId)
        => ServerChanged?.Invoke(guildId) ?? Task.CompletedTask;

    public List<(string ChannelId, IReadOnlyList<DiscordEmbedContent> Embeds)> Posts { get; } = [];

    /// <summary>Every message posted with a line of text above it, in order, with its id.</summary>
    public List<(string ChannelId, string MessageId, string? Text, IReadOnlyList<DiscordEmbedContent> Embeds, IReadOnlyList<DiscordLinkButton> Links)> Messages { get; } = [];

    /// <summary>Every rewrite, in order. The message id says which card was changed.</summary>
    public List<(string ChannelId, string MessageId, string? Text, IReadOnlyList<DiscordEmbedContent> Embeds, IReadOnlyList<DiscordLinkButton> Links)> Edits { get; } = [];

    /// <summary>
    /// The pictures sent with each message, by message id, so a test can see what was uploaded and
    /// what was not.
    /// </summary>
    /// <remarks>
    /// An edit records what it was given, which is null when the caller left the message's files
    /// alone -- the whole point of a card that is rewritten every minute. An edit key is the
    /// message id with the number of the edit after it, so one message's edits do not overwrite
    /// each other.
    /// </remarks>
    public Dictionary<string, IReadOnlyList<DiscordPicture>?> PicturesSent { get; } = new(StringComparer.Ordinal);

    /// <summary>The pictures each rewrite was given, in order. Null means "keep what is there".</summary>
    public List<IReadOnlyList<DiscordPicture>?> EditPictures { get; } = [];

    private int _nextMessageId = 1000;

    public List<string> Tokens { get; } = [];

    public string? RegisteredGuildId { get; private set; }

    public IReadOnlyList<DiscordCommandDefinition> RegisteredCommands { get; private set; } = [];

    public int RegisterCalls { get; private set; }

    public bool Disposed { get; private set; }

    public bool DisconnectCalled { get; private set; }

    /// <summary>Set to make the next <see cref="ConnectAsync"/> throw with this message.</summary>
    public string? ConnectError { get; set; }

    /// <summary>Set to make <see cref="RegisterGuildCommandsAsync"/> throw.</summary>
    public string? RegisterError { get; set; }

    /// <summary>Whether <see cref="ConnectAsync"/> moves straight to <c>Ready</c> or waits for the test.</summary>
    public bool ReadyOnConnect { get; set; }

    public void FailNextPost(string error, bool permanent = false)
        => _outcomes.Enqueue(DiscordPostOutcome.Failed(error, permanent));

    /// <summary>Makes the next rewrite fail. Separate queue: posting and editing fail apart.</summary>
    public void FailNextEdit(string error, bool permanent = false, bool notFound = false)
        => _editOutcomes.Enqueue(DiscordPostOutcome.Failed(error, permanent, notFound));

    private readonly Queue<DiscordPostOutcome> _editOutcomes = new();

    public Task ConnectAsync(string token, CancellationToken ct)
    {
        Tokens.Add(token);

        if (ConnectError is { } error)
            throw new InvalidOperationException(error);

        State = ReadyOnConnect ? DiscordGatewayState.Ready : DiscordGatewayState.Connecting;
        return Task.CompletedTask;
    }

    public Task<int> RegisterGuildCommandsAsync(
        string guildId, IReadOnlyList<DiscordCommandDefinition> commands, CancellationToken ct)
    {
        RegisterCalls++;

        if (RegisterError is { } error)
            throw new InvalidOperationException(error);

        RegisteredGuildId = guildId;
        RegisteredCommands = commands;
        return Task.FromResult(commands.Count);
    }

    public Task<DiscordPostOutcome> PostAsync(
        string channelId, IReadOnlyList<DiscordEmbedContent> embeds, CancellationToken ct)
    {
        var outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : DiscordPostOutcome.Ok;

        if (outcome.Sent)
            Posts.Add((channelId, embeds));

        return Task.FromResult(outcome);
    }

    public Task<DiscordPostOutcome> PostAsync(
        string channelId,
        string? text,
        IReadOnlyList<DiscordEmbedContent> embeds,
        IReadOnlyList<DiscordLinkButton>? links,
        CancellationToken ct) =>
        PostAsync(channelId, text, embeds, links, pictures: null, ct);

    public Task<DiscordPostOutcome> PostAsync(
        string channelId,
        string? text,
        IReadOnlyList<DiscordEmbedContent> embeds,
        IReadOnlyList<DiscordLinkButton>? links,
        IReadOnlyList<DiscordPicture>? pictures,
        CancellationToken ct) =>
        PostAsync(channelId, text, embeds, links, pictures, actions: null, ct);

    public Task<DiscordPostOutcome> PostAsync(
        string channelId,
        string? text,
        IReadOnlyList<DiscordEmbedContent> embeds,
        IReadOnlyList<DiscordLinkButton>? links,
        IReadOnlyList<DiscordPicture>? pictures,
        IReadOnlyList<DiscordActionButton>? actions,
        CancellationToken ct)
    {
        var outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : null;

        if (outcome is { Sent: false })
            return Task.FromResult(outcome);

        var messageId = (_nextMessageId++).ToString(System.Globalization.CultureInfo.InvariantCulture);

        Posts.Add((channelId, embeds));
        Messages.Add((channelId, messageId, text, embeds, links ?? []));
        PicturesSent[messageId] = pictures;
        ActionsSent[messageId] = actions ?? [];

        return Task.FromResult(DiscordPostOutcome.Posted(messageId));
    }

    /// <summary>Every message posted that pinged a role, in order: the only kind that pings one.</summary>
    public List<(string ChannelId, string MessageId, string RoleId)> RolePings { get; } = [];

    public Task<DiscordPostOutcome> PostMentioningRoleAsync(
        string channelId,
        string roleId,
        IReadOnlyList<DiscordEmbedContent> embeds,
        IReadOnlyList<DiscordLinkButton>? links,
        IReadOnlyList<DiscordPicture>? pictures,
        CancellationToken ct)
    {
        var outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : null;

        if (outcome is { Sent: false })
            return Task.FromResult(outcome);

        var messageId = (_nextMessageId++).ToString(System.Globalization.CultureInfo.InvariantCulture);

        Posts.Add((channelId, embeds));
        Messages.Add((channelId, messageId, $"<@&{roleId}>", embeds, links ?? []));
        PicturesSent[messageId] = pictures;
        RolePings.Add((channelId, messageId, roleId));

        return Task.FromResult(DiscordPostOutcome.Posted(messageId));
    }

    /// <summary>The buttons the bot answers that each posted message carried, by message id.</summary>
    public Dictionary<string, IReadOnlyList<DiscordActionButton>> ActionsSent { get; } = new(StringComparer.Ordinal);

    /// <summary>Every card marked as dealt with, in order.</summary>
    public List<(string ChannelId, string MessageId, string Line, string RemoveButtonsStarting)> Handled { get; } = [];

    public Task<DiscordPostOutcome> MarkHandledAsync(
        string channelId, string messageId, string line, string removeButtonsStarting, CancellationToken ct)
    {
        Handled.Add((channelId, messageId, line, removeButtonsStarting));
        return Task.FromResult(DiscordPostOutcome.Posted(messageId));
    }

    public event Func<DiscordFormSubmit, Task>? FormSubmitted;

    public Task RaiseFormAsync(DiscordFormSubmit submit)
        => FormSubmitted?.Invoke(submit) ?? Task.CompletedTask;

    public Task<DiscordPostOutcome> EditAsync(
        string channelId,
        string messageId,
        string? text,
        IReadOnlyList<DiscordEmbedContent> embeds,
        IReadOnlyList<DiscordLinkButton>? links,
        CancellationToken ct) =>
        EditAsync(channelId, messageId, text, embeds, links, pictures: null, ct);

    public Task<DiscordPostOutcome> EditAsync(
        string channelId,
        string messageId,
        string? text,
        IReadOnlyList<DiscordEmbedContent> embeds,
        IReadOnlyList<DiscordLinkButton>? links,
        IReadOnlyList<DiscordPicture>? pictures,
        CancellationToken ct)
    {
        var outcome = _editOutcomes.Count > 0 ? _editOutcomes.Dequeue() : null;

        if (outcome is { Sent: false })
            return Task.FromResult(outcome);

        Edits.Add((channelId, messageId, text, embeds, links ?? []));
        EditPictures.Add(pictures);

        return Task.FromResult(DiscordPostOutcome.Posted(messageId));
    }

    /// <summary>Every message deleted, in order.</summary>
    public List<(string ChannelId, string MessageId, string Reason)> Deleted { get; } = [];

    /// <summary>Every timeout, in order.</summary>
    public List<(string GuildId, string UserId, TimeSpan Duration, string Reason)> TimedOut { get; } = [];

    /// <summary>
    /// Makes the next delete fail. Its own queue, apart from posting and editing. <paramref name="notFound"/>
    /// is Discord's 404, the post is already gone; <paramref name="permanent"/> alone is a refusal (403).
    /// </summary>
    public void FailNextDelete(string error, bool permanent = false, bool notFound = false)
        => _deleteOutcomes.Enqueue(DiscordPostOutcome.Failed(error, permanent || notFound, notFound));

    private readonly Queue<DiscordPostOutcome> _deleteOutcomes = new();

    public Task<DiscordPostOutcome> DeleteMessageAsync(string channelId, string messageId, string reason, CancellationToken ct)
    {
        if (_deleteOutcomes.Count > 0)
            return Task.FromResult(_deleteOutcomes.Dequeue());

        Deleted.Add((channelId, messageId, reason));
        return Task.FromResult(DiscordPostOutcome.Ok);
    }

    // ── Posts ────────────────────────────────────────────────────────────────────────────────

    /// <summary>One post the fake was asked to send.</summary>
    public sealed record SentPost(string ChannelId, string? MessageId, string Text, string? RoleId, IReadOnlyList<DiscordPicture> Pictures);

    /// <summary>Every post send asked for, in order, whatever came of it.</summary>
    public List<SentPost> PostSends { get; } = [];

    /// <summary>Every post edit, in order: channel, message, the new text.</summary>
    public List<(string ChannelId, string MessageId, string Text)> PostEdits { get; } = [];

    /// <summary>Every publish to followers, in order.</summary>
    public List<(string ChannelId, string MessageId)> Published { get; } = [];

    /// <summary>Every look asked for, in order: the channel, the id it read after, and how many.</summary>
    public List<(string ChannelId, string? AfterId, int Limit)> RecentReads { get; } = [];

    /// <summary>The time the fake's message ids are made from, so a look after a time finds them.</summary>
    public Func<DateTimeOffset> PostClock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Set to make every publish fail with this sentence.</summary>
    public string? PublishError { get; set; }

    private readonly Queue<(DiscordPostOutcome Answer, bool Landed)> _postAnswers = new();

    private ulong _postSequence;

    /// <summary>The next post send answers this, and nothing lands in the channel.</summary>
    public void AnswerNextPost(DiscordPostOutcome answer) => _postAnswers.Enqueue((answer, false));

    /// <summary>
    /// The next post send gets no clear answer. With <paramref name="landed"/> the message is in the
    /// channel anyway, the way a timeout after Discord took it looks.
    /// </summary>
    public void NoClearAnswerToNextPost(bool landed) =>
        _postAnswers.Enqueue((DiscordPostOutcome.NoClearAnswer("Discord gave no answer: timed out"), landed));

    public Task<DiscordPostOutcome> SendPostAsync(
        string channelId, string text, string? roleId, IReadOnlyList<DiscordPicture>? pictures, CancellationToken ct)
    {
        var files = pictures ?? [];

        if (_postAnswers.Count > 0)
        {
            var (answer, landed) = _postAnswers.Dequeue();
            var landedId = landed ? Land(channelId, text, files) : null;
            PostSends.Add(new SentPost(channelId, landedId, text, roleId, files));
            return Task.FromResult(answer);
        }

        var messageId = Land(channelId, text, files);
        PostSends.Add(new SentPost(channelId, messageId, text, roleId, files));
        return Task.FromResult(DiscordPostOutcome.Posted(messageId));
    }

    /// <summary>Puts a message by the bot in the channel's history, with an id made from <see cref="PostClock"/>.</summary>
    public string Land(string channelId, string text, IReadOnlyList<DiscordPicture> files, string? authorId = null)
    {
        var id = (ulong.Parse(Modbot.Core.Posts.PostRules.DiscordIdAt(PostClock()), System.Globalization.CultureInfo.InvariantCulture)
                  + ++_postSequence).ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (!History.TryGetValue(channelId, out var list))
            History[channelId] = list = [];

        list.Add(new DiscordMessageSnapshot(
            id,
            "111111111111111111",
            channelId,
            null,
            authorId ?? BotUserId ?? "999000999",
            "Modbot",
            true,
            PostClock(),
            null,
            text,
            files.Select(f => new DiscordAttachmentSnapshot(f.Name, "image/png", f.Bytes.Length, "https://cdn.discordapp.com/" + f.Name)).ToList(),
            0,
            null,
            0,
            false));

        return id;
    }

    public Task<DiscordPostOutcome> EditPostAsync(string channelId, string messageId, string text, CancellationToken ct)
    {
        var outcome = _editOutcomes.Count > 0 ? _editOutcomes.Dequeue() : null;
        if (outcome is { Sent: false })
            return Task.FromResult(outcome);

        PostEdits.Add((channelId, messageId, text));
        return Task.FromResult(DiscordPostOutcome.Posted(messageId));
    }

    public Task<DiscordPostOutcome> PublishAsync(string channelId, string messageId, CancellationToken ct)
    {
        if (PublishError is { } error)
            return Task.FromResult(DiscordPostOutcome.Failed(error, permanent: true));

        Published.Add((channelId, messageId));
        return Task.FromResult(DiscordPostOutcome.Ok);
    }

    public Task<DiscordMessagePage> ReadRecentAsync(string channelId, string? afterMessageId, int limit, CancellationToken ct)
    {
        RecentReads.Add((channelId, afterMessageId, limit));

        if (NoAccess.Contains(channelId))
            return Task.FromResult(DiscordMessagePage.Failed("The bot may not read this channel's history.", noAccess: true));

        var page = (History.TryGetValue(channelId, out var list) ? list : [])
            .Where(m => afterMessageId is null || Number(m.Id) > Number(afterMessageId))
            .OrderBy(m => Number(m.Id))
            .Take(limit)
            .OrderByDescending(m => Number(m.Id))
            .ToList();

        return Task.FromResult(new DiscordMessagePage(
            page,
            page.Count > 0 ? page[^1].Id : null,
            page.Count > 0 ? page[0].Id : null,
            Full: page.Count >= limit));
    }

    // ── Server events ────────────────────────────────────────────────────────────────────────

    /// <summary>A server event as the fake holds it: what it says, and whether it started or ended.</summary>
    public sealed record FakeServerEvent(string GuildId, string Id, DiscordScheduledEventDetails Details, bool Started, bool Ended);

    /// <summary>Every server event the bot made, by id, as it stands now.</summary>
    public Dictionary<string, FakeServerEvent> ServerEvents { get; } = new(StringComparer.Ordinal);

    /// <summary>Every create, update and end, in order: <c>create</c>, <c>update</c> or <c>end</c>, and the event id.</summary>
    public List<(string Action, string EventId)> ServerEventCalls { get; } = [];

    /// <summary>Set to make every server event call fail with this sentence.</summary>
    public string? ServerEventError { get; set; }

    public Task<DiscordPostOutcome> CreateEventAsync(string guildId, DiscordScheduledEventDetails details, CancellationToken ct)
    {
        if (ServerEventError is { } error)
            return Task.FromResult(DiscordPostOutcome.Failed(error, permanent: true));

        var id = (_nextMessageId++).ToString(System.Globalization.CultureInfo.InvariantCulture);
        ServerEvents[id] = new FakeServerEvent(guildId, id, details, Started: false, Ended: false);
        ServerEventCalls.Add(("create", id));

        return Task.FromResult(DiscordPostOutcome.Posted(id));
    }

    public Task<DiscordPostOutcome> UpdateEventAsync(
        string guildId, string eventId, DiscordScheduledEventDetails details, bool start, CancellationToken ct)
    {
        if (ServerEventError is { } error)
            return Task.FromResult(DiscordPostOutcome.Failed(error, permanent: true));

        if (!ServerEvents.TryGetValue(eventId, out var existing) || existing.Ended)
            return Task.FromResult(DiscordPostOutcome.Failed(DiscordScheduledEventDetails.Gone, permanent: true));

        ServerEvents[eventId] = existing with { Details = details, Started = existing.Started || start };
        ServerEventCalls.Add(("update", eventId));

        return Task.FromResult(DiscordPostOutcome.Posted(eventId));
    }

    public Task<DiscordPostOutcome> EndEventAsync(string guildId, string eventId, CancellationToken ct)
    {
        if (ServerEvents.TryGetValue(eventId, out var existing))
            ServerEvents[eventId] = existing with { Ended = true };

        ServerEventCalls.Add(("end", eventId));
        return Task.FromResult(DiscordPostOutcome.Ok);
    }

    public Task<DiscordPostOutcome> TimeOutAsync(string guildId, string userId, TimeSpan duration, string reason, CancellationToken ct)
    {
        TimedOut.Add((guildId, userId, duration, reason));
        return Task.FromResult(DiscordPostOutcome.Ok);
    }

    public Task DisconnectAsync()
    {
        DisconnectCalled = true;
        State = DiscordGatewayState.Disconnected;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        State = DiscordGatewayState.Disconnected;
        return ValueTask.CompletedTask;
    }

    public Task RaiseReadyAsync()
    {
        State = DiscordGatewayState.Ready;
        return Ready?.Invoke() ?? Task.CompletedTask;
    }

    /// <summary>The session came back without a fresh sign-in, which raises no Ready.</summary>
    public Task RaiseResumedAsync()
    {
        State = DiscordGatewayState.Ready;
        return Resumed?.Invoke() ?? Task.CompletedTask;
    }

    public Task RaiseDisconnectedAsync(string reason, bool fatal = false)
    {
        State = DiscordGatewayState.Disconnected;
        return Disconnected?.Invoke(new DiscordDisconnect(reason, fatal)) ?? Task.CompletedTask;
    }

    /// <summary>Discord refused a privileged intent, as close code 4014 reads.</summary>
    public Task RaiseIntentsRefusedAsync()
    {
        State = DiscordGatewayState.Disconnected;
        return Disconnected?.Invoke(new DiscordDisconnect("Discord refused the Server Members intent.", Fatal: true, IntentsRefused: true))
            ?? Task.CompletedTask;
    }

    public Task RaiseCommandAsync(DiscordCommandCall call)
        => CommandReceived?.Invoke(call) ?? Task.CompletedTask;

    public event Func<DiscordButtonPress, Task>? ButtonPressed;

    public Task RaiseButtonAsync(DiscordButtonPress press)
        => ButtonPressed?.Invoke(press) ?? Task.CompletedTask;

    public event Func<DiscordSuggestionAsk, Task>? SuggestionAsked;

    public Task RaiseSuggestionAskedAsync(DiscordSuggestionAsk ask)
        => SuggestionAsked?.Invoke(ask) ?? Task.CompletedTask;

    // ── Messages ─────────────────────────────────────────────────────────────────────────────

    public event Func<DiscordMessageSnapshot, Task>? MessageReceived;

    public event Func<DiscordMessageSnapshot, Task>? MessageEdited;

    public event Func<string, string, IReadOnlyList<string>, Task>? MessagesDeleted;

    /// <summary>Each channel's or thread's history, in any order. Ids are numbers, as Discord's are.</summary>
    public Dictionary<string, List<DiscordMessageSnapshot>> History { get; } = new(StringComparer.Ordinal);

    /// <summary>Channels that answer as if the bot may not read them.</summary>
    public HashSet<string> NoAccess { get; } = new(StringComparer.Ordinal);

    /// <summary>Every page asked for, in order: the channel, and the before and after ids.</summary>
    public List<(string ChannelId, string? BeforeId, string? AfterId)> Reads { get; } = [];

    public List<DiscordThreadSnapshot> Threads { get; } = [];

    /// <summary>Every call to list threads: which channels had their archived threads listed.</summary>
    public List<IReadOnlyList<string>> ArchivedListings { get; } = [];

    private static ulong Number(string id) => ulong.Parse(id, System.Globalization.CultureInfo.InvariantCulture);

    public Task<DiscordMessagePage> ReadMessagesAsync(string channelId, string? beforeId, string? afterId, CancellationToken ct)
    {
        Reads.Add((channelId, beforeId, afterId));

        if (NoAccess.Contains(channelId))
            return Task.FromResult(DiscordMessagePage.Failed("The bot may not read this channel's history.", noAccess: true));

        var ordered = (History.TryGetValue(channelId, out var list) ? list : [])
            .OrderByDescending(m => Number(m.Id))
            .ToList();

        var page = beforeId is not null
            ? ordered.Where(m => Number(m.Id) < Number(beforeId)).Take(DiscordMessagePage.Size).ToList()
            : afterId is not null
                ? ordered.Where(m => Number(m.Id) > Number(afterId)).TakeLast(DiscordMessagePage.Size).ToList()
                : ordered.Take(DiscordMessagePage.Size).ToList();

        return Task.FromResult(new DiscordMessagePage(
            page,
            page.Count > 0 ? page[^1].Id : null,
            page.Count > 0 ? page[0].Id : null,
            Full: page.Count >= DiscordMessagePage.Size));
    }

    public Task<IReadOnlyList<DiscordThreadSnapshot>> ReadThreadsAsync(
        string guildId, IReadOnlyList<string> channelIds, IReadOnlyList<string> archivedIn, CancellationToken ct)
    {
        ArchivedListings.Add(archivedIn.ToList());

        var wanted = channelIds.ToHashSet(StringComparer.Ordinal);
        var listed = archivedIn.ToHashSet(StringComparer.Ordinal);

        return Task.FromResult<IReadOnlyList<DiscordThreadSnapshot>>(Threads
            .Where(t => wanted.Contains(t.ParentChannelId) && (!t.Archived || listed.Contains(t.ParentChannelId)))
            .ToList());
    }

    public Task RaiseMessageAsync(DiscordMessageSnapshot message)
        => MessageReceived?.Invoke(message) ?? Task.CompletedTask;

    public Task RaiseMessageEditedAsync(DiscordMessageSnapshot message)
        => MessageEdited?.Invoke(message) ?? Task.CompletedTask;

    public Task RaiseMessagesDeletedAsync(string guildId, string channelId, params string[] ids)
        => MessagesDeleted?.Invoke(guildId, channelId, ids) ?? Task.CompletedTask;

    // ── Reactions ────────────────────────────────────────────────────────────────────────────

    public event Func<DiscordReactionSnapshot, Task>? ReactionAdded;

    public event Func<DiscordReactionSnapshot, Task>? ReactionRemoved;

    /// <summary>Every reaction the bot put on a message itself, in order.</summary>
    public List<(string ChannelId, string MessageId, string Emoji)> OwnReactions { get; } = [];

    /// <summary>Set to make adding a reaction fail with this sentence.</summary>
    public string? ReactionError { get; set; }

    public Task<DiscordPostOutcome> AddReactionAsync(
        string channelId, string messageId, string emoji, CancellationToken ct)
    {
        if (ReactionError is { } error)
            return Task.FromResult(DiscordPostOutcome.Failed(error, permanent: true));

        OwnReactions.Add((channelId, messageId, emoji));
        return Task.FromResult(DiscordPostOutcome.Ok);
    }

    public Task RaiseReactionAddedAsync(DiscordReactionSnapshot reaction)
        => ReactionAdded?.Invoke(reaction) ?? Task.CompletedTask;

    public Task RaiseReactionRemovedAsync(DiscordReactionSnapshot reaction)
        => ReactionRemoved?.Invoke(reaction) ?? Task.CompletedTask;

    // ── Members, voice and moderation ────────────────────────────────────────────────────────

    public event Func<string, string, Task>? MemberLeft;

    public event Func<string, DiscordMemberSnapshot, Task>? MemberUpdated;

    public event Func<string, string, Task>? MemberBanned;

    public event Func<string, string, Task>? MemberUnbanned;

    public event Func<string, string, string?, string?, Task>? VoiceChanged;

    public event Func<string, Task>? AuditLogChanged;

    /// <summary>What <see cref="ReadMembersAsync"/> answers. Null means the list could not be had.</summary>
    public List<DiscordMemberSnapshot>? Members { get; set; }

    public List<DiscordVoiceState> Voice { get; set; } = [];

    /// <summary>The whole audit log, any order. Ids are numbers, as Discord's are.</summary>
    public List<DiscordAuditEntry> AuditLog { get; } = [];

    public bool AuditLogNoAccess { get; set; }

    public Task<DiscordAuditPage> ReadAuditLogAsync(string guildId, string? afterId, CancellationToken ct)
    {
        if (AuditLogNoAccess)
            return Task.FromResult(new DiscordAuditPage([], null, NoAccess: true, Error: "The bot may not read the audit log."));

        var entries = AuditLog
            .Where(e => afterId is null || Number(e.Id) > Number(afterId))
            .OrderBy(e => Number(e.Id))
            .ToList();

        return Task.FromResult(new DiscordAuditPage(entries, entries.Count > 0 ? entries[^1].Id : null));
    }

    public Task<IReadOnlyList<DiscordMemberSnapshot>?> ReadMembersAsync(string guildId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<DiscordMemberSnapshot>?>(Options.MemberEvents ? Members?.ToList() : null);

    public IReadOnlyList<DiscordVoiceState> ReadVoice(string guildId) => Voice.ToList();

    public int? ReadMemberCount(string guildId) => Members?.Count;

    public int? Online { get; set; }

    public int OnlineReads { get; private set; }

    public Task<int?> ReadOnlineCountAsync(string guildId, CancellationToken ct)
    {
        OnlineReads++;
        return Task.FromResult(Online);
    }

    /// <summary>What <see cref="ReadServerEventsAsync"/> answers. Null means Discord did not answer.</summary>
    public List<Core.Discord.DiscordServerEvent>? ServerEventList { get; set; } = [];

    public int ServerEventReads { get; private set; }

    public Task<IReadOnlyList<Core.Discord.DiscordServerEvent>?> ReadServerEventsAsync(string guildId, CancellationToken ct)
    {
        ServerEventReads++;
        return Task.FromResult<IReadOnlyList<Core.Discord.DiscordServerEvent>?>(ServerEventList?.ToList());
    }

    public Task RaiseMemberJoinedAsync(string guildId, DiscordMemberSnapshot member)
    {
        Members?.Add(member);
        return RaiseMemberJoinedAsync(new DiscordMemberJoin(guildId, member.UserId, member.Username, member.IsBot, member));
    }

    public Task RaiseMemberLeftAsync(string guildId, string userId)
    {
        Members?.RemoveAll(m => m.UserId == userId);
        return MemberLeft?.Invoke(guildId, userId) ?? Task.CompletedTask;
    }

    public Task RaiseMemberUpdatedAsync(string guildId, DiscordMemberSnapshot member)
        => MemberUpdated?.Invoke(guildId, member) ?? Task.CompletedTask;

    public Task RaiseMemberBannedAsync(string guildId, string userId)
        => MemberBanned?.Invoke(guildId, userId) ?? Task.CompletedTask;

    public Task RaiseMemberUnbannedAsync(string guildId, string userId)
        => MemberUnbanned?.Invoke(guildId, userId) ?? Task.CompletedTask;

    public Task RaiseVoiceChangedAsync(string guildId, string userId, string? from, string? to)
        => VoiceChanged?.Invoke(guildId, userId, from, to) ?? Task.CompletedTask;

    public Task RaiseAuditLogChangedAsync(string guildId)
        => AuditLogChanged?.Invoke(guildId) ?? Task.CompletedTask;

    public Task RaiseDisconnectedAsync(DiscordDisconnect disconnect)
    {
        State = DiscordGatewayState.Disconnected;
        return Disconnected?.Invoke(disconnect) ?? Task.CompletedTask;
    }
}

/// <summary>Hands out the gateways a test prepared, in order, and remembers every one it made.</summary>
public sealed class FakeGatewayFactory : IDiscordGatewayFactory
{
    private readonly Queue<FakeGateway> _prepared = new();

    public List<FakeGateway> Created { get; } = [];

    public FakeGateway Next(Action<FakeGateway>? configure = null)
    {
        var gateway = new FakeGateway();
        configure?.Invoke(gateway);
        _prepared.Enqueue(gateway);
        return gateway;
    }

    public IDiscordGateway Create(DiscordGatewayOptions options)
    {
        var gateway = _prepared.Count > 0 ? _prepared.Dequeue() : new FakeGateway();
        gateway.Options = options;
        Created.Add(gateway);
        return gateway;
    }

}
