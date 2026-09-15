using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Data;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.AI.Calls;

/// <summary>
/// Deletes call log rows older than the operator's keep-for setting, once a day.
/// </summary>
/// <remarks>
/// Its own loop rather than a step of the fact pruner, because the call log is not a fact: it is
/// Modbot's record of what it asked a provider, and the operator sets how long it is kept without
/// touching how long moderation history is kept.
/// </remarks>
public sealed class AiCallLogPruneService : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(24);

    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger _log = Log.Logger.ForContext(LogArea.Name, LogArea.Moderation);

    public AiCallLogPruneService(IServiceScopeFactory scopes)
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

            wait = Every;

            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
                var clock = scope.ServiceProvider.GetRequiredService<IModbotClock>();

                var deleted = await AiCallLog.PruneAsync(db, clock.UtcNow, stoppingToken).ConfigureAwait(false);

                if (deleted > 0)
                    _log.Information("Deleted {Count} AI call log row(s) past the keep-for setting", deleted);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                _log.Warning(e, "Deleting old AI call log rows failed");
            }
        }
    }
}
