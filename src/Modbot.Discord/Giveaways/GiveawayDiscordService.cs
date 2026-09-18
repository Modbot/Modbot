using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Modbot.Discord.Bot;
using Serilog;

namespace Modbot.Discord.Giveaways;

/// <summary>
/// Runs <see cref="GiveawayDiscordPublisher"/> every twenty seconds, only while the bot has a ready
/// session (giveaways design §7.2).
/// </summary>
/// <remarks>
/// Its own loop, like the calendar's and the instance announcer's, so a server that has taken the
/// bot's access to a channel away stops the giveaway posts and nothing else.
/// </remarks>
public sealed class GiveawayDiscordService : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    private readonly IServiceScopeFactory _scopes;
    private readonly DiscordBotService _bot;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    public GiveawayDiscordService(
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
                    var publisher = scope.ServiceProvider.GetRequiredService<GiveawayDiscordPublisher>();
                    await publisher.RunOnceAsync(gateway, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "The giveaways' Discord pass failed; it will run again");
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
