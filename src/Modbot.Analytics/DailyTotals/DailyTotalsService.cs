using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modbot.Analytics.DailyTotals;

/// <summary>
/// Keeps the daily totals caught up with the fact log while Modbot is running.
/// </summary>
/// <remarks>
/// <para>
/// Frequent rather than nightly: a daily total that is a day behind makes every chart in the UI a day
/// behind, and an incremental run over a quarter-hour of facts is a handful of small queries. The
/// work is idempotent, so a run that overlaps the previous one loses nothing -- the advisory lock
/// in <see cref="DailyTotalsJob"/> makes them queue.
/// </para>
/// <para>
/// A failure here is never fatal. Daily totals are derived data; whatever went wrong, the facts are
/// still being written, and the next run -- or a rebuild -- reconstructs everything missed.
/// </para>
/// </remarks>
public sealed class DailyTotalsService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DailyTotalsService> _log;

    public DailyTotalsService(IServiceScopeFactory scopes, ILogger<DailyTotalsService> log)
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
            var job = scope.ServiceProvider.GetRequiredService<DailyTotalsJob>();

            var result = await job.RunIncrementalAsync(ct);

            if (result.RowsWritten > 0)
                _log.LogDebug(
                    "Rolled up {Rows} rows for {From}..{To}.",
                    result.RowsWritten,
                    result.From,
                    result.To);

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception e)
        {
            _log.LogError(e, "Daily totals run failed. Charts will lag until a run succeeds.");
            return false;
        }
    }
}
