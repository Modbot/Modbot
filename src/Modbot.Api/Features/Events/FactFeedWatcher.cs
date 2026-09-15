using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Serilog;

namespace Modbot.Api.Features.Events;

/// <summary>
/// Pulses <see cref="FactSignal"/> when the newest committed fact id moves (API keys design §5.5).
/// </summary>
/// <remarks>
/// <para>
/// The writer pulses as it inserts, which can be before the transaction holding the insert commits;
/// a reader woken then sees nothing. This one small read, for the whole process rather than per
/// connection, catches the commit half a second later. It also catches facts written by anything
/// that does not go through a writer holding the signal.
/// </para>
/// </remarks>
public sealed class FactFeedWatcher : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private readonly IServiceScopeFactory _scopes;
    private readonly FactSignal _signal;

    public FactFeedWatcher(IServiceScopeFactory scopes, FactSignal signal)
    {
        _scopes = scopes;
        _signal = signal;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long? seen = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
                var newest = await db.Events.AsNoTracking().MaxAsync(e => (long?)e.Id, stoppingToken) ?? 0;

                if (seen is not null && newest != seen)
                    _signal.Pulse();

                seen = newest;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                Log.Debug(e, "The fact feed watcher could not read the newest fact id");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
