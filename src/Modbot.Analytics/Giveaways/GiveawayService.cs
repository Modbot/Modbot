using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Serilog;

namespace Modbot.Analytics.Giveaways;

/// <summary>
/// Runs <see cref="GiveawayScheduler"/> every half a minute: closes giveaways on time and draws
/// the ones with a draw time.
/// </summary>
/// <remarks>
/// Half a minute rather than every few seconds because the times involved are hours and days, and
/// a giveaway drawn thirty seconds late is a giveaway drawn on time. A pass with nothing to do is
/// two indexed queries.
/// </remarks>
public sealed class GiveawayService : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    public GiveawayService(
        IServiceScopeFactory scopes,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        _scopes = scopes;
        _delay = delay ?? Task.Delay;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Analytics);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var scheduler = scope.ServiceProvider.GetRequiredService<GiveawayScheduler>();
                var pass = await scheduler.RunOnceAsync(stoppingToken).ConfigureAwait(false);

                foreach (var (id, problem) in pass.Problems)
                    _log.Warning("The giveaway {Giveaway} could not be drawn: {Reason}", id, problem);

                if (pass.Closed > 0 || pass.Drawn > 0)
                    _log.Information("Giveaways: closed {Closed}, drew {Drawn}", pass.Closed, pass.Drawn);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "The giveaway pass failed; it will run again");
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
