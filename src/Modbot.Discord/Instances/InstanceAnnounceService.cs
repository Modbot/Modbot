using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Modbot.Discord.Bot;
using Serilog;

namespace Modbot.Discord.Instances;

/// <summary>
/// Runs <see cref="InstanceAnnouncer"/> on a timer, only while the bot has a ready session.
/// </summary>
/// <remarks>
/// <para>
/// Its own service rather than a second job inside the moderation log's loop, for the same reason
/// the producers are separate everywhere else in Modbot: a channel that has been deleted, or a
/// Discord outage, should stop the thing that depends on it and nothing else. A group announcing
/// its instances must not be able to hold up the moderation log, which is the record.
/// </para>
/// <para>
/// Twenty seconds between passes. Individual cards are rewritten at most once a minute
/// (<see cref="InstanceAnnouncer.RewriteEvery"/>) -- this interval only decides how quickly a
/// instance that has just opened gets its first card, which is the part anybody notices.
/// </para>
/// </remarks>
public sealed class InstanceAnnounceService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DiscordBotService _bot;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(2);

    public InstanceAnnounceService(
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
            var wait = PollInterval;

            if (_bot.ReadyGateway is { } gateway)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var announcer = scope.ServiceProvider.GetRequiredService<InstanceAnnouncer>();
                    var pass = await announcer.RunOnceAsync(gateway, stoppingToken).ConfigureAwait(false);

                    if (pass.Outcome == InstanceAnnouncePassOutcome.Failed)
                    {
                        wait = RetryAfterFailure;
                        _log.Warning("Could not announce instances in Discord: {Reason}", pass.Error ?? "no detail");
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A failure here must not take the host down, and must not stop the bot: the
                    // next pass gets a fresh scope and a fresh context.
                    _log.Error(ex, "The instance announcer failed; it will try again");
                    wait = RetryAfterFailure;
                }
            }

            try
            {
                await _delay(wait, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
