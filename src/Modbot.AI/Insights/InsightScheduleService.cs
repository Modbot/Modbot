using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modbot.AI.Insights;

/// <summary>Runs <see cref="InsightScheduler"/> once a minute (AI insights design §3).</summary>
/// <remarks>
/// A minute because the schedule is to the hour, and a pass with nothing due is two small reads.
/// A failure is logged and never fatal: an insight is a summary of data that is still there.
/// </remarks>
public sealed class InsightScheduleService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    public InsightScheduleService(
        IServiceScopeFactory scopes,
        ILogger<InsightScheduleService>? log = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        _scopes = scopes;
        _log = log ?? NullLogger<InsightScheduleService>.Instance;
        _delay = delay ?? Task.Delay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var written = await scope.ServiceProvider.GetRequiredService<InsightScheduler>().RunDueAsync(stoppingToken);

                if (written > 0)
                    _log.LogInformation("Wrote {Count} scheduled insight(s).", written);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _log.LogError(e, "The scheduled insight pass failed; trying again in a minute.");
            }

            try
            {
                await _delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
