using System.Globalization;
using System.Net;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Modbot.Core.Logging;
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
/// <strong>Intents.</strong> <c>Guilds</c> only. It is what slash commands and channel lookups need
/// and it is not privileged, so nothing has to be switched on in the Developer Portal. Message
/// content is never requested: the bot reads no messages.
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

    public DiscordNetGateway(ILogger? log = null)
    {
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
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
    }

    public DiscordGatewayState State => _state;

    public event Func<Task>? Ready;

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
        PostAsync(channelId, text: null, embeds, ct);

    public Task<DiscordPostOutcome> PostAsync(
        string channelId, string? text, IReadOnlyList<DiscordEmbedContent> embeds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(embeds);

        return InChannelAsync(channelId, async channel =>
        {
            // Names inside an embed never ping anybody, even one that happens to read like @here,
            // and neither does the operator's own line above it.
            var sent = await channel.SendMessageAsync(
                    text: text,
                    embeds: embeds.Select(ToEmbed).ToArray(),
                    allowedMentions: AllowedMentions.None)
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

        _client.Dispose();
    }

    private Task OnConnected()
    {
        // Connected is the socket; Ready is the session. Nothing is usable until Ready.
        _state = DiscordGatewayState.Connecting;
        return Task.CompletedTask;
    }

    private Task OnReady()
    {
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
                allowedMentions: AllowedMentions.None));

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
        WebSocketClosedException { CloseCode: 4013 or 4014 } =>
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

    private static Embed ToEmbed(DiscordEmbedContent content)
    {
        var builder = new EmbedBuilder()
            .WithTitle(content.Title)
            .WithColor(new Color(content.Color));

        if (content.Description is { Length: > 0 })
            builder.WithDescription(content.Description);

        if (content.Timestamp is { } at)
            builder.WithTimestamp(at);

        if (content.Url is { Length: > 0 })
            builder.WithUrl(content.Url);

        if (content.Footer is { Length: > 0 })
            builder.WithFooter(content.Footer);

        foreach (var field in content.Fields)
            builder.AddField(field.Name, field.Value, field.Inline);

        return builder.Build();
    }
}

public sealed class DiscordNetGatewayFactory : IDiscordGatewayFactory
{
    public IDiscordGateway Create() => new DiscordNetGateway();
}
