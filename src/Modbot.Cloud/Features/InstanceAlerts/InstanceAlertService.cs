using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modbot.Cloud.Features.InstanceAlerts;

/// <summary>Checks the watched deployments every few minutes.</summary>
public sealed class InstanceAlertService(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<InstanceAlertService> log) : BackgroundService
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wait = FirstRun;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(wait, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            wait = await RunOnceAsync(stoppingToken) ? InstanceAlertChecker.CheckEvery : RetryDelay;
        }
    }

    internal async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var checker = scope.ServiceProvider.GetRequiredService<InstanceAlertChecker>();

            var run = await checker.RunOnceAsync(ct);

            if (run.Problems.Count > 0 || run.Recoveries.Count > 0)
            {
                log.LogInformation(
                    "Instance alerts: {Problems} went quiet or wrong, {Recoveries} came back, {Sent} email(s) sent",
                    run.Problems,
                    run.Recoveries,
                    run.Sent);
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Checking the watched Modbot deployments failed");
            return false;
        }
    }
}
