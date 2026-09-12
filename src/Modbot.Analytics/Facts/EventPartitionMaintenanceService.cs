using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modbot.Analytics.Facts;

/// <summary>
/// Keeps <c>modbot_event</c>'s partitions ahead of the clock for as long as Modbot is running.
/// </summary>
/// <remarks>
/// <para>
/// It runs once at startup and then daily. Startup matters most: a deployment that has been down
/// across a month boundary has no partition for today, and without this its very first fact --
/// and every one after it -- fails to insert.
/// </para>
/// <para>
/// Daily rather than monthly because a job that only fires on a date has no second chance if the
/// process happens to be restarting that minute, and the work is a catalogue lookup that finds
/// nothing on all but one day in thirty.
/// </para>
/// </remarks>
public sealed class EventPartitionMaintenanceService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>
    /// How long to wait before trying again when maintenance fails. Short, because until it
    /// succeeds no fact can be written at all.
    /// </summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EventPartitionMaintenanceService> _log;

    public EventPartitionMaintenanceService(
        IServiceScopeFactory scopes,
        ILogger<EventPartitionMaintenanceService> log)
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
            var maintainer = scope.ServiceProvider.GetRequiredService<EventPartitionMaintainer>();

            var created = await maintainer.EnsureAsync(ct);

            if (created.Count > 0)
                _log.LogInformation("Created fact-log partitions {Partitions}.", created);

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception e)
        {
            // Loud, and not fatal to the process: the operator needs the rest of Modbot up to fix
            // whatever is wrong with the database, and a crash loop would take the UI with it.
            _log.LogError(
                e,
                "Could not create fact-log partitions. Ingest will fail until this succeeds.");

            return false;
        }
    }
}
