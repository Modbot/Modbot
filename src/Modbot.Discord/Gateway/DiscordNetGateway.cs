using System.Globalization;
using System.Net;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Modbot.Core.Logging;
using DiscordChannelTypes = Modbot.Core.Data.Entities.DiscordChannelTypes;
using Serilog;
using Serilog.Events;

namespace Modbot.Discord.Gateway;

/// <summary>
/// <see cref="IDiscordGateway"/> over Discord.Net's socket client.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why Discord.Net.</strong> It is the longest-lived and most widely used .NET library for
/// Discord, which matters for a project meant to be picked up by other people: a future
/// contributor is likelier to have seen it. Only the WebSocket package is referenced -- slash
/// commands, embeds and one channel to post to need the socket client and the REST client it
/// carries, and none of the text-command or interaction frameworks.
/// </para>
/// <para>
/// <strong>Intents.</strong> <c>Guilds</c>, which is what slash commands and channel lookups need
/// and is not privileged. <c>GuildMembers</c> is added only for a session made with
/// <see cref="DiscordGatewayOptions.MemberEvents"/> -- the prompt for new joiners -- because it is
/// privileged: it has to be switched on in the Developer Portal, and Discord closes a session that
/// asks for it without that (close code 4014).
/// </para>
/// <para>
/// <strong>Threading.</strong> The library raises events on its gateway task and complains when a
/// handler holds it for more than a few seconds. Every handler here hands the work to the thread
/// pool at once, so a slow database query answers late rather than stalling the heartbeat.
/// </para>
/// </remarks>
public sealed class DiscordNetGateway : IDiscordGateway
{
    private readonly DiscordSocketClient _client;
    private readonly ILogger _log;

    private volatile DiscordGatewayState _state = DiscordGatewayState.Disconnected;

    /// <summary>Whether this session has been ready at least once, so a later connect is a resume.</summary>
    private volatile bool _sessionReady;

    public DiscordNetGateway(DiscordGatewayOptions? options = null, ILogger? log = null)
    {
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);

        var intents = GatewayIntents.Guilds;
        if (options?.MemberEvents == true)
            intents |= GatewayIntents.GuildMembers;

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = intents,
            AlwaysDownloadUsers = false,
            MessageCacheSize = 0,
            LogGatewayIntentWarnings = false,
            LogLevel = LogSeverity.Info,
        });

        _client.Log += OnLog;
        _client.Connected += OnConnected;
        _client.Ready += OnReady;
        _client.Disconnected += OnDisconnected;
        _client.SlashCommandExecuted += OnSlashCommand;

        _client.ChannelCreated += OnChannelCreated;
        _client.ChannelUpdated += OnChannelUpdated;
        _client.ChannelDestroyed += OnChannelDestroyed;
        _client.RoleCreated += OnRoleCreated;
        _client.RoleUpdated += OnRoleUpdated;
        _client.RoleDeleted += OnRoleDeleted;
        _client.GuildUpdated += OnGuildUpdated;
        _client.GuildMemberUpdated += OnGuildMemberUpdated;
        _client.UserJoined += OnUserJoined;
    }

    public DiscordGatewayState State => _state;

    public event Func<DiscordMemberJoin, Task>? MemberJoined;

    public event Func<string, DiscordChannelSnapshot, Task>? ChannelChanged;

    public event Func<string, string, Task>? ChannelRemoved;

    public event Func<string, Task>? ServerChanged;

    public event Func<Task>? Ready;

    public event Func<Task>? Resumed;

    public event Func<DiscordDisconnect, Task>? Disconnected;

    public event Func<DiscordCommandCall, Task>? CommandReceived;

    public async Task ConnectAsync(string token, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        _state = DiscordGatewayState.Connecting;

        try
        {
            await _client.LoginAsync(TokenType.Bot, token).ConfigureAwait(false);
            await _client.StartAsync().ConfigureAwait(false);
        }
        catch (HttpException e) when (e.HttpCode == HttpStatusCode.Unauthorized)
        {
            _state = DiscordGatewayState.Disconnected;
            throw new InvalidOperationException("Discord rejected the bot token.", e);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _state = DiscordGatewayState.Disconnected;
            throw new InvalidOperationException($"Could not sign in to Discord: {e.Message}", e);
        }
    }

    public async Task<int> RegisterGuildCommandsAsync(
        string guildId, IReadOnlyList<DiscordCommandDefinition> commands, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(commands);

        if (!ulong.TryParse(guildId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            throw new InvalidOperationException("The guild id is not a Discord server id. It should be a long number.");

        var guild = _client.GetGuild(id)
            ?? throw new InvalidOperationException(
                "The bot is not in that server. Invite it with the link from the Developer Portal, "
                + "then check the guild id.");

        var properties = commands.Select(ToProperties).ToArray();

        var registered = await guild.BulkOverwriteApplicationCommandAsync(properties).ConfigureAwait(false);
        return registered.Count;
    }

    public Task<DiscordPostOutcome> PostAsync(
        string channelId, IReadOnlyList<DiscordEmbedContent> embeds, CancellationToken ct) =>
        PostAsync(channelId, text: null, embeds, links: null, ct);

    public Task<DiscordPostOutcome> PostAsync(
        string channelId,
        string? text,
        IReadOnlyList<DiscordEmbedContent> embeds,
        IReadOnlyList<DiscordLinkButton>? links,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(embeds);

        return InChannelAsync(channelId, async channel =>
        {
            // Names inside an embed never ping anybody, even one that happens to read like @here,
            // and neither does the operator's own line above it.
            var sent = await channel.SendMessageAsync(
                    text: text,
                    embeds: embeds.Select(ToEmbed).ToArray(),
                    allowedMentions: AllowedMentions.None,
                    components: Buttons(links) is { Components.Count: > 0 } buttons ? buttons : null)
                .ConfigureAwait(false);

            return DiscordPostOutcome.Posted(
                sent.Id.ToString(CultureInfo.InvariantCulture));
        });
    }

    public Task<DiscordPostOutcome> EditAsync(
        string channelId,
        string messageId,
        string? text,
        IReadOnlyList<DiscordEmbedContent> embeds,
        IReadOnlyList<DiscordLinkButton>? links,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(embeds);

        if (!ulong.TryParse(messageId, NumberStyles.None, CultureInfo.InvariantCulture, out var message))
        {
            return Task.FromResult(DiscordPostOutcome.Failed(
                "That is not a Discord message id.", permanent: true));
        }

        return InChannelAsync(channelId, async channel =>
        {
            // Only messages the bot itself sent can be edited, which is what this is for; anything
            // else comes back as a permanent failure and the caller forgets the id.
            if (await channel.GetMessageAsync(message).ConfigureAwait(false) is not IUserMessage mine)
            {
                return DiscordPostOutcome.Failed(
                    "That message is gone, or was not posted by the bot.", permanent: true);
            }

            await mine.ModifyAsync(m =>
            {
                m.Content = text;
                m.Embeds = embeds.Select(ToEmbed).ToArray();
                m.AllowedMentions = AllowedMentions.None;

                // Always set, so an edit with no buttons takes away the ones the message had.
                m.Components = Buttons(links);
            }).ConfigureAwait(false);

            return DiscordPostOutcome.Posted(messageId);
        });
    }

    /// <summary>
    /// Finds the channel and runs one piece of work against it, turning every way Discord can say
    /// no into a <see cref="DiscordPostOutcome"/> rather than an exception.
    /// </summary>
    /// <remarks>
    /// Shared by posting and editing because the failures are identical and the difference between
    /// "try again in a minute" and "stop and tell the operator" is the only thing either caller
    /// acts on. A 403 or a 404 is permanent: the bot has been removed from the channel, or the
    /// channel is gone, and retrying is just noise in the log until somebody changes a setting.
    /// </remarks>
    public Task<DiscordPostOutcome> DeleteMessageAsync(string channelId, string messageId, string reason, CancellationToken ct)
    {
        if (!ulong.TryParse(messageId, NumberStyles.None, CultureInfo.InvariantCulture, out var message))
            return Task.FromResult(DiscordPostOutcome.Failed("That is not a Discord message id.", permanent: true));

        return InChannelAsync(channelId, async channel =>
        {
            await channel.DeleteMessageAsync(message, new RequestOptions { AuditLogReason = reason, CancelToken = ct })
                .ConfigureAwait(false);
            return DiscordPostOutcome.Ok;
        });
    }

    public async Task<DiscordPostOutcome> TimeOutAsync(
        string guildId, string userId, TimeSpan duration, string reason, CancellationToken ct)
    {
        if (!ulong.TryParse(guildId, NumberStyles.None, CultureInfo.InvariantCulture, out var guild)
            || !ulong.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out var user))
        {
            return DiscordPostOutcome.Failed("That is not a Discord server or user id.", permanent: true);
        }

        try
        {
            // REST rather than the member cache: the cache needs the Server Members intent, and one
            // lookup per timeout does not.
            var member = await _client.Rest.GetGuildUserAsync(guild, user, new RequestOptions { CancelToken = ct })
                .ConfigureAwait(false);

            if (member is null)
                return DiscordPostOutcome.Failed("That person is not in the server.", permanent: true);

            await member.SetTimeOutAsync(duration, new RequestOptions { AuditLogReason = reason, CancelToken = ct })
                .ConfigureAwait(false);
            return DiscordPostOutcome.Ok;
        }
        catch (HttpException e)
        {
            var permanent = e.HttpCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound;
            return DiscordPostOutcome.Failed(
                e.HttpCode == HttpStatusCode.Forbidden
                    ? "The bot may not time that person out; it needs Moderate Members and a role above theirs."
                    : $"Discord answered {(int)e.HttpCode}.",
                permanent);
        }
        catch (RateLimitedException)
        {
            return DiscordPostOutcome.Failed("Discord is rate limiting the bot.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return DiscordPostOutcome.Failed($"Could not time out on Discord: {e.Message}");
        }
    }

    private async Task<DiscordPostOutcome> InChannelAsync(
        string channelId, Func<IMessageChannel, Task<DiscordPostOutcome>> work)
    {
        if (!ulong.TryParse(channelId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return DiscordPostOutcome.Failed(
                "The channel id is not a Discord channel id. It should be a long number.", permanent: true);
        }

        try
        {
            var channel = _client.GetChannel(id) as IMessageChannel
                ?? await _client.GetChannelAsync(id).ConfigureAwait(false) as IMessageChannel;

            if (channel is null)
            {
                return DiscordPostOutcome.Failed(
                    "Discord does not know that channel, or the bot cannot see it. Check the id and "
                    + "the bot's access to the channel.", permanent: true);
            }

            return await work(channel).ConfigureAwait(false);
        }
        catch (HttpException e)
        {
            var permanent = e.HttpCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound;
            return DiscordPostOutcome.Failed(Explain(e), permanent);
        }
        catch (RateLimitedException)
        {
            return DiscordPostOutcome.Failed("Discord is rate limiting the bot; it will try again shortly.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return DiscordPostOutcome.Failed($"Could not post to Discord: {e.Message}");
        }
    }

    public async Task<DiscordPostOutcome> SendDirectMessageAsync(
        string userId, string text, IReadOnlyList<DiscordLinkButton>? links, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if (!ulong.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return DiscordPostOutcome.Failed("That is not a Discord user id.", permanent: true);

        try
        {
            var user = await _client.Rest.GetUserAsync(id).ConfigureAwait(false);
            if (user is null)
                return DiscordPostOutcome.Failed("Discord does not know that user.", permanent: true);

            var channel = await user.CreateDMChannelAsync().ConfigureAwait(false);
            var sent = await channel.SendMessageAsync(
                    text: text,
                    allowedMentions: AllowedMentions.None,
                    components: Buttons(links) is { Components.Count: > 0 } buttons ? buttons : null)
                .ConfigureAwait(false);

            return DiscordPostOutcome.Posted(Text(sent.Id));
        }
        catch (HttpException e) when (e.DiscordCode == DiscordErrorCode.CannotSendMessageToUser)
        {
            return new DiscordPostOutcome(
                false, "That member does not accept direct messages.", Permanent: true, DirectMessagesClosed: true);
        }
        catch (HttpException e)
        {
            var permanent = e.HttpCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound;
            return DiscordPostOutcome.Failed($"Could not send the direct message: Discord answered {(int)e.HttpCode}.", permanent);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return DiscordPostOutcome.Failed($"Could not send the direct message: {e.Message}");
        }
    }

    public Task<DiscordPostOutcome> MentionAsync(
        string channelId, string userId, string text, IReadOnlyList<DiscordLinkButton>? links, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if (!ulong.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return Task.FromResult(DiscordPostOutcome.Failed("That is not a Discord user id.", permanent: true));

        return InChannelAsync(channelId, async channel =>
        {
            // Only this one person may be pinged, whatever else the text happens to contain.
            var sent = await channel.SendMessageAsync(
                    text: $"<@{Text(id)}> {text}",
                    allowedMentions: new AllowedMentions { UserIds = [id] },
                    components: Buttons(links) is { Components.Count: > 0 } buttons ? buttons : null)
                .ConfigureAwait(false);

            return DiscordPostOutcome.Posted(Text(sent.Id));
        });
    }

    public Task<DiscordRoleOutcome> AddRoleAsync(string guildId, string userId, string roleId, CancellationToken ct)
        => RoleAsync(guildId, userId, roleId, add: true);

    public Task<DiscordRoleOutcome> RemoveRoleAsync(string guildId, string userId, string roleId, CancellationToken ct)
        => RoleAsync(guildId, userId, roleId, add: false);

    /// <summary>
    /// One role change by ids, over REST. The member does not have to be in the session's cache,
    /// which without the members intent they usually are not.
    /// </summary>
    private async Task<DiscordRoleOutcome> RoleAsync(string guildId, string userId, string roleId, bool add)
    {
        if (!ulong.TryParse(guildId, NumberStyles.None, CultureInfo.InvariantCulture, out var guild)
            || !ulong.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out var user))
        {
            return DiscordRoleOutcome.Failed("That is not a Discord id.");
        }

        if (!ulong.TryParse(roleId, NumberStyles.None, CultureInfo.InvariantCulture, out var role))
            return DiscordRoleOutcome.NoSuchRole;

        var options = new RequestOptions { AuditLogReason = "Modbot: linked VRChat account" };

        try
        {
            if (add)
                await _client.Rest.AddRoleAsync(guild, user, role, options).ConfigureAwait(false);
            else
                await _client.Rest.RemoveRoleAsync(guild, user, role, options).ConfigureAwait(false);

            return DiscordRoleOutcome.Ok;
        }
        catch (HttpException e) when (e.DiscordCode == DiscordErrorCode.UnknownMember)
        {
            return DiscordRoleOutcome.MemberNotInServer;
        }
        catch (HttpException e) when (e.DiscordCode == DiscordErrorCode.UnknownRole)
        {
            return DiscordRoleOutcome.NoSuchRole;
        }
        catch (HttpException e) when (e.HttpCode == HttpStatusCode.Forbidden)
        {
            return DiscordRoleOutcome.Failed(
                "The bot may not change that role. Give it Manage Roles and keep its role above the linked roles.");
        }
        catch (HttpException e)
        {
            return DiscordRoleOutcome.Failed($"Could not change the role: Discord answered {(int)e.HttpCode}.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return DiscordRoleOutcome.Failed($"Could not change the role: {e.Message}");
        }
    }

    public async Task DisconnectAsync()
    {
        _state = DiscordGatewayState.Disconnected;

        try
        {
            await _client.StopAsync().ConfigureAwait(false);
            await _client.LogoutAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Already gone is the outcome wanted.
            _log.Debug(e, "Ignoring an error while closing the Discord session");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);

        _client.Log -= OnLog;
        _client.Connected -= OnConnected;
        _client.Ready -= OnReady;
        _client.Disconnected -= OnDisconnected;
        _client.SlashCommandExecuted -= OnSlashCommand;
        _client.ChannelCreated -= OnChannelCreated;
        _client.ChannelUpdated -= OnChannelUpdated;
        _client.ChannelDestroyed -= OnChannelDestroyed;
        _client.RoleCreated -= OnRoleCreated;
        _client.RoleUpdated -= OnRoleUpdated;
        _client.RoleDeleted -= OnRoleDeleted;
        _client.GuildUpdated -= OnGuildUpdated;
        _client.GuildMemberUpdated -= OnGuildMemberUpdated;
        _client.UserJoined -= OnUserJoined;

        _client.Dispose();
    }

    public DiscordServerSnapshot? ReadServer(string guildId)
    {
        if (!ulong.TryParse(guildId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return null;

        if (_client.GetGuild(id) is not { } guild)
            return null;

        // The bot's own member arrives with the server under the Guilds intent. Until it does,
        // every permission reads as missing rather than guessed.
        var me = guild.CurrentUser;
        var serverWide = me?.GuildPermissions ?? GuildPermissions.None;

        var channels = guild.Channels
            .Select(Describe)
            .OfType<DiscordChannelSnapshot>()
            .ToArray();

        var roles = guild.Roles
            .Select(r => Describe(r, me))
            .ToArray();

        return new DiscordServerSnapshot(
            Text(guild.Id),
            guild.Name,
            serverWide.ViewAuditLog,
            serverWide.ManageRoles,
            channels,
            roles);
    }

    // ── Channel and role changes ───────────────────────────────────────────────────────────
    //
    // Each is described on the gateway task, from the library's cache, and handed on. A change
    // that can move the bot's permissions everywhere -- a role, the server, the bot's own roles --
    // is passed on as "the server changed" and read again whole, because working out which
    // channels one role edit touched is exactly the permission arithmetic the library already does.

    private Task OnChannelCreated(SocketChannel channel) => ChannelChangedTo(channel);

    private Task OnChannelUpdated(SocketChannel before, SocketChannel after) => ChannelChangedTo(after);

    private Task ChannelChangedTo(SocketChannel channel)
    {
        if (channel is SocketGuildChannel inServer && Describe(inServer) is { } snapshot)
        {
            var handler = ChannelChanged;
            if (handler is not null)
                _ = Task.Run(() => Guard(handler(Text(inServer.Guild.Id), snapshot), "channel changed"));
        }

        return Task.CompletedTask;
    }

    private Task OnChannelDestroyed(SocketChannel channel)
    {
        if (channel is SocketGuildChannel inServer)
        {
            var handler = ChannelRemoved;
            if (handler is not null)
                _ = Task.Run(() => Guard(handler(Text(inServer.Guild.Id), Text(inServer.Id)), "channel removed"));
        }

        return Task.CompletedTask;
    }

    private Task OnRoleCreated(SocketRole role) => ServerChangedIn(role.Guild.Id);

    private Task OnRoleUpdated(SocketRole before, SocketRole after) => ServerChangedIn(after.Guild.Id);

    private Task OnRoleDeleted(SocketRole role) => ServerChangedIn(role.Guild.Id);

    private Task OnGuildUpdated(SocketGuild before, SocketGuild after) => ServerChangedIn(after.Id);

    private Task OnGuildMemberUpdated(Cacheable<SocketGuildUser, ulong> before, SocketGuildUser after)
    {
        // Only the bot's own roles matter here, and only its own updates arrive without the
        // privileged members intent anyway.
        return after.Id == _client.CurrentUser?.Id
            ? ServerChangedIn(after.Guild.Id)
            : Task.CompletedTask;
    }

    private Task OnUserJoined(SocketGuildUser user)
    {
        var handler = MemberJoined;
        if (handler is not null)
        {
            var joined = new DiscordMemberJoin(Text(user.Guild.Id), Text(user.Id), user.Username, user.IsBot);
            _ = Task.Run(() => Guard(handler(joined), "member joined"));
        }

        return Task.CompletedTask;
    }

    private Task ServerChangedIn(ulong guildId)
    {
        var handler = ServerChanged;
        if (handler is not null)
            _ = Task.Run(() => Guard(handler(Text(guildId)), "server changed"));

        return Task.CompletedTask;
    }

    private static string Text(ulong id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>A channel in the shape Modbot keeps, or null for a thread or anything else not offered.</summary>
    private static DiscordChannelSnapshot? Describe(SocketGuildChannel channel)
    {
        var type = channel.GetChannelType() switch
        {
            ChannelType.Text => DiscordChannelTypes.Text,
            ChannelType.News => DiscordChannelTypes.Announcement,
            ChannelType.Forum => DiscordChannelTypes.Forum,
            ChannelType.Media => DiscordChannelTypes.Media,
            ChannelType.Voice => DiscordChannelTypes.Voice,
            ChannelType.Stage => DiscordChannelTypes.Stage,
            ChannelType.Category => DiscordChannelTypes.Category,
            _ => null,
        };

        if (type is null)
            return null;

        var nsfw = channel switch
        {
            SocketForumChannel forum => forum.IsNsfw,
            ITextChannel text => text.IsNsfw,
            _ => false,
        };

        // Effective permissions: server roles, then category and channel overwrites, with
        // Administrator granting everything -- the library's own resolution.
        var permissions = channel.Guild.CurrentUser is { } me
            ? me.GetPermissions(channel)
            : ChannelPermissions.None;

        return new DiscordChannelSnapshot(
            Text(channel.Id),
            channel.Name,
            type,
            (channel as INestedChannel)?.CategoryId is { } category ? Text(category) : null,
            channel.Position,
            nsfw,
            new DiscordChannelPermissions(
                permissions.ViewChannel,
                permissions.ReadMessageHistory,
                permissions.SendMessages,
                permissions.EmbedLinks,
                permissions.AttachFiles,
                permissions.ManageMessages));
    }

    private static DiscordRoleSnapshot Describe(SocketRole role, SocketGuildUser? me)
    {
        // Discord's own rule for handing out a role: Manage Roles, and the role strictly below
        // the bot's highest. Managed roles and @everyone cannot be given by anybody.
        var canAssign = me is not null
            && me.GuildPermissions.ManageRoles
            && !role.IsManaged
            && !role.IsEveryone
            && role.Position < me.Hierarchy;

        return new DiscordRoleSnapshot(
            Text(role.Id),
            role.Name,
            (int)role.Colors.PrimaryColor.RawValue,
            role.Position,
            role.IsManaged,
            role.IsEveryone,
            canAssign);
    }

    private Task OnConnected()
    {
        // Connected is the socket; Ready is the session. On the first connect nothing is usable
        // until Ready.
        //
        // A reconnect is different. Discord.Net resumes a dropped session where it can, and its
        // RESUMED handler raises no Ready -- only this. Treating that as "still connecting" left
        // the session marked not ready for good, which stopped every post and showed the bot as
        // reconnecting while it was online.
        if (!_sessionReady)
        {
            _state = DiscordGatewayState.Connecting;
            return Task.CompletedTask;
        }

        _state = DiscordGatewayState.Ready;

        var handler = Resumed;
        if (handler is not null)
            _ = Task.Run(() => Guard(handler(), "resumed"));

        return Task.CompletedTask;
    }

    private Task OnReady()
    {
        _sessionReady = true;
        _state = DiscordGatewayState.Ready;

        var handler = Ready;
        if (handler is not null)
            _ = Task.Run(() => Guard(handler(), "ready"));

        return Task.CompletedTask;
    }

    private Task OnDisconnected(Exception? exception)
    {
        _state = DiscordGatewayState.Disconnected;

        var handler = Disconnected;
        if (handler is not null)
            _ = Task.Run(() => Guard(handler(Describe(exception)), "disconnected"));

        return Task.CompletedTask;
    }

    private Task OnSlashCommand(SocketSlashCommand command)
    {
        _ = Task.Run(() => Guard(DispatchAsync(command), "command"));
        return Task.CompletedTask;
    }

    private async Task DispatchAsync(SocketSlashCommand command)
    {
        // Discord gives three seconds to acknowledge. Deferring first, only to the person who
        // asked, buys the database round trips; the answer then arrives as a follow-up.
        await command.DeferAsync(ephemeral: true).ConfigureAwait(false);

        var options = command.Data.Options
            .ToDictionary(
                o => o.Name,
                o => Convert.ToString(o.Value, CultureInfo.InvariantCulture) ?? string.Empty,
                StringComparer.Ordinal);

        var call = new DiscordCommandCall(
            command.User.Id.ToString(CultureInfo.InvariantCulture),
            command.User.Username,
            command.Data.Name,
            options,
            (reply, _) => command.FollowupAsync(
                text: reply.Text,
                embeds: reply.Embeds.Count == 0 ? null : reply.Embeds.Select(ToEmbed).ToArray(),
                ephemeral: true,
                allowedMentions: AllowedMentions.None,
                components: Buttons(reply.Links) is { Components.Count: > 0 } buttons ? buttons : null));

        var handler = CommandReceived;
        if (handler is not null)
            await handler(call).ConfigureAwait(false);
    }

    private async Task Guard(Task work, string what)
    {
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.Error(e, "The Discord {What} handler failed", what);
        }
    }

    private Task OnLog(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical or LogSeverity.Error => LogEventLevel.Error,
            LogSeverity.Warning => LogEventLevel.Warning,
            LogSeverity.Info => LogEventLevel.Information,
            _ => LogEventLevel.Debug,
        };

        _log.Write(level, message.Exception, "Discord.Net {Source}: {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The library's close reason, in a sentence, and whether the same settings can ever work.
    /// Close codes are Discord's: 4004 bad token, 4013 and 4014 refused intents.
    /// </summary>
    private static DiscordDisconnect Describe(Exception? exception) => exception switch
    {
        null => new DiscordDisconnect("The gateway connection closed.", Fatal: false),
        WebSocketClosedException { CloseCode: 4004 } =>
            new DiscordDisconnect("Discord rejected the bot token.", Fatal: true),
        WebSocketClosedException { CloseCode: 4014 } =>
            new DiscordDisconnect(
                "Discord refused the Server Members intent. Turn it on in the Developer Portal under Bot.",
                Fatal: true,
                IntentsRefused: true),
        WebSocketClosedException { CloseCode: 4013 } =>
            new DiscordDisconnect("Discord refused the gateway intents this bot asked for.", Fatal: true),
        WebSocketClosedException w =>
            new DiscordDisconnect($"Discord closed the connection ({w.CloseCode}: {w.Reason}).", Fatal: false),
        _ => new DiscordDisconnect($"The gateway connection dropped: {exception.Message}", Fatal: false),
    };

    private static string Explain(HttpException e)
    {
        var reason = e.HttpCode switch
        {
            HttpStatusCode.Forbidden =>
                "the bot may not post in that channel; give it View Channel, Send Messages and Embed Links there",
            HttpStatusCode.NotFound => "Discord does not know that channel",
            HttpStatusCode.Unauthorized => "the bot token was rejected",
            _ => $"Discord answered {(int)e.HttpCode}",
        };

        return string.IsNullOrEmpty(e.Reason)
            ? $"Could not post to Discord: {reason}."
            : $"Could not post to Discord: {reason} ({e.Reason}).";
    }

    private static ApplicationCommandProperties ToProperties(DiscordCommandDefinition command)
    {
        var builder = new SlashCommandBuilder()
            .WithName(command.Name)
            .WithDescription(command.Description);

        foreach (var option in command.Options)
        {
            builder.AddOption(
                option.Name,
                option.Kind == DiscordOptionKind.WholeNumber
                    ? ApplicationCommandOptionType.Integer
                    : ApplicationCommandOptionType.String,
                option.Description,
                isRequired: option.Required,
                minValue: option.Min,
                maxValue: option.Max);
        }

        return builder.Build();
    }

    /// <summary>Link buttons on one row. An address that is not plain https is left off.</summary>
    private static MessageComponent Buttons(IReadOnlyList<DiscordLinkButton>? links)
    {
        var builder = new ComponentBuilder();

        foreach (var link in links ?? [])
        {
            if (IsHttps(link.Url) && link.Label is { Length: > 0 })
                builder.WithButton(label: link.Label, style: ButtonStyle.Link, url: link.Url);
        }

        return builder.Build();
    }

    private static bool IsHttps(string? address) =>
        address is { Length: > 0 }
        && Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps;

    private static Embed ToEmbed(DiscordEmbedContent content)
    {
        var builder = new EmbedBuilder()
            .WithTitle(content.Title)
            .WithColor(new Color(content.Color));

        if (content.Description is { Length: > 0 })
            builder.WithDescription(content.Description);

        if (content.Timestamp is { } at)
            builder.WithTimestamp(at);

        // Discord refuses the whole message over one bad address, so anything that is not a plain
        // https address is left off rather than allowed to lose the post.
        if (IsHttps(content.Url))
            builder.WithUrl(content.Url);

        if (IsHttps(content.ImageUrl))
            builder.WithImageUrl(content.ImageUrl);

        if (IsHttps(content.ThumbnailUrl))
            builder.WithThumbnailUrl(content.ThumbnailUrl);

        if (content.Footer is { Length: > 0 })
            builder.WithFooter(content.Footer);

        foreach (var field in content.Fields)
            builder.AddField(field.Name, field.Value, field.Inline);

        return builder.Build();
    }
}

public sealed class DiscordNetGatewayFactory : IDiscordGatewayFactory
{
    public IDiscordGateway Create(DiscordGatewayOptions options) => new DiscordNetGateway(options);
}

