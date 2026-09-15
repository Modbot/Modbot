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
using Modbot.Discord.Members;
using Modbot.Discord.Messages;
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
/// <para>
/// <strong>Both privileged intents are asked for</strong> -- Server Members and Message Content --
/// because members are recorded and messages stored in full (M5 spec §5). If Discord refuses one
/// because it is off in the Developer Portal, the bot names it on the Health page and connects
/// again without it rather than stopping, so the moderation log and the commands do not go quiet
/// over a switch they do not need. It asks again when the settings change or Modbot restarts.
/// Turning the prompt for new joiners on or off still changes the fingerprint and reconnects.
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

    /// <summary>The fingerprint whose privileged intents Discord refused. Connected without them until settings change.</summary>
    private string? _intentsRefusedFor;

    private bool _membersRefused;

    private bool _contentRefused;

    /// <summary>What the current session asked Discord for.</summary>
    private DiscordGatewayOptions _sessionOptions = new();
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

    private readonly DiscordHistoryReader _history;

    /// <summary>The history being read for the current session, and the way to stop it.</summary>
    private CancellationTokenSource? _readingStop;

    private Task _reading = Task.CompletedTask;

    /// <summary>One read of the audit log at a time: a live entry and a catch-up must not both record it.</summary>
    private readonly SemaphoreSlim _auditLog = new(1, 1);

    /// <summary>
    /// Whether this session has read when the bot was last listening. Until it has, that moment is
    /// not moved on, or the gap to catch up would be lost before it was looked at.
    /// </summary>
    private volatile bool _gapRead;

    private DateTimeOffset _seenWrittenAt;

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
        _history = new DiscordHistoryReader(scopes, clock, _options, _delay, _log);
    }

    /// <summary>
    /// The current pass over the server's message history, or a finished task when none is
    /// running. Exposed so a test can wait for it.
    /// </summary>
    public Task Reading => _reading;

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

        var fingerprint = Fingerprint(config.Token, config.GuildId, config.MemberEvents);

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

            // Any change -- the switch turned off and on again included -- is worth asking again.
            _intentsRefusedFor = null;

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

        // Note that the bot is listening, once a minute, so the next catch-up knows where the gap began.
        if (_gateway is { State: DiscordGatewayState.Ready } && _gapRead && _guildId is { } listening
            && now - _seenWrittenAt >= TimeSpan.FromMinutes(1))
        {
            _seenWrittenAt = now;
            await RecordAsync("note that the bot is listening", (recorder, token) => recorder.SeenAsync(listening, token))
                .ConfigureAwait(false);
        }

        if (_gateway is not null || _stopped)
            return;

        if (_nextAttemptAt is { } at && now < at)
            return;

        var refused = _intentsRefusedFor == fingerprint;
        var options = new DiscordGatewayOptions(
            MemberEvents: !(refused && _membersRefused),
            MessageContent: !(refused && _contentRefused));

        await ConnectAsync(config.Token, config.GuildId, options, ct).ConfigureAwait(false);
    }

    private async Task ConnectAsync(string token, string guildId, DiscordGatewayOptions options, CancellationToken ct)
    {
        _status.Connecting();

        var gateway = _gateways.Create(options);
        gateway.MemberJoined += OnMemberJoinedAsync;
        gateway.Ready += OnReadyAsync;
        gateway.Resumed += OnResumedAsync;
        gateway.Disconnected += OnDisconnectedAsync;
        gateway.CommandReceived += OnCommandAsync;
        gateway.ChannelChanged += OnChannelChangedAsync;
        gateway.ChannelRemoved += OnChannelRemovedAsync;
        gateway.ServerChanged += OnServerChangedAsync;
        gateway.MessageReceived += OnMessageReceivedAsync;
        gateway.MessageEdited += OnMessageEditedAsync;
        gateway.MessagesDeleted += OnMessagesDeletedAsync;
        gateway.MemberJoined += RecordMemberJoinedAsync;
        gateway.MemberLeft += OnMemberLeftAsync;
        gateway.MemberUpdated += OnMemberUpdatedAsync;
        gateway.MemberBanned += OnMemberBannedAsync;
        gateway.MemberUnbanned += OnMemberUnbannedAsync;
        gateway.VoiceChanged += OnVoiceChangedAsync;
        gateway.AuditLogChanged += OnAuditLogChangedAsync;

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
        _sessionOptions = options;
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

        // Everything was asked for and Discord took it: whatever was refused before is on now.
        if (_sessionOptions is { MemberEvents: true, MessageContent: true })
            _status.IntentsAllowed();

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
        StartReading(signedIn: true);
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
        StartReading(signedIn: false);
    }

    /// <summary>
    /// Starts reading the server's message history in the background, stopping any pass already
    /// running first: a new session means the channel list and the gap to catch up have changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In the background because a read-back can take hours, and the Ready handler must return.
    /// It runs one page at a time and stops with the session.
    /// </para>
    /// <para>
    /// Everything since the bot last listened is caught up first: the member list, the audit log and
    /// who is in voice, after a fresh sign-in and after a resume alike. The member list comes over
    /// the gateway and the audit log is a request or two, so both are cheap. Each channel's messages
    /// are caught up forward from the newest stored only after a fresh sign-in -- a restart, or a
    /// session that could not be resumed: after a resume Discord replays the messages the session
    /// missed, and reading every channel on each resume would be hundreds of requests.
    /// </para>
    /// </remarks>
    private void StartReading(bool signedIn)
    {
        var gateway = _gateway;
        var guildId = _guildId;
        if (gateway is null || guildId is null)
            return;

        StopReading();

        var stop = new CancellationTokenSource();
        _readingStop = stop;

        var previous = _reading;
        _reading = Task.Run(async () =>
        {
            // The pass it replaced finishes its page first; two at once would read the same pages.
            await previous.ConfigureAwait(false);

            try
            {
                await CatchUpAsync(gateway, guildId, messages: signedIn, stop.Token).ConfigureAwait(false);

                await _history.ReadBackAsync(gateway, guildId, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                _log.Warning(e, "Reading the Discord server's message history failed; it carries on at the next sign-in");
                _status.Problem($"Could not read the server's message history: {e.Message}", _clock.UtcNow);
            }
        });
    }

    /// <summary>Everything the bot missed while it was away, in the order that keeps names right.</summary>
    private async Task CatchUpAsync(IDiscordGateway gateway, string guildId, bool messages, CancellationToken ct)
    {
        DateTimeOffset? seenThrough = null;
        await RecordAsync("read when the bot last listened", async (recorder, token) =>
            seenThrough = await recorder.SeenThroughAsync(guildId, token).ConfigureAwait(false)).ConfigureAwait(false);
        _gapRead = true;

        // Members first, so the audit log's facts can name the people in them.
        if (await gateway.ReadMembersAsync(guildId, ct).ConfigureAwait(false) is { } members)
        {
            await RecordAsync("compare the member list", (recorder, token) =>
                recorder.MembersListedAsync(guildId, members, seenThrough, token)).ConfigureAwait(false);
        }

        await ReadAuditLogAsync(gateway, guildId, ct).ConfigureAwait(false);

        var voice = gateway.ReadVoice(guildId);
        await RecordAsync("compare who is in voice", (recorder, token) =>
            recorder.VoiceListedAsync(guildId, voice, seenThrough, token)).ConfigureAwait(false);

        if (messages)
            await _history.CatchUpAsync(gateway, guildId, ct).ConfigureAwait(false);
    }

    /// <summary>Reads the audit log from where the last read stopped and records what is new.</summary>
    private async Task ReadAuditLogAsync(IDiscordGateway gateway, string guildId, CancellationToken ct)
    {
        await _auditLog.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            using var scope = _scopes.CreateScope();
            var recorder = scope.ServiceProvider.GetRequiredService<DiscordEventRecorder>();

            var after = await recorder.AuditLogReadThroughAsync(guildId, ct).ConfigureAwait(false);
            var page = await gateway.ReadAuditLogAsync(guildId, after, ct).ConfigureAwait(false);

            if (page.Error is not null && !page.NoAccess)
                _log.Warning("Could not read the Discord audit log: {Reason}", page.Error);

            if (page.Entries.Count > 0 || page.NewestId is not null)
                await recorder.AuditLogAsync(guildId, page, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warning(e, "Could not record the Discord audit log");
            _status.Problem($"Could not record the Discord audit log: {e.Message}", _clock.UtcNow);
        }
        finally
        {
            _auditLog.Release();
        }
    }

    private Task RecordMemberJoinedAsync(DiscordMemberJoin join)
        => IsOurServer(join.GuildId)
            ? RecordAsync("record a member joining", (recorder, ct) => recorder.MemberJoinedAsync(
                join.GuildId,
                join.Member ?? new DiscordMemberSnapshot(join.UserId, join.Username, join.Username, null, join.IsBot, null, [], null),
                _gateway?.ReadMemberCount(join.GuildId),
                ct))
            : Task.CompletedTask;

    private Task OnMemberLeftAsync(string guildId, string userId)
        => IsOurServer(guildId)
            ? RecordAsync("record a member leaving", (recorder, ct) =>
                recorder.MemberLeftAsync(guildId, userId, _gateway?.ReadMemberCount(guildId), ct))
            : Task.CompletedTask;

    private Task OnMemberUpdatedAsync(string guildId, DiscordMemberSnapshot member)
        => IsOurServer(guildId)
            ? RecordAsync("record a member change", (recorder, ct) => recorder.MemberUpdatedAsync(guildId, member, ct))
            : Task.CompletedTask;

    private Task OnMemberBannedAsync(string guildId, string userId)
        => IsOurServer(guildId)
            ? RecordAsync("record a ban", (recorder, ct) => recorder.BannedAsync(guildId, userId, ct))
            : Task.CompletedTask;

    private Task OnMemberUnbannedAsync(string guildId, string userId)
        => IsOurServer(guildId)
            ? RecordAsync("record an unban", (recorder, ct) => recorder.UnbannedAsync(guildId, userId, ct))
            : Task.CompletedTask;

    private Task OnVoiceChangedAsync(string guildId, string userId, string? from, string? to)
        => IsOurServer(guildId)
            ? RecordAsync("record a voice change", (recorder, ct) => recorder.VoiceChangedAsync(guildId, userId, from, to, ct))
            : Task.CompletedTask;

    private Task OnAuditLogChangedAsync(string guildId)
        => IsOurServer(guildId) && _gateway is { } gateway
            ? ReadAuditLogAsync(gateway, guildId, CancellationToken.None)
            : Task.CompletedTask;

    private async Task RecordAsync(string what, Func<DiscordEventRecorder, CancellationToken, Task> work)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var recorder = scope.ServiceProvider.GetRequiredService<DiscordEventRecorder>();
            await work(recorder, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warning(e, "Could not {What}", what);
            _status.Problem($"Could not {what}: {e.Message}", _clock.UtcNow);
        }
    }

    private void StopReading()
    {
        var stop = _readingStop;
        _readingStop = null;

        if (stop is not null)
        {
            stop.Cancel();
            stop.Dispose();
        }
    }

    private Task OnMessageReceivedAsync(DiscordMessageSnapshot message)
        => IsOurServer(message.GuildId)
            ? MessagesAsync("store a Discord message", (handler, ct) => handler.ReceivedAsync([message], ct))
            : Task.CompletedTask;

    private Task OnMessageEditedAsync(DiscordMessageSnapshot message)
        => IsOurServer(message.GuildId)
            ? MessagesAsync("store an edited Discord message", (handler, ct) => handler.EditedAsync(message, ct))
            : Task.CompletedTask;

    private Task OnMessagesDeletedAsync(string guildId, string channelId, IReadOnlyList<string> messageIds)
        => IsOurServer(guildId)
            ? MessagesAsync("mark Discord messages deleted", (handler, ct) => handler.DeletedAsync(messageIds, ct))
            : Task.CompletedTask;

    private async Task MessagesAsync(string what, Func<DiscordMessageHandler, CancellationToken, Task> work)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<DiscordMessageHandler>();
            await work(handler, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // One message lost; catching up after the next reconnect finds a new one again.
            _log.Warning(e, "Could not {What}", what);
            _status.Problem($"Could not {what}: {e.Message}", _clock.UtcNow);
        }
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

    private async Task OnMemberJoinedAsync(DiscordMemberJoin member)
    {
        var gateway = _gateway;
        if (gateway is null || !IsOurServer(member.GuildId))
            return;

        try
        {
            using var scope = _scopes.CreateScope();
            var prompt = scope.ServiceProvider.GetRequiredService<Linking.LinkPrompt>();
            await prompt.HandleAsync(gateway, member, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warning(e, "Could not send the link prompt to a new Discord member");
            _status.Problem($"Could not send the link prompt to a new member: {e.Message}", _clock.UtcNow);
        }
    }

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

        if (disconnect.IntentsRefused && (_sessionOptions.MemberEvents || _sessionOptions.MessageContent))
        {
            // Connect again without the refused intents, straight away: the moderation log and
            // the commands need neither. When the flags could not be read to say which, both go.
            var missing = disconnect.MissingIntents is { Count: > 0 } named
                ? named
                : ["Server Members Intent", "Message Content Intent"];

            _intentsRefusedFor = _fingerprint;
            _membersRefused = missing.Contains("Server Members Intent");
            _contentRefused = missing.Contains("Message Content Intent");

            _status.IntentsRefused(disconnect.Reason, now, missing);
            _log.Warning("{Reason} Connecting without them", disconnect.Reason);
            _nextAttemptAt = null;
            await TearDownAsync().ConfigureAwait(false);
            return;
        }

        if (disconnect.Fatal)
        {
            _status.Failed(disconnect.Reason, now);
            _log.Error("Discord bot stopped: {Reason}. Fix the settings; the bot will try again when they change", disconnect.Reason);
            _stopped = true;
            await TearDownAsync().ConfigureAwait(false);
            return;
        }

        _gapRead = false;
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

        StopReading();
        _gapRead = false;

        if (gateway is null)
            return;

        gateway.MemberJoined -= OnMemberJoinedAsync;
        gateway.Ready -= OnReadyAsync;
        gateway.Resumed -= OnResumedAsync;
        gateway.Disconnected -= OnDisconnectedAsync;
        gateway.CommandReceived -= OnCommandAsync;
        gateway.ChannelChanged -= OnChannelChangedAsync;
        gateway.ChannelRemoved -= OnChannelRemovedAsync;
        gateway.ServerChanged -= OnServerChangedAsync;
        gateway.MessageReceived -= OnMessageReceivedAsync;
        gateway.MessageEdited -= OnMessageEditedAsync;
        gateway.MessagesDeleted -= OnMessagesDeletedAsync;
        gateway.MemberJoined -= RecordMemberJoinedAsync;
        gateway.MemberLeft -= OnMemberLeftAsync;
        gateway.MemberUpdated -= OnMemberUpdatedAsync;
        gateway.MemberBanned -= OnMemberBannedAsync;
        gateway.MemberUnbanned -= OnMemberUnbannedAsync;
        gateway.VoiceChanged -= OnVoiceChangedAsync;
        gateway.AuditLogChanged -= OnAuditLogChangedAsync;

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
            .Select(s => new { s.DiscordBotTokenEncrypted, s.DiscordGuildId, s.DiscordLinkPromptNewMembers })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (settings is null)
            return new BotConfig(null, null, false, false);

        var token = protector.Unprotect(settings.DiscordBotTokenEncrypted);

        var sendsEvents = await db.DiscordEventRoutes.AsNoTracking()
            .AnyAsync(r => r.Enabled && r.ChannelId != "", ct)
            .ConfigureAwait(false);

        return new BotConfig(
            string.IsNullOrWhiteSpace(token) ? null : token,
            string.IsNullOrWhiteSpace(settings.DiscordGuildId) ? null : settings.DiscordGuildId.Trim(),
            sendsEvents,
            settings.DiscordLinkPromptNewMembers);

    }

    private static string Fingerprint(string token, string guildId, bool memberEvents)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            token + '\n' + guildId + '\n' + (memberEvents ? "members" : string.Empty))));

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private sealed record BotConfig(string? Token, string? GuildId, bool LogChannelConfigured, bool MemberEvents);

}
