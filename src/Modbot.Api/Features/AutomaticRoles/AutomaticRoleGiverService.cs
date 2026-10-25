using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Api.Features.AutomaticRoles;

/// <summary>
/// Runs <see cref="AutomaticRoleGiver"/> when a companion reports something (<see cref="AutomaticRoleSignal"/>)
/// and at least every <see cref="Interval"/>, one scope per pass.
/// </summary>
public sealed class AutomaticRoleGiverService : BackgroundService
{
    /// <summary>The longest the job goes without looking, however quiet the companions are.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly IModbotClock _clock;
    private readonly AutomaticRoleSignal _signal;

    public AutomaticRoleGiverService(IServiceScopeFactory scopes, IModbotClock clock, AutomaticRoleSignal signal)
    {
        _scopes = scopes;
        _clock = clock;
        _signal = signal;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startedAt = _clock.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AutomaticRoleGiver>().RunOnceAsync(startedAt, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                Log.Error(e, "An automatic roles pass failed; trying again shortly");
            }

            try
            {
                await _signal.WaitAsync(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
