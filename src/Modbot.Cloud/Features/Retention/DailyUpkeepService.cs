namespace Modbot.Cloud.Features.Retention;

/// <summary>
/// Once a day: delete what retention says to.
/// </summary>
/// <remarks>
/// <para>
/// A failure is logged and retried in five minutes. It never stops the process: an admin needs the
/// rest of Cloud up to see what is wrong.
/// </para>
/// <para>
/// The wait between runs is a <see cref="TimeProvider"/> timer, so the system clock is never read
/// directly.
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

            var result = await scope.ServiceProvider.GetRequiredService<RetentionPruner>().RunAsync(ct);
            if (result.EventsRemoved > 0 || result.ClocksRemoved > 0)
                log.LogInformation("Retention removed {Events} events and {Clocks} clock rows", result.EventsRemoved, result.ClocksRemoved);

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception e)
        {
            log.LogError(e, "Daily retention failed; trying again in five minutes.");
            return false;
        }
    }
}
