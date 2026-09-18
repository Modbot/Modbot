using System.Globalization;
using System.Net;
using Discord;
using Discord.Net;
using Discord.Rest;
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
/// <strong>Intents.</strong> <see cref="Intents"/>. Two are privileged and must be switched on in
/// the Developer Portal under Bot, Privileged Gateway Intents: Message Content, because messages
/// are stored in full (M5 spec §5.1), and Server Members, for joins, leaves and role changes.
/// Discord refuses the whole session when one is off (close code 4014) without saying which, so
/// the application's own flags are read to name them on the Health page, and the bot connects
/// again without them (<see cref="DiscordGatewayOptions"/>).
/// </para>
/// <para>
/// <strong>Threading.</strong> The library raises events on its gateway task and complains when a
/// handler holds it for more than a few seconds. Every handler here hands the work to the thread
/// pool at once, so a slow database query answers late rather than stalling the heartbeat.
/// </para>
/// </remarks>
public sealed class DiscordNetGateway : IDiscordGateway
{
    /// <summary>
    /// What the session asks Discord for. The server: channels, roles, slash commands. Messages and
    /// their text. Members, with their bans and timeouts. Voice, for voice sessions.
    /// </summary>
    public const GatewayIntents Intents =
        GatewayIntents.Guilds
        | GatewayIntents.GuildMessages
        | GatewayIntents.MessageContent
        | GatewayIntents.GuildMembers
        | GatewayIntents.GuildVoiceStates
        | GatewayIntents.GuildBans

        // Reactions, for giveaways people enter by reacting (giveaways design §4.2). Not
        // privileged, so it needs nothing switched on in the Developer Portal.
        | GatewayIntents.GuildMessageReactions;

    private readonly DiscordSocketClient _client;
    private readonly ILogger _log;

    /// <summary>What this session asked for: <see cref="Intents"/>, less any refused before.</summary>
    private readonly GatewayIntents _intents;

    private volatile DiscordGatewayState _state = DiscordGatewayState.Disconnected;

    /// <summary>Whether this session has been ready at least once, so a later connect is a resume.</summary>
    private volatile bool _sessionReady;

    public DiscordNetGateway(DiscordGatewayOptions? options = null, ILogger? log = null)
    {
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);

        _intents = IntentsFor(options ?? new DiscordGatewayOptions());

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = _intents,
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

        _client.MessageReceived += OnMessageReceived;
        _client.MessageUpdated += OnMessageUpdated;
        _client.MessageDeleted += OnMessageDeleted;
        _client.MessagesBulkDeleted += OnMessagesBulkDeleted;

        _client.ReactionAdded += OnReactionAdded;
        _client.ReactionRemoved += OnReactionRemoved;

        _client.UserLeft += OnUserLeft;
        _client.UserBanned += OnUserBanned;
        _client.UserUnbanned += OnUserUnbanned;
        _client.UserVoiceStateUpdated += OnVoiceStateUpdated;
        _client.AuditLogCreated += OnAuditLogCreated;
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

    public event Func<DiscordMessageSnapshot, Task>? MessageReceived;

    public event Func<DiscordMessageSnapshot, Task>? MessageEdited;

    public event Func<string, string, IReadOnlyList<string>, Task>? MessagesDeleted;

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
    // ── Reactions ────────────────────────────────────────────────────────────────────────

    public event Func<DiscordReactionSnapshot, Task>? ReactionAdded;

    public event Func<DiscordReactionSnapshot, Task>? ReactionRemoved;

    public Task<DiscordPostOutcome> AddReactionAsync(
        string channelId, string messageId, string emoji, CancellationToken ct)
    {
        if (!ulong.TryParse(messageId, NumberStyles.None, CultureInfo.InvariantCulture, out var message))
            return Task.FromResult(DiscordPostOutcome.Failed("That is not a Discord message id.", permanent: true));

        if (Emoji(emoji) is not { } emote)
            return Task.FromResult(DiscordPostOutcome.Failed("Discord does not recognise that emoji.", permanent: true));

        return InChannelAsync(channelId, async channel =>
        {
            if (await channel.GetMessageAsync(message, options: new RequestOptions { CancelToken = ct })
                    .ConfigureAwait(false) is not IUserMessage found)
            {
                return DiscordPostOutcome.Failed("That message is gone.", permanent: true);
            }

            await found.AddReactionAsync(emote, new RequestOptions { CancelToken = ct }).ConfigureAwait(false);
            return DiscordPostOutcome.Ok;
        });
    }

    /// <summary>
    /// One of the server's own emoji, or a standard one. Null when Discord would take neither.
    /// </summary>
    /// <remarks>
    /// The text is whatever an operator typed into the giveaway, so it is tried as a custom emote
    /// first and left as a plain character otherwise. Nothing validates the character itself:
    /// Discord decides what it accepts, and a list of emoji kept in Modbot would be out of date
    /// the week after it was written.
    /// </remarks>
    private static IEmote? Emoji(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        return Emote.TryParse(trimmed, out var emote) ? emote : new Emoji(trimmed);
    }

    private Task OnReactionAdded(
        Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction)
        => RaiseReaction(ReactionAdded, reaction, "reaction added");

    private Task OnReactionRemoved(
        Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction)
        => RaiseReaction(ReactionRemoved, reaction, "reaction removed");

    private Task RaiseReaction(Func<DiscordReactionSnapshot, Task>? handler, SocketReaction reaction, string what)
    {
        // Only reactions in a server. A reaction on a direct message has no giveaway behind it and
        // no server to check membership against.
        if (handler is null || reaction.Channel is not SocketGuildChannel guildChannel)
            return Task.CompletedTask;

        var snapshot = new DiscordReactionSnapshot(
            Text(guildChannel.Guild.Id),
            Text(reaction.Channel.Id),
            Text(reaction.MessageId),
            Text(reaction.UserId),
            reaction.Emote.ToString() ?? string.Empty,
            reaction.User.IsSpecified ? reaction.User.Value.Username : null,
            reaction.User.IsSpecified && reaction.User.Value.IsBot);

        _ = Task.Run(() => Guard(handler(snapshot), what));
        return Task.CompletedTask;
    }

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

    // ── Server events (calendar design §3.2) ───────────────────────────────────────────────

    public Task<DiscordPostOutcome> CreateEventAsync(
        string guildId, DiscordScheduledEventDetails details, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(details);

        return InGuildAsync(guildId, async guild =>
        {
            using var cover = await CoverImages.FetchAsync(details.CoverImageUrl, ct).ConfigureAwait(false);

            var created = await guild.CreateEventAsync(
                    details.Name,
                    details.StartsAt,
                    GuildScheduledEventType.External,
                    GuildScheduledEventPrivacyLevel.Private,
                    details.Description,
                    details.EndsAt,
                    channelId: null,
                    location: details.Location,
                    coverImage: cover?.Image,
                    options: new RequestOptions { CancelToken = ct })
                .ConfigureAwait(false);

            return DiscordPostOutcome.Posted(Text(created.Id));
        });
    }

    public Task<DiscordPostOutcome> UpdateEventAsync(
        string guildId, string eventId, DiscordScheduledEventDetails details, bool start, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(details);

        if (ParseId(eventId) is not { } id)
            return Task.FromResult(DiscordPostOutcome.Failed("That is not a Discord event id.", permanent: true));

        return InGuildAsync(guildId, async guild =>
        {
            if (await guild.GetEventAsync(id, new RequestOptions { CancelToken = ct }).ConfigureAwait(false) is not { } found)
                return DiscordPostOutcome.Failed(DiscordScheduledEventDetails.Gone, permanent: true);

            if (found.Status is GuildScheduledEventStatus.Completed or GuildScheduledEventStatus.Cancelled)
                return DiscordPostOutcome.Failed(DiscordScheduledEventDetails.Gone, permanent: true);

            using var cover = await CoverImages.FetchAsync(details.CoverImageUrl, ct).ConfigureAwait(false);

            await found.ModifyAsync(e =>
            {
                e.Name = details.Name;
                e.Description = details.Description ?? string.Empty;
                e.Location = details.Location;
                e.EndTime = details.EndsAt;

                // A started event's start cannot move. The caller never sends a start in the past:
                // it knows the time, and this class does not read the clock.
                if (found.Status == GuildScheduledEventStatus.Scheduled)
                    e.StartTime = details.StartsAt;

                if (cover is not null)
                    e.CoverImage = cover.Image;
            }, new RequestOptions { CancelToken = ct }).ConfigureAwait(false);

            if (start && found.Status == GuildScheduledEventStatus.Scheduled)
                await found.StartAsync(new RequestOptions { CancelToken = ct }).ConfigureAwait(false);

            return DiscordPostOutcome.Posted(eventId);
        });
    }

    public Task<DiscordPostOutcome> EndEventAsync(string guildId, string eventId, CancellationToken ct)
    {
        if (ParseId(eventId) is not { } id)
            return Task.FromResult(DiscordPostOutcome.Ok);

        return InGuildAsync(guildId, async guild =>
        {
            // Gone already is what ending wanted.
            if (await guild.GetEventAsync(id, new RequestOptions { CancelToken = ct }).ConfigureAwait(false) is not { } found)
                return DiscordPostOutcome.Ok;

            if (found.Status is GuildScheduledEventStatus.Completed or GuildScheduledEventStatus.Cancelled)
                return DiscordPostOutcome.Ok;

            // Completed when it had started, cancelled when it had not: Discord.Net picks by status.
            await found.EndAsync(new RequestOptions { CancelToken = ct }).ConfigureAwait(false);
            return DiscordPostOutcome.Ok;
        }, goneIsOk: true);
    }

    private async Task<DiscordPostOutcome> InGuildAsync(
        string guildId, Func<SocketGuild, Task<DiscordPostOutcome>> work, bool goneIsOk = false)
    {
        if (ParseId(guildId) is not { } id || _client.GetGuild(id) is not { } guild)
            return DiscordPostOutcome.Failed("The bot is not in that server.", permanent: true);

        try
        {
            return await work(guild).ConfigureAwait(false);
        }
        catch (HttpException e) when (goneIsOk && e.HttpCode == HttpStatusCode.NotFound)
        {
            return DiscordPostOutcome.Ok;
        }
        catch (HttpException e)
        {
            var permanent = e.HttpCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound;
            return DiscordPostOutcome.Failed(
                e.HttpCode == HttpStatusCode.Forbidden
                    ? "The bot may not manage server events; it needs Manage Events."
                    : e.HttpCode == HttpStatusCode.NotFound
                        ? DiscordScheduledEventDetails.Gone
                        : $"Discord answered {(int)e.HttpCode}: {e.Reason ?? e.Message}",
                permanent);
        }
        catch (RateLimitedException)
        {
            return DiscordPostOutcome.Failed("Discord is rate limiting the bot.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return DiscordPostOutcome.Failed($"Could not change the server event: {e.Message}");
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
        _client.MessageReceived -= OnMessageReceived;
        _client.MessageUpdated -= OnMessageUpdated;
        _client.MessageDeleted -= OnMessageDeleted;
        _client.MessagesBulkDeleted -= OnMessagesBulkDeleted;
        _client.UserLeft -= OnUserLeft;
        _client.UserBanned -= OnUserBanned;
        _client.UserUnbanned -= OnUserUnbanned;
        _client.UserVoiceStateUpdated -= OnVoiceStateUpdated;
        _client.AuditLogCreated -= OnAuditLogCreated;

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
            roles,
            BotCanManageEvents: serverWide.ManageEvents);
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
        var handler = MemberUpdated;
        if (handler is not null)
        {
            var snapshot = Describe(after);
            _ = Task.Run(() => Guard(handler(Text(after.Guild.Id), snapshot), "member updated"));
        }

        // The bot's own roles move its permissions everywhere, so the whole server is read again.
        return after.Id == _client.CurrentUser?.Id
            ? ServerChangedIn(after.Guild.Id)
            : Task.CompletedTask;
    }

    // ── Members, voice and moderation ──────────────────────────────────────────────────────

    public event Func<string, string, Task>? MemberLeft;

    public event Func<string, DiscordMemberSnapshot, Task>? MemberUpdated;

    public event Func<string, string, Task>? MemberBanned;

    public event Func<string, string, Task>? MemberUnbanned;

    public event Func<string, string, string?, string?, Task>? VoiceChanged;

    public event Func<string, Task>? AuditLogChanged;

    private Task OnUserJoined(SocketGuildUser user)
    {
        var handler = MemberJoined;
        if (handler is not null)
        {
            var joined = new DiscordMemberJoin(Text(user.Guild.Id), Text(user.Id), user.Username, user.IsBot, Describe(user));
            _ = Task.Run(() => Guard(handler(joined), "member joined"));
        }

        return Task.CompletedTask;
    }

    private Task OnUserLeft(SocketGuild guild, SocketUser user)
    {
        var handler = MemberLeft;
        if (handler is not null)
            _ = Task.Run(() => Guard(handler(Text(guild.Id), Text(user.Id)), "member left"));

        return Task.CompletedTask;
    }

    private Task OnUserBanned(SocketUser user, SocketGuild guild)
    {
        var handler = MemberBanned;
        if (handler is not null)
            _ = Task.Run(() => Guard(handler(Text(guild.Id), Text(user.Id)), "member banned"));

        return Task.CompletedTask;
    }

    private Task OnUserUnbanned(SocketUser user, SocketGuild guild)
    {
        var handler = MemberUnbanned;
        if (handler is not null)
            _ = Task.Run(() => Guard(handler(Text(guild.Id), Text(user.Id)), "member unbanned"));

        return Task.CompletedTask;
    }

    private Task OnVoiceStateUpdated(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        var handler = VoiceChanged;
        var from = before.VoiceChannel;
        var to = after.VoiceChannel;
        var guild = to?.Guild ?? from?.Guild;

        // Muting, deafening or starting a stream changes the state without changing the channel.
        if (handler is not null && guild is not null && from?.Id != to?.Id)
        {
            _ = Task.Run(() => Guard(
                handler(Text(guild.Id), Text(user.Id), from is null ? null : Text(from.Id), to is null ? null : Text(to.Id)),
                "voice changed"));
        }

        return Task.CompletedTask;
    }

    private Task OnAuditLogCreated(SocketAuditLogEntry entry, SocketGuild guild)
    {
        var handler = AuditLogChanged;
        if (handler is not null)
            _ = Task.Run(() => Guard(handler(Text(guild.Id)), "audit log changed"));

        return Task.CompletedTask;
    }

    public async Task<DiscordAuditPage> ReadAuditLogAsync(string guildId, string? afterId, CancellationToken ct)
    {
        if (ParseId(guildId) is not { } id || _client.GetGuild(id) is not { } guild)
            return new DiscordAuditPage([], null, NoAccess: true, Error: "The bot is not in that server.");

        if (guild.CurrentUser is { GuildPermissions.ViewAuditLog: false })
            return new DiscordAuditPage([], null, NoAccess: true, Error: "The bot may not read the audit log.");

        try
        {
            var after = ParseId(afterId);

            // A thousand entries is ten requests. Past the first read, which walks back as far as
            // Discord keeps, a gap that size is days of a busy server's moderation.
            var pages = after is { } a
                ? guild.GetAuditLogsAsync(1000, Request(ct), afterId: a)
                : guild.GetAuditLogsAsync(1000, Request(ct));

            var entries = (await pages.FlattenAsync().ConfigureAwait(false))
                .OrderBy(e => e.Id)
                .ToList();

            return new DiscordAuditPage(
                entries.Select(Describe).OfType<DiscordAuditEntry>().ToList(),
                entries.Count > 0 ? Text(entries[^1].Id) : null);
        }
        catch (HttpException e) when (e.HttpCode == HttpStatusCode.Forbidden)
        {
            return new DiscordAuditPage([], null, NoAccess: true, Error: "The bot may not read the audit log.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new DiscordAuditPage([], null, Error: $"Could not read the audit log: {e.Message}");
        }
    }

    public async Task<IReadOnlyList<DiscordMemberSnapshot>?> ReadMembersAsync(string guildId, CancellationToken ct)
    {
        if (ParseId(guildId) is not { } id || _client.GetGuild(id) is not { } guild)
            return null;

        // Without the Server Members intent Discord sends no list; the Health card already says why.
        if (!_intents.HasFlag(GatewayIntents.GuildMembers))
            return null;

        try
        {
            // Over the gateway in chunks of a thousand, not REST pages, so no REST limit is spent
            // however large the server; the session keeps the list current after.
            await guild.DownloadUsersAsync().ConfigureAwait(false);
            return guild.Users.Select(Describe).ToList();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warning(e, "Could not read the Discord server's member list");
            return null;
        }
    }

    public IReadOnlyList<DiscordVoiceState> ReadVoice(string guildId)
    {
        if (ParseId(guildId) is not { } id || _client.GetGuild(id) is not { } guild)
            return [];

        return guild.VoiceChannels
            .SelectMany(channel => channel.ConnectedUsers.Select(user => new DiscordVoiceState(Text(user.Id), Text(channel.Id))))
            .ToList();
    }

    public int? ReadMemberCount(string guildId)
        => ParseId(guildId) is { } id && _client.GetGuild(id) is { } guild ? guild.MemberCount : null;

    private static DiscordMemberSnapshot Describe(IGuildUser user) => new(
        Text(user.Id),
        user.Username ?? string.Empty,
        user.DisplayName ?? user.Username ?? string.Empty,
        user.Nickname,
        user.IsBot,
        user.JoinedAt,
        user.RoleIds.Where(r => r != user.GuildId).Select(Text).ToArray(),
        user.TimedOutUntil,
        user.GlobalName,
        user.GetDisplayAvatarUrl() ?? user.GetDefaultAvatarUrl(),
        user.IsPending ?? false,
        user.PremiumSince);

    /// <summary>An audit log entry in Modbot's words, or null for a kind Modbot does not record.</summary>
    private static DiscordAuditEntry? Describe(RestAuditLogEntry entry)
    {
        var id = Text(entry.Id);
        var actor = entry.User is { } user ? Text(user.Id) : null;
        var reason = string.IsNullOrWhiteSpace(entry.Reason) ? null : entry.Reason;

        return entry.Data switch
        {
            BanAuditLogData ban => new(id, entry.CreatedAt, DiscordAuditKinds.Ban, actor, Target(ban.Target), reason),
            UnbanAuditLogData unban => new(id, entry.CreatedAt, DiscordAuditKinds.Unban, actor, Target(unban.Target), reason),
            KickAuditLogData kick => new(id, entry.CreatedAt, DiscordAuditKinds.Kick, actor, Target(kick.Target), reason),

            MemberUpdateAuditLogData member when member.Before.TimedOutUntil != member.After.TimedOutUntil =>
                member.After.TimedOutUntil is { } until
                    ? new(id, entry.CreatedAt, DiscordAuditKinds.Timeout, actor, Target(member.Target), reason, Until: until)
                    : new(id, entry.CreatedAt, DiscordAuditKinds.TimeoutRemoved, actor, Target(member.Target), reason),

            MemberRoleAuditLogData roles => new(
                id, entry.CreatedAt, DiscordAuditKinds.Roles, actor, Target(roles.Target), reason,
                Roles: roles.Roles.Select(r => new DiscordRoleChange(Text(r.RoleId), r.Name ?? string.Empty, r.Added)).ToArray()),

            MessageDeleteAuditLogData deleted => new(
                id, entry.CreatedAt, DiscordAuditKinds.MessagesDeleted, actor, Target(deleted.Target), reason,
                ChannelId: Text(deleted.ChannelId), Count: deleted.MessageCount),

            MessageBulkDeleteAuditLogData bulk => new(
                id, entry.CreatedAt, DiscordAuditKinds.MessagesBulkDeleted, actor, Text(bulk.ChannelId), reason,
                ChannelId: Text(bulk.ChannelId), Count: bulk.MessageCount),

            ChannelCreateAuditLogData created => new(
                id, entry.CreatedAt, DiscordAuditKinds.ChannelCreated, actor, Text(created.ChannelId), reason, Name: created.ChannelName),
            ChannelUpdateAuditLogData changed => new(
                id, entry.CreatedAt, DiscordAuditKinds.ChannelChanged, actor, Text(changed.ChannelId), reason, Name: changed.After.Name),
            ChannelDeleteAuditLogData removed => new(
                id, entry.CreatedAt, DiscordAuditKinds.ChannelDeleted, actor, Text(removed.ChannelId), reason, Name: removed.ChannelName),

            RoleCreateAuditLogData created => new(
                id, entry.CreatedAt, DiscordAuditKinds.RoleCreated, actor, Text(created.RoleId), reason, Name: created.Properties.Name),
            RoleUpdateAuditLogData changed => new(
                id, entry.CreatedAt, DiscordAuditKinds.RoleChanged, actor, Text(changed.RoleId), reason, Name: changed.After.Name),
            RoleDeleteAuditLogData removed => new(
                id, entry.CreatedAt, DiscordAuditKinds.RoleDeleted, actor, Text(removed.RoleId), reason, Name: removed.Properties.Name),

            _ => null,
        };

        static string? Target(IUser? target) => target is null ? null : Text(target.Id);
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

    // ── Messages ───────────────────────────────────────────────────────────────────────────
    //
    // Described on the gateway task, where the library's objects are still valid, and handed to
    // the thread pool. The message cache is off, so an edit or a delete carries no copy of the
    // message before it: the store has that.

    private Task OnMessageReceived(SocketMessage message)
    {
        var handler = MessageReceived;
        if (handler is not null && Describe(message, message.Channel) is { } snapshot)
            _ = Task.Run(() => Guard(handler(snapshot), "message received"));

        return Task.CompletedTask;
    }

    private Task OnMessageUpdated(Cacheable<IMessage, ulong> before, SocketMessage after, ISocketMessageChannel channel)
    {
        var handler = MessageEdited;
        if (handler is not null && after is not null && Describe(after, channel) is { } snapshot)
            _ = Task.Run(() => Guard(handler(snapshot), "message edited"));

        return Task.CompletedTask;
    }

    private Task OnMessageDeleted(Cacheable<IMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel)
        => Deleted(channel.Id, [message.Id]);

    private Task OnMessagesBulkDeleted(
        IReadOnlyCollection<Cacheable<IMessage, ulong>> messages, Cacheable<IMessageChannel, ulong> channel)
        => Deleted(channel.Id, messages.Select(m => m.Id).ToArray());

    private Task Deleted(ulong channelId, IReadOnlyList<ulong> messageIds)
    {
        var handler = MessagesDeleted;

        // The channel is looked up in the session to learn its server; a delete in a direct
        // message has none and is not Modbot's business.
        if (handler is not null && _client.GetChannel(channelId) is IGuildChannel inServer)
        {
            var ids = messageIds.Select(Text).ToArray();
            _ = Task.Run(() => Guard(handler(Text(inServer.GuildId), Text(channelId), ids), "messages deleted"));
        }

        return Task.CompletedTask;
    }

    public async Task<DiscordMessagePage> ReadMessagesAsync(
        string channelId, string? beforeId, string? afterId, CancellationToken ct)
    {
        if (!ulong.TryParse(channelId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return DiscordMessagePage.Failed("That is not a Discord channel id.", noAccess: true);

        var before = ParseId(beforeId);
        var after = ParseId(afterId);

        try
        {
            var channel = _client.GetChannel(id) as IMessageChannel
                ?? await _client.GetChannelAsync(id, Request(ct)).ConfigureAwait(false) as IMessageChannel;

            if (channel is not IGuildChannel)
                return DiscordMessagePage.Failed("Discord does not know that channel, or the bot cannot see it.", noAccess: true);

            // A limit of one page is one request. The library queues it behind Discord's rate
            // limit for the channel and waits a 429 out itself; nothing here retries.
            var pages = before is { } b
                ? channel.GetMessagesAsync(b, Direction.Before, DiscordMessagePage.Size, CacheMode.AllowDownload, Request(ct))
                : after is { } a
                    ? channel.GetMessagesAsync(a, Direction.After, DiscordMessagePage.Size, CacheMode.AllowDownload, Request(ct))
                    : channel.GetMessagesAsync(DiscordMessagePage.Size, CacheMode.AllowDownload, Request(ct));

            var messages = (await pages.FlattenAsync().ConfigureAwait(false))
                .OrderByDescending(m => m.Id)
                .ToList();

            return new DiscordMessagePage(
                messages.Select(m => Describe(m, channel)).OfType<DiscordMessageSnapshot>().ToList(),
                messages.Count > 0 ? Text(messages[^1].Id) : null,
                messages.Count > 0 ? Text(messages[0].Id) : null,
                Full: messages.Count >= DiscordMessagePage.Size);
        }
        catch (HttpException e) when (e.HttpCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return DiscordMessagePage.Failed(
                e.HttpCode == HttpStatusCode.NotFound
                    ? "The channel is gone."
                    : "The bot may not read this channel's history.",
                noAccess: true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return DiscordMessagePage.Failed($"Could not read messages from Discord: {e.Message}", noAccess: false);
        }
    }

    public async Task<IReadOnlyList<DiscordThreadSnapshot>> ReadThreadsAsync(
        string guildId, IReadOnlyList<string> channelIds, IReadOnlyList<string> archivedIn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channelIds);
        ArgumentNullException.ThrowIfNull(archivedIn);

        if (!ulong.TryParse(guildId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || _client.GetGuild(id) is not { } guild)
        {
            return [];
        }

        var wanted = channelIds.ToHashSet(StringComparer.Ordinal);
        var threads = new Dictionary<ulong, DiscordThreadSnapshot>();

        // Open threads arrive with the server and cost no request.
        foreach (var thread in guild.ThreadChannels)
        {
            if (thread.ParentChannel is { } parent && wanted.Contains(Text(parent.Id)))
                threads[thread.Id] = new DiscordThreadSnapshot(Text(thread.Id), Text(parent.Id), thread.Name, thread.IsArchived);
        }

        // Archived public threads cost a request per fifty, per channel. Private archived threads
        // need Manage Threads and are left out.
        foreach (var channelId in archivedIn.Where(wanted.Contains))
        {
            if (ParseId(channelId) is not { } cid || guild.GetChannel(cid) is not IThreadContainerChannel container)
                continue;

            DateTimeOffset? before = null;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var page = await container
                        .GetPublicArchivedThreadsAsync(50, before, Request(ct))
                        .ConfigureAwait(false);

                    foreach (var thread in page)
                        threads.TryAdd(thread.Id, new DiscordThreadSnapshot(Text(thread.Id), channelId, thread.Name, Archived: true));

                    if (page.Count < 50)
                        break;

                    before = page.Min(t => t.ArchiveTimestamp);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A channel whose archive cannot be listed still has its open threads read.
                _log.Debug(e, "Could not list the archived threads in channel {ChannelId}", channelId);
            }
        }

        return threads.Values.ToList();
    }

    private static RequestOptions Request(CancellationToken ct) => new() { CancelToken = ct };

    private static ulong? ParseId(string? id)
        => id is not null && ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>A message in the shape Modbot stores, or null for a system line or a direct message.</summary>
    private static DiscordMessageSnapshot? Describe(IMessage message, IChannel channel)
    {
        if (message is not IUserMessage user || message.Source == MessageSource.System)
            return null;

        if (channel is not IGuildChannel inServer)
            return null;

        // In the library's model a thread's parent channel is its CategoryId.
        var channelId = Text(channel.Id);
        string? threadId = null;

        if (channel is IThreadChannel thread && ((INestedChannel)thread).CategoryId is { } parent)
        {
            channelId = Text(parent);
            threadId = Text(thread.Id);
        }

        var author = message.Author;
        var name = (author as IGuildUser)?.DisplayName ?? author.GlobalName ?? author.Username ?? string.Empty;

        return new DiscordMessageSnapshot(
            Text(message.Id),
            Text(inServer.GuildId),
            channelId,
            threadId,
            Text(author.Id),
            name,
            author.IsBot || author.IsWebhook,
            message.Timestamp,
            message.EditedTimestamp,
            message.Content ?? string.Empty,
            message.Attachments
                .Select(a => new DiscordAttachmentSnapshot(a.Filename, a.ContentType, a.Size, a.Url))
                .ToArray(),
            message.Embeds.Count,
            message.Type == MessageType.Reply && message.Reference?.MessageId is { IsSpecified: true } reply
                ? Text(reply.Value)
                : null,
            message.MentionedUserIds.Count + message.MentionedRoleIds.Count + (message.MentionedEveryone ? 1 : 0),
            user.IsPinned);
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

        _ = Task.Run(() => Guard(EnsureAvatarAsync(), "avatar"));

        var handler = Ready;
        if (handler is not null)
            _ = Task.Run(() => Guard(handler(), "ready"));

        return Task.CompletedTask;
    }

    /// <summary>Whether this process has already looked at the bot's avatar, so it does so once.</summary>
    private int _avatarChecked;

    /// <summary>
    /// Gives a bot that still has Discord's default avatar the Modbot mark, once per process.
    /// </summary>
    /// <remarks>
    /// Only the default avatar is replaced: an operator who uploaded their own picture in the
    /// developer portal keeps it. Discord limits how often an avatar may change, so the upload is
    /// attempted once and a refusal is logged rather than retried.
    /// </remarks>
    private async Task EnsureAvatarAsync()
    {
        if (Interlocked.Exchange(ref _avatarChecked, 1) != 0 || _client.CurrentUser?.AvatarId is not null)
            return;

        await using var picture = typeof(DiscordNetGateway).Assembly.GetManifestResourceStream("Modbot.Discord.Assets.avatar.png");
        if (picture is null)
            return;

        await _client.CurrentUser!.ModifyAsync(u => u.Avatar = new Image(picture)).ConfigureAwait(false);
        _log.Information("Set the bot's avatar to the Modbot mark, since it still had Discord's default");
    }

    private Task OnDisconnected(Exception? exception)
    {
        _state = DiscordGatewayState.Disconnected;

        var handler = Disconnected;
        if (handler is not null)
            _ = Task.Run(() => Guard(DescribeDisconnectAsync(exception, handler), "disconnected"));

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

    private async Task DescribeDisconnectAsync(Exception? exception, Func<DiscordDisconnect, Task> handler)
    {
        var disconnect = Describe(exception);

        if (exception is WebSocketClosedException { CloseCode: 4014 })
        {
            var missing = await MissingIntentsAsync().ConfigureAwait(false);
            if (missing.Count > 0)
            {
                disconnect = disconnect with
                {
                    Reason = "Discord refused the gateway intents. Turn these on in the Developer Portal under Bot: "
                        + string.Join(", ", missing) + ".",
                    MissingIntents = missing,
                };
            }
        }

        await handler(disconnect).ConfigureAwait(false);
    }

    /// <summary>
    /// The privileged intents this bot asks for that are switched off for the application.
    /// </summary>
    /// <remarks>
    /// Close code 4014 says only "disallowed intents". The application's flags say which are on:
    /// the plain flag for a verified bot, the limited one for a bot in fewer than a hundred
    /// servers, which is every self-hosted Modbot. Signing in over REST has already worked by the
    /// time the gateway refuses, so this one request can be made. Empty when it cannot.
    /// </remarks>
    private async Task<IReadOnlyList<string>> MissingIntentsAsync()
    {
        try
        {
            var application = await _client.GetApplicationInfoAsync().ConfigureAwait(false);
            return MissingIntents(application.Flags, _intents);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Debug(e, "Could not read the application's flags to name the refused intents");
            return [];
        }
    }

    /// <summary>Which privileged intents in <see cref="Intents"/> the flags leave off, by the Developer Portal's names.</summary>
    public static IReadOnlyList<string> MissingIntents(ApplicationFlags flags) => MissingIntents(flags, Intents);

    /// <summary>The intents a session made with these options asks for.</summary>
    public static GatewayIntents IntentsFor(DiscordGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var intents = Intents;

        if (!options.MemberEvents)
            intents &= ~GatewayIntents.GuildMembers;

        if (!options.MessageContent)
            intents &= ~GatewayIntents.MessageContent;

        return intents;
    }

    private static IReadOnlyList<string> MissingIntents(ApplicationFlags flags, GatewayIntents intents)
    {
        var missing = new List<string>();

        if (intents.HasFlag(GatewayIntents.GuildPresences)
            && !flags.HasFlag(ApplicationFlags.GatewayPresence)
            && !flags.HasFlag(ApplicationFlags.GatewayPresenceLimited))
        {
            missing.Add("Presence Intent");
        }

        if (intents.HasFlag(GatewayIntents.GuildMembers)
            && !flags.HasFlag(ApplicationFlags.GatewayGuildMembers)
            && !flags.HasFlag(ApplicationFlags.GatewayGuildMembersLimited))
        {
            missing.Add("Server Members Intent");
        }

        if (intents.HasFlag(GatewayIntents.MessageContent)
            && !flags.HasFlag(ApplicationFlags.GatewayMessageContent)
            && !flags.HasFlag(ApplicationFlags.GatewayMessageContentLimited))
        {
            missing.Add("Message Content Intent");
        }

        return missing;
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
                "Discord refused a privileged intent. Turn on Server Members Intent and Message Content Intent in the Developer Portal under Bot.",
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
            builder.WithFooter(content.Footer, IsHttps(content.FooterIconUrl) ? content.FooterIconUrl : null);

        foreach (var field in content.Fields)
            builder.AddField(field.Name, field.Value, field.Inline);

        return builder.Build();
    }
}

public sealed class DiscordNetGatewayFactory : IDiscordGatewayFactory
{
    public IDiscordGateway Create(DiscordGatewayOptions options) => new DiscordNetGateway(options);
}

