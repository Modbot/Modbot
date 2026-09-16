using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Data;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Core.Logging.Store;

/// <summary>Deletes log lines past the retention window, once a day.</summary>
/// <remarks>
/// Its first pass waits five minutes, so a Modbot that is crash-looping spends its short life
/// starting up rather than deleting rows, and so the startup lines are written before anything runs
/// over the table they went into.
/// </remarks>
public sealed class LogRetentionService : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(24);
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger _log = Log.Logger.ForContext(LogArea.Name, LogArea.Analytics);

    public LogRetentionService(IServiceScopeFactory scopes)
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

            wait = await RunOnceAsync(stoppingToken).ConfigureAwait(false) ? Every : RetryDelay;
        }
    }

    private async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var clock = scope.ServiceProvider.GetRequiredService<IModbotClock>();

            var deleted = await LogStore.PruneAsync(db, clock.UtcNow, ct).ConfigureAwait(false);

            // Logged because it is irreversible: when somebody asks where last spring's log went,
            // this line is the answer.
            if (deleted > 0)
                _log.Information("Deleted {Count} stored log line(s) past the keep-for setting", deleted);

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception e)
        {
            _log.Warning(e, "Deleting old stored log lines failed");
            return false;
        }
    }
}
