using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Modbot.Core.Posts;
using Modbot.Discord.Bot;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.Posts;

/// <summary>
/// Runs <see cref="PostDiscordSender"/> every twenty seconds, only while the bot has a ready
/// session (posts design §3.3).
/// </summary>
/// <remarks>
/// A sibling of the calendar's Discord loop with the same shape, rather than a step inside it, so a
/// calendar pass that throws does not hold posts up, and the other way round.
/// </remarks>
public sealed class PostDiscordService : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    private readonly IServiceScopeFactory _scopes;
    private readonly DiscordBotService _bot;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    public PostDiscordService(
        IServiceScopeFactory scopes,
        DiscordBotService bot,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(bot);

        _scopes = scopes;
        _bot = bot;
        _delay = delay ?? Task.Delay;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_bot.ReadyGateway is { } gateway)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<PostDiscordSender>()
                        .RunOnceAsync(gateway, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "The posts' Discord pass failed; it will run again");
                }
            }

            try
            {
                await _delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

/// <summary>
/// <see cref="IDiscordPostActions"/> through the bot's live session: the Marketing tab's Edit, Delete
/// and publish-again on a post already on Discord (posts design §4.5).
/// </summary>
/// <param name="readyGateway">The bot's session while it is ready, or null.</param>
public sealed class DiscordPostActions(Func<IDiscordGateway?> readyGateway) : IDiscordPostActions
{
    public async Task<PostSiteOutcome> EditAsync(string channelId, string messageId, string text, CancellationToken ct = default)
    {
        if (readyGateway() is not { } gateway)
            return PostSiteOutcome.Offline;

        return Of(await gateway.EditPostAsync(channelId, messageId, text, ct).ConfigureAwait(false));
    }

    public async Task<PostSiteOutcome> DeleteAsync(string channelId, string messageId, string reason, CancellationToken ct = default)
    {
        if (readyGateway() is not { } gateway)
            return PostSiteOutcome.Offline;

        var outcome = await gateway.DeleteMessageAsync(channelId, messageId, reason, ct).ConfigureAwait(false);

        // A message already gone is what a delete wanted.
        return outcome.NotFound ? PostSiteOutcome.Ok : Of(outcome);
    }

    public async Task<PostSiteOutcome> PublishAsync(string channelId, string messageId, CancellationToken ct = default)
    {
        if (readyGateway() is not { } gateway)
            return PostSiteOutcome.Offline;

        return Of(await gateway.PublishAsync(channelId, messageId, ct).ConfigureAwait(false));
    }

    private static PostSiteOutcome Of(DiscordPostOutcome outcome) =>
        outcome.Sent
            ? PostSiteOutcome.Ok
            : PostSiteOutcome.Failed(outcome.Error ?? "Discord refused.", gone: outcome.NotFound);
}
