using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Serilog;

namespace Modbot.Api.Features.Health.Alerts;

/// <summary>Runs the health checks every few minutes and emails what changed.</summary>
/// <remarks>
/// Its first pass waits two minutes. A Modbot that has just started has not finished a sync pass
/// yet, and alerting that sync has stopped thirty seconds after a deploy would be wrong every time.
/// </remarks>
public sealed class HealthAlertService : BackgroundService
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger _log = Log.Logger.ForContext(LogArea.Name, LogArea.Setup);

    public HealthAlertService(IServiceScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        _scopes = scopes;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wait = FirstRun;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(wait, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            wait = await RunOnceAsync(stoppingToken).ConfigureAwait(false)
                ? HealthAlertChecker.CheckEvery
                : RetryDelay;
        }
    }

    private async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var checker = scope.ServiceProvider.GetRequiredService<HealthAlertChecker>();

            var run = await checker.RunOnceAsync(ct).ConfigureAwait(false);

            if (run.Problems.Count > 0 || run.Recoveries.Count > 0)
            {
                _log.Information(
                    "Health alerts: {Problems} went wrong, {Recoveries} came back, {Sent} email(s) sent",
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
            _log.Warning(e, "Checking Modbot's own health failed");
            return false;
        }
    }
}
