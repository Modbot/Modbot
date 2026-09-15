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

    public IDiscordGateway Create()
    {
        var gateway = _prepared.Count > 0 ? _prepared.Dequeue() : new FakeGateway();
        Created.Add(gateway);
        return gateway;
    }
}
