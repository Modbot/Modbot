using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modbot.Analytics.Retention;

/// <summary>
/// Runs retention pruning once a day.
/// </summary>
/// <remarks>
/// <para>
/// Daily and not hourly: the unit of work is a whole month's partition, so there is nothing to do
/// on all but a few days of the month, and the one day it does act it takes an ACCESS EXCLUSIVE
/// lock on the fact log for as long as the copy takes. Once a day is as often as that is worth
/// risking.
/// </para>
/// <para>
/// A failure is logged and retried, never fatal. Failing to prune costs disk; taking Modbot down
/// over it costs moderation.
/// </para>
/// </remarks>
public sealed class RetentionService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RetentionService> _log;

    public RetentionService(IServiceScopeFactory scopes, ILogger<RetentionService> log)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(log);

        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var succeeded = await RunOnceAsync(stoppingToken);

            try
            {
                await Task.Delay(succeeded ? Interval : RetryDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var pruner = scope.ServiceProvider.GetRequiredService<RetentionPruner>();

            var result = await pruner.PruneAsync(ct);

            // Logged at information because it is irreversible. When someone asks in six months
            // where a partition went, this line is the answer.
            if (result.Dropped.Count > 0 || result.MovedOut.Count > 0 || result.MessagesDropped.Count > 0)
                _log.LogInformation(
                    "Retention dropped {Dropped}, rebuilt {MovedOut} without expired facts, and dropped Discord message months {MessagesDropped}.",
                    result.Dropped,
                    result.MovedOut,
                    result.MessagesDropped);

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception e)
        {
            _log.LogError(e, "Retention pruning failed. The fact log will keep growing until it succeeds.");
            return false;
        }
    }
}
