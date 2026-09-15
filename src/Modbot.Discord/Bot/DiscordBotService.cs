using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Data;
using Modbot.Core.Discord;
using Modbot.Core.Logging;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.ServerIndex;
using Serilog;

namespace Modbot.Discord.Bot;

/// <summary>
/// Keeps one gateway session alive for as long as a token and a guild id are stored, and none
/// when they are not (foundation §9).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Settings are polled, not pushed.</strong> The token and guild live in the database and
/// are changed from the settings page. Rather than thread a "settings changed" signal through the
/// API into this service, the loop re-reads the single settings row every few seconds -- the same
/// thing the sync producers do for their pacing -- and compares a fingerprint. A changed token
/// tears the session down and signs in again; a cleared one tears it down and stops. Nothing
/// needs a restart, which is the point the evidence store's reloadable holder makes at length.
/// </para>
/// <para>
/// <strong>Reconnecting is layered.</strong> Discord.Net reconnects a dropped socket by itself.
/// This service only steps in when that has not worked for a few minutes, or when the library
/// reports a close that can never succeed with the same settings (bad token, refused intents) --
/// in which case it stops and waits for the operator rather than knocking on a locked door every
/// thirty seconds. Sign-in failures back off from thirty seconds to ten minutes.
/// </para>
/// <para>
/// <strong>The token is never kept.</strong> It is decrypted for one sign-in and handed to the
/// gateway; what this service remembers is a hash, enough to notice a change.
/// </para>
/// </remarks>
public sealed class DiscordBotService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IDiscordGatewayFactory _gateways;
    private readonly IModbotClock _clock;
    private readonly DiscordBotStatus _status;
    private readonly DiscordBotOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    private IDiscordGateway? _gateway;
    private string? _fingerprint;
    private string? _guildId;
    private DateTimeOffset? _disconnectedAt;
    private DateTimeOffset? _nextAttemptAt;
    private TimeSpan _retry;
    private bool _stopped;
    private int _commandsRegistered;

    /// <summary>
    /// One write to the channel and role lists at a time. The gateway raises its events on the
    /// thread pool, and a full refresh racing a channel update would insert the same row twice.
    /// </summary>
    private readonly SemaphoreSlim _indexing = new(1, 1);

    public DiscordBotService(
        IServiceScopeFactory scopes,
        IDiscordGatewayFactory gateways,
        IModbotClock clock,
        DiscordBotStatus status,
        DiscordBotOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(gateways);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(status);

        _scopes = scopes;
        _gateways = gateways;
        _clock = clock;
        _status = status;
        _options = options ?? new DiscordBotOptions();
        _delay = delay ?? Task.Delay;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
        _retry = _options.FirstRetry;
    }

    /// <summary>The session, when it can answer and post. Null otherwise.</summary>
    public IDiscordGateway? ReadyGateway
        => _gateway is { State: DiscordGatewayState.Ready } gateway ? gateway : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                _log.Error(e, "The Discord bot's connection loop failed; trying again shortly");
                _status.Problem($"The bot's connection loop failed: {e.Message}", _clock.UtcNow);
            }

            try
            {
                await _delay(_options.SettingsPollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await TearDownAsync().ConfigureAwait(false);
    }

    /// <summary>One pass of the loop. Public so a test can drive it without the timer.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var config = await ReadConfigAsync(ct).ConfigureAwait(false);
        var now = _clock.UtcNow;

        _status.LogChannel(config.LogChannelConfigured);

        if (config.Token is null || config.GuildId is null)
        {
            if (_gateway is not null)
            {
                _log.Information("Discord bot settings were cleared; stopping the bot");
                await TearDownAsync().ConfigureAwait(false);
            }

            _fingerprint = null;
            _stopped = false;
            _nextAttemptAt = null;
            _status.NotConfigured();
            return;
        }

        var fingerprint = Fingerprint(config.Token, config.GuildId);

        if (fingerprint != _fingerprint)
        {
            if (_gateway is not null)
            {
                _log.Information("Discord bot settings changed; reconnecting");
                await TearDownAsync().ConfigureAwait(false);
            }

            // A new token or guild is a fresh start, whatever the last one did. Remembered
            // before the attempt, so a sign-in that fails is not mistaken for new settings on
            // the next tick and retried without its backoff.
            _fingerprint = fingerprint;
            _stopped = false;
            _nextAttemptAt = null;
            _retry = _options.FirstRetry;
        }

        // An event can be missed. If the gateway can post but the status says otherwise, the
        // status is what is wrong.
        if (_gateway is { State: DiscordGatewayState.Ready } && _status.State != DiscordBotState.Connected)
        {
            _disconnectedAt = null;
            _status.Connected(now, _commandsRegistered);
        }

        // Not ready for too long after a drop, whatever state it is stuck in. A socket that came
        // back without its session returning sits in Connecting, not Disconnected.
        if (_gateway is { State: not DiscordGatewayState.Ready }
            && _disconnectedAt is { } since
            && now - since >= _options.RebuildAfterDisconnected)
        {
            _log.Warning(
                "The Discord session has been down for {Duration}; signing in afresh",
                now - since);
            await TearDownAsync().ConfigureAwait(false);
        }

        if (_gateway is not null || _stopped)
            return;

        if (_nextAttemptAt is { } at && now < at)
            return;

        await ConnectAsync(config.Token, config.GuildId, ct).ConfigureAwait(false);
    }

    private async Task ConnectAsync(string token, string guildId, CancellationToken ct)
    {
        _status.Connecting();

        var gateway = _gateways.Create();
        gateway.Ready += OnReadyAsync;
        gateway.Resumed += OnResumedAsync;
        gateway.Disconnected += OnDisconnectedAsync;
        gateway.CommandReceived += OnCommandAsync;
        gateway.ChannelChanged += OnChannelChangedAsync;
        gateway.ChannelRemoved += OnChannelRemovedAsync;
        gateway.ServerChanged += OnServerChangedAsync;

        try
        {
            await gateway.ConnectAsync(token, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var now = _clock.UtcNow;
            _status.Failed(e.Message, now);
            _nextAttemptAt = now + _retry;
            _log.Warning("Could not sign the Discord bot in: {Reason}. Trying again in {Retry}", e.Message, _retry);
            _retry = Min(_retry + _retry, _options.MaxRetry);

            await gateway.DisposeAsync().ConfigureAwait(false);
            return;
        }

        _gateway = gateway;
        _guildId = guildId;
        _disconnectedAt = null;
        _log.Information("Discord bot signing in");
    }

    private async Task OnReadyAsync()
    {
        var gateway = _gateway;
        var guildId = _guildId;
        if (gateway is null || guildId is null)
            return;

        _disconnectedAt = null;
        _retry = _options.FirstRetry;

        try
        {
            var count = await gateway
                .RegisterGuildCommandsAsync(guildId, DiscordCommands.All, CancellationToken.None)
                .ConfigureAwait(false);

            _commandsRegistered = count;
            _status.Connected(_clock.UtcNow, count);
            _log.Information("Discord bot connected; {Count} slash commands registered on the guild", count);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Connected but useless. Say so and stay signed in: the channel poster still works,
            // and a fixed guild id is picked up by the settings poll without a restart.
            _commandsRegistered = 0;
            _status.Connected(_clock.UtcNow, 0);
            _status.Problem($"Could not register the slash commands: {e.Message}", _clock.UtcNow);
            _log.Warning("Discord bot connected but could not register its commands: {Reason}", e.Message);
        }

        await RefreshServerIndexAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The session came back without signing in again. The slash commands registered at sign-in
    /// still stand, so they are not registered again: Discord limits how often a guild's commands
    /// may be replaced.
    /// </summary>
    /// <remarks>
    /// The channel and role lists are read again in full, though: whatever changed while the
    /// session was away is only certain to be caught by looking.
    /// </remarks>
    private async Task OnResumedAsync()
    {
        if (_gateway is null)
            return;

        _disconnectedAt = null;
        _retry = _options.FirstRetry;
        _status.Connected(_clock.UtcNow, _commandsRegistered);
        _log.Information("Discord bot resumed its session");

        await RefreshServerIndexAsync().ConfigureAwait(false);
    }

    /// <summary>Reads every channel and role from the session and stores them.</summary>
    private Task RefreshServerIndexAsync()
    {
        var gateway = _gateway;
        var guildId = _guildId;
        if (gateway is null || guildId is null)
            return Task.CompletedTask;

        return IndexAsync(async (index, ct) =>
        {
            // Read inside the lock, so the stored picture is never older than one already saved.
            if (gateway.ReadServer(guildId) is not { } server)
            {
                _log.Debug("The Discord session does not know server {GuildId}; not reading its channels", guildId);
                return;
            }

            await index.RefreshAsync(server, ct).ConfigureAwait(false);
            _log.Information(
                "Read {Channels} Discord channels and {Roles} roles",
                server.Channels.Count,
                server.Roles.Count);
        });
    }

    private Task OnChannelChangedAsync(string guildId, DiscordChannelSnapshot channel)
    {
        if (!IsOurServer(guildId))
            return Task.CompletedTask;

        // A category's overwrites flow down to the channels synced with it, and Discord does not
        // promise an update for each of those, so a changed category means reading everything.
        if (channel.Type == Core.Data.Entities.DiscordChannelTypes.Category)
            return RefreshServerIndexAsync();

        return IndexAsync((index, ct) => index.SaveChannelAsync(guildId, channel, ct));
    }

    private Task OnChannelRemovedAsync(string guildId, string channelId)
        => IsOurServer(guildId)
            ? IndexAsync((index, ct) => index.RemoveChannelAsync(guildId, channelId, ct))
            : Task.CompletedTask;

    private Task OnServerChangedAsync(string guildId)
        => IsOurServer(guildId) ? RefreshServerIndexAsync() : Task.CompletedTask;

    /// <summary>The bot may sit in more than one server; only the one in settings is kept.</summary>
    private bool IsOurServer(string guildId)
        => _guildId is { } ours && string.Equals(ours, guildId, StringComparison.Ordinal);

    private async Task IndexAsync(Func<DiscordServerIndex, CancellationToken, Task> work)
    {
        await _indexing.WaitAsync().ConfigureAwait(false);

        try
        {
            using var scope = _scopes.CreateScope();
            var index = scope.ServiceProvider.GetRequiredService<DiscordServerIndex>();
            await work(index, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The lists go stale until the next change or sign-in; nothing else stops.
            _log.Warning(e, "Could not save the Discord server's channels and roles");
            _status.Problem($"Could not save the Discord server's channels and roles: {e.Message}", _clock.UtcNow);
        }
        finally
        {
            _indexing.Release();
        }
    }

    private async Task OnDisconnectedAsync(DiscordDisconnect disconnect)
    {
        var now = _clock.UtcNow;
        _disconnectedAt ??= now;

        if (disconnect.Fatal)
        {
            _status.Failed(disconnect.Reason, now);
            _log.Error("Discord bot stopped: {Reason}. Fix the settings; the bot will try again when they change", disconnect.Reason);
            _stopped = true;
            await TearDownAsync().ConfigureAwait(false);
            return;
        }

        _status.Disconnected(disconnect.Reason, now);
        _log.Warning("Discord bot lost its connection: {Reason}. Reconnecting", disconnect.Reason);
    }

    private async Task OnCommandAsync(DiscordCommandCall call)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>();
            var reply = await handler.HandleAsync(call, CancellationToken.None).ConfigureAwait(false);
            await call.ReplyAsync(reply, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.Error(e, "The /{Command} command failed", call.CommandName);
            _status.Problem($"/{call.CommandName} failed: {e.Message}", _clock.UtcNow);

            try
            {
                await call.ReplyAsync(
                        DiscordReply.Say("Something went wrong on Modbot's side. The operator can find the details in the log."),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception replyError)
            {
                _log.Debug(replyError, "Could not tell Discord that the command failed");
            }
        }
    }

    private async Task TearDownAsync()
    {
        var gateway = _gateway;
        _gateway = null;
        _guildId = null;
        _disconnectedAt = null;

        if (gateway is null)
            return;

        gateway.Ready -= OnReadyAsync;
        gateway.Resumed -= OnResumedAsync;
        gateway.Disconnected -= OnDisconnectedAsync;
        gateway.CommandReceived -= OnCommandAsync;
        gateway.ChannelChanged -= OnChannelChangedAsync;
        gateway.ChannelRemoved -= OnChannelRemovedAsync;
        gateway.ServerChanged -= OnServerChangedAsync;

        try
        {
            await gateway.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.Debug(e, "Ignoring an error while closing the Discord session");
        }
    }

    private async Task<BotConfig> ReadConfigAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();

        var settings = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.DiscordBotTokenEncrypted, s.DiscordGuildId, s.DiscordLogChannelId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (settings is null)
            return new BotConfig(null, null, false);

        var token = protector.Unprotect(settings.DiscordBotTokenEncrypted);

        return new BotConfig(
            string.IsNullOrWhiteSpace(token) ? null : token,
            string.IsNullOrWhiteSpace(settings.DiscordGuildId) ? null : settings.DiscordGuildId.Trim(),
            !string.IsNullOrWhiteSpace(settings.DiscordLogChannelId));
    }

    private static string Fingerprint(string token, string guildId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token + '\n' + guildId)));

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private sealed record BotConfig(string? Token, string? GuildId, bool LogChannelConfigured);
}
