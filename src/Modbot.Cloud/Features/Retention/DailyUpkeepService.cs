using Modbot.Cloud.Engine;

namespace Modbot.Cloud.Features.Retention;

/// <summary>
/// Once a day: make next month's partitions, then drop what retention says to.
/// </summary>
/// <remarks>
/// <para>
/// Partitions come first and are also made before the app starts serving (see
/// <c>CloudApp.PrepareAsync</c>), so a Cloud that was down across a month boundary ingests from its
/// first request. This keeps them ahead for as long as it runs.
/// </para>
/// <para>
/// A failure is logged and retried in five minutes. It never stops the process: an admin needs the
/// rest of Cloud up to see what is wrong.
/// </para>
/// <para>
/// The wait between runs is a <see cref="TimeProvider"/> timer, so tests can drive it and the
/// system clock is never read directly.
/// </para>
/// </remarks>
public sealed class DailyUpkeepService(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<DailyUpkeepService> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var succeeded = await RunOnceAsync(stoppingToken);

            try
            {
                await Task.Delay(succeeded ? Interval : RetryDelay, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();

            var created = await scope.ServiceProvider.GetRequiredService<PartitionMaintainer>().EnsureAsync(ct);
            if (created.Count > 0)
                log.LogInformation("Created partitions {Partitions}", created);

            var result = await scope.ServiceProvider.GetRequiredService<RetentionPruner>().RunAsync(ct);
            if (result.DroppedPartitions.Count > 0 || result.LogFilesRemoved > 0 || result.ClocksRemoved > 0)
            {
                log.LogInformation(
                    "Retention dropped {Partitions}, and removed {Files} log file rows and {Clocks} clock rows",
                    result.DroppedPartitions, result.LogFilesRemoved, result.ClocksRemoved);
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception e)
        {
            log.LogError(e, "Daily upkeep failed; trying again in five minutes. Ingest fails once a month has no partition.");
            return false;
        }
    }
}
