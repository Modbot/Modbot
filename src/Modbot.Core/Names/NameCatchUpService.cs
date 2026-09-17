using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Data;
using Modbot.Core.Logging;
using Serilog;

namespace Modbot.Core.Names;

/// <summary>Runs the name catch-up once after startup, a batch at a time, and then stops.</summary>
/// <remarks>
/// Its first pass waits a minute, so a Modbot that is crash-looping spends its short life starting
/// up rather than rewriting rows, and so the migration that added the columns is long finished.
/// Batches are spaced out: a 150,000-row table is a few hundred small saves rather than one long
/// transaction, and the syncs writing the same tables are not held up. Once nothing is left the
/// version is recorded and the service exits; the next change to the rules ships as a new build,
/// which starts it again.
/// </remarks>
public sealed class NameCatchUpService : BackgroundService
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BetweenBatches = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger _log = Log.Logger.ForContext(LogArea.Name, LogArea.Sync);

    public NameCatchUpService(IServiceScopeFactory scopes)
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

            if (await RunAsync(stoppingToken).ConfigureAwait(false))
                return;

            wait = RetryDelay;
        }
    }

    /// <summary>True when the catch-up is complete; false when it failed and should be tried again.</summary>
    private async Task<bool> RunAsync(CancellationToken ct)
    {
        try
        {
            await using (var scope = _scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
                await NameCatchUp.RestartIfOutOfDateAsync(db, _log, ct).ConfigureAwait(false);
            }

            var filled = 0;
            while (true)
            {
                int batch;
                await using (var scope = _scopes.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
                    batch = await NameCatchUp.FillBatchAsync(db, ct).ConfigureAwait(false);
                }

                if (batch == 0)
                    break;

                filled += batch;
                await Task.Delay(BetweenBatches, ct).ConfigureAwait(false);
            }

            await using (var scope = _scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
                await NameCatchUp.MarkCompleteAsync(db, ct).ConfigureAwait(false);
            }

            if (filled > 0)
                _log.Information("Made searchable names for {Count} stored people", filled);

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception e)
        {
            _log.Warning(e, "Making searchable names failed; trying again later");
            return false;
        }
    }
}
