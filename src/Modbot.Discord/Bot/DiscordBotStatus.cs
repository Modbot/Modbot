using Modbot.Core.Discord;

namespace Modbot.Discord.Bot;

/// <summary>
/// The bot's own account of itself, kept in memory for the Health page.
/// </summary>
/// <remarks>
/// In-process only, like <c>SyncDiagnostics</c>: a restart resets it, and that is right -- the
/// question the page answers is "what is this process doing", not "what happened last week".
/// The last error is a sentence and never carries the token.
/// </remarks>
public sealed class DiscordBotStatus : IDiscordBotStatus
{
    private readonly Lock _gate = new();

    private DiscordBotState _state = DiscordBotState.NotConfigured;
    private DateTimeOffset? _connectedSince;
    private string? _lastError;
    private DateTimeOffset? _lastErrorAt;
    private int _commandsRegistered;
    private bool _logChannelConfigured;
    private DateTimeOffset? _lastPostedAt;
    private int _postedInThisProcess;

    public DiscordBotSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new DiscordBotSnapshot(
                _state,
                _connectedSince,
                _lastError,
                _lastErrorAt,
                _commandsRegistered,
                _logChannelConfigured,
                _lastPostedAt,
                _postedInThisProcess);
        }
    }

    public DiscordBotState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public void NotConfigured()
    {
        lock (_gate)
        {
            _state = DiscordBotState.NotConfigured;
            _connectedSince = null;
            _commandsRegistered = 0;
        }
    }

    public void Connecting()
    {
        lock (_gate)
        {
            _state = DiscordBotState.Connecting;
            _connectedSince = null;
            _commandsRegistered = 0;
        }
    }

    public void Connected(DateTimeOffset at, int commandsRegistered)
    {
        lock (_gate)
        {
            _state = DiscordBotState.Connected;
            _connectedSince ??= at;
            _commandsRegistered = commandsRegistered;
        }
    }

    public void Disconnected(string reason, DateTimeOffset at)
    {
        lock (_gate)
        {
            _state = DiscordBotState.Disconnected;
            _connectedSince = null;
            _lastError = reason;
            _lastErrorAt = at;
        }
    }

    public void Failed(string error, DateTimeOffset at)
    {
        lock (_gate)
        {
            _state = DiscordBotState.Failed;
            _connectedSince = null;
            _commandsRegistered = 0;
            _lastError = error;
            _lastErrorAt = at;
        }
    }

    /// <summary>Something went wrong that did not end the session -- a post refused, commands not registered.</summary>
    public void Problem(string error, DateTimeOffset at)
    {
        lock (_gate)
        {
            _lastError = error;
            _lastErrorAt = at;
        }
    }

    public void LogChannel(bool configured)
    {
        lock (_gate)
            _logChannelConfigured = configured;
    }

    public void Posted(int count, DateTimeOffset at)
    {
        lock (_gate)
        {
            _postedInThisProcess += count;
            _lastPostedAt = at;
        }
    }
}
