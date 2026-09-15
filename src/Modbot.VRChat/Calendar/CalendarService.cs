using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Modbot.VRChat.RateLimiting;
using Serilog;

namespace Modbot.VRChat.Calendar;

/// <summary>
/// Runs the calendar's VRChat side: moving events along, opening instances, and writing VRChat's
/// calendar (calendar design §9).
/// </summary>
/// <remarks>
/// <para>
/// Two loops rather than one. A calendar write waits up to a minute for its turn in the gate, and
/// an instance due to open must not wait behind it; moving events along asks VRChat nothing and
/// must not wait behind either. So the scheduler and the opener share one loop, in that order, and
/// the calendar writes have their own.
/// </para>
/// <para>
/// Fifteen seconds between passes. Time here comes from <c>IModbotClock</c> inside each pass; the
/// wait between passes is only a pause.
/// </para>
/// </remarks>
public sealed class CalendarService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopes;
    private readonly IDelayScheduler _delays;
    private readonly ILogger _log;

    public CalendarService(IServiceScopeFactory scopes, IDelayScheduler? delays = null, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        _scopes = scopes;
        _delays = delays ?? new RealDelayScheduler();
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(
            LoopAsync("calendar schedule", RunScheduleAsync, stoppingToken),
            LoopAsync("VRChat calendar publish", RunPublishAsync, stoppingToken));

    private static async Task RunScheduleAsync(IServiceProvider scope, CancellationToken ct)
    {
        await scope.GetRequiredService<CalendarScheduler>().RunOnceAsync(ct).ConfigureAwait(false);
        await scope.GetRequiredService<CalendarOpener>().RunOnceAsync(ct).ConfigureAwait(false);
    }

    private static Task RunPublishAsync(IServiceProvider scope, CancellationToken ct) =>
        scope.GetRequiredService<CalendarVRChatPublisher>().RunOnceAsync(ct);

    private async Task LoopAsync(
        string what, Func<IServiceProvider, CancellationToken, Task> pass, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _delays.DelayAsync(Interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                using var scope = _scopes.CreateScope();
                await pass(scope.ServiceProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A pass that throws must not take the host down or stop the calendar: the next
                // pass gets a fresh scope and a fresh context.
                _log.Error(ex, "The {What} pass failed; it will run again", what);
            }
        }
    }
}
