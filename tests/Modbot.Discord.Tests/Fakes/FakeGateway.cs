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

    public Task<DiscordRoleOutcome> AddRoleAsync(string guildId, string userId, string roleId, CancellationToken ct)
        => RoleAsync(true, guildId, userId, roleId);

    public Task<DiscordRoleOutcome> RemoveRoleAsync(string guildId, string userId, string roleId, CancellationToken ct)
        => RoleAsync(false, guildId, userId, roleId);

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
    public void FailNextEdit(string error, bool permanent = false)
        => _editOutcomes.Enqueue(DiscordPostOutcome.Failed(error, permanent));

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
        CancellationToken ct)
    {
        var outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : null;

        if (outcome is { Sent: false })
            return Task.FromResult(outcome);

        var messageId = (_nextMessageId++).ToString(System.Globalization.CultureInfo.InvariantCulture);

        Posts.Add((channelId, embeds));
        Messages.Add((channelId, messageId, text, embeds, links ?? []));

        return Task.FromResult(DiscordPostOutcome.Posted(messageId));
    }

    public Task<DiscordPostOutcome> EditAsync(
        string channelId,
        string messageId,
        string? text,
        IReadOnlyList<DiscordEmbedContent> embeds,
        IReadOnlyList<DiscordLinkButton>? links,
        CancellationToken ct)
    {
        var outcome = _editOutcomes.Count > 0 ? _editOutcomes.Dequeue() : null;

        if (outcome is { Sent: false })
            return Task.FromResult(outcome);

        Edits.Add((channelId, messageId, text, embeds, links ?? []));
        return Task.FromResult(DiscordPostOutcome.Posted(messageId));
    }

    /// <summary>Every message deleted, in order.</summary>
    public List<(string ChannelId, string MessageId, string Reason)> Deleted { get; } = [];

    /// <summary>Every timeout, in order.</summary>
    public List<(string GuildId, string UserId, TimeSpan Duration, string Reason)> TimedOut { get; } = [];

    public Task<DiscordPostOutcome> DeleteMessageAsync(string channelId, string messageId, string reason, CancellationToken ct)
    {
        Deleted.Add((channelId, messageId, reason));
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
