using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modbot.Analytics.Storage;

/// <summary>
/// Makes sure every day Modbot runs gets one storage row.
/// </summary>
/// <remarks>
/// <para>
/// Wakes hourly but measures at most once a day: each run only checks whether today already has a
/// row, which is a key lookup, and measures only when it does not. A plain once-a-day timer would
/// drift a few seconds later each day and eventually step over a midnight, skipping a day.
/// </para>
/// <para>
/// A failure is logged and retried on the next hour, never fatal. A missing day is a gap in a
/// chart; taking Modbot down over it would be a gap in moderation.
/// </para>
/// </remarks>
public sealed class StorageHistoryService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<StorageHistoryService> _log;

    public StorageHistoryService(IServiceScopeFactory scopes, ILogger<StorageHistoryService> log)
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
            await RunOnceAsync(stoppingToken);

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var history = scope.ServiceProvider.GetRequiredService<StorageHistory>();

            if (!await history.IsTodayRecordedAsync(ct))
                await history.RecordTodayAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down; the next start checks again.
        }
        catch (Exception e)
        {
            _log.LogError(e, "Recording today's storage size failed. It will be tried again in an hour.");
        }
    }
}
