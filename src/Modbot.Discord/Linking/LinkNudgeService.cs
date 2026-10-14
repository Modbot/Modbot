using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Discord;
using Modbot.Core.Logging;
using Modbot.Discord.Bot;
using Serilog;

namespace Modbot.Discord.Linking;

/// <summary>
/// Takes a turn of the link reminder every <see cref="LinkNudgeQueue.Gap"/> while the bot has a ready
/// session. A turn does nothing unless staff have started the reminder.
/// </summary>
/// <remarks>Its own loop, like the linked-role job, so a slow or failing reminder holds up nothing else.</remarks>
public sealed class LinkNudgeService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DiscordBotService _bot;
    private readonly ILogger _log;

    public LinkNudgeService(IServiceScopeFactory scopes, DiscordBotService bot, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(bot);

        _scopes = scopes;
        _bot = bot;
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
                    var nudges = scope.ServiceProvider.GetRequiredService<LinkNudges>();
                    var step = await nudges.SendNextAsync(gateway, stoppingToken).ConfigureAwait(false);

                    if (step == LinkNudgeStep.Finished)
                        _log.Information("The link reminder has reached everyone it began with");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    _log.Warning(e, "A turn of the link reminder failed; trying again shortly");
                }
            }

            try
            {
                await Task.Delay(LinkNudgeQueue.Gap, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
