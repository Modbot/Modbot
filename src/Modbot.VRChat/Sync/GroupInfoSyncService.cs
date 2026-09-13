using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Scheduling;
using Serilog;

namespace Modbot.VRChat.Sync;

/// <summary>
/// Re-reads the group's metadata on a fixed, desynchronised schedule.
/// </summary>
/// <remarks>
/// <para>
/// Fixed rather than adaptive, unlike the audit log, because there is nothing to adapt to: a
/// group's name and roles change a few times a year and a poll that found no change is no
/// evidence at all about when the next one will come. The audit log's cadence earns its
/// complexity by turning arrival rate into request rate; here there is no arrival rate.
/// </para>
/// <para>
/// Its own hosted service, so that a cold stop on <c>groups.read</c> stops this and nothing else
/// — spec 4.3.1's scoped stop is only real if the producers are actually separate.
/// </para>
/// </remarks>
public sealed class GroupInfoSyncService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IModbotClock _clock;
    private readonly SyncDiagnostics _diagnostics;
    private readonly GroupInfoSyncOptions _options;
    private readonly DesyncedSchedule _schedule;
    private readonly IDelayScheduler _delays;
    private readonly ILogger _log;

    public GroupInfoSyncService(
        IServiceScopeFactory scopes,
        IModbotClock clock,
        SyncDiagnostics diagnostics,
        IMonotonicClock? elapsed = null,
        GroupInfoSyncOptions? options = null,
        IDelayScheduler? delays = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _scopes = scopes;
        _clock = clock;
        _diagnostics = diagnostics;
        _options = (options ?? new GroupInfoSyncOptions()).Clamped();

        // Spec 4.2.2's desynchronisation, measured on a monotonic source so an NTP step cannot
        // compress the schedule into a burst.
        _schedule = new DesyncedSchedule(
            _options.Interval, elapsed ?? new StopwatchMonotonicClock(), jitterFraction: _options.JitterFraction);

        _delays = delays ?? new RealDelayScheduler();
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var next = _schedule.NextDelay();

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await WaitAsync(next, stoppingToken).ConfigureAwait(false))
                return;

            var outcome = await RunOnceAsync(stoppingToken).ConfigureAwait(false);

            next = outcome switch
            {
                // Nothing will be sent until the stop lifts, so waiting the ordinary interval
                // would only produce refusals in the log (spec 4.3.1).
                SyncOutcome.RateLimited => _options.RateLimitedInterval,
                SyncOutcome.Failed => _options.RetryInterval,
                _ => _schedule.NextDelay(),
            };
        }
    }

    private async Task<SyncOutcome> RunOnceAsync(CancellationToken ct)
    {
        var started = _clock.UtcNow;

        try
        {
            using var scope = _scopes.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<GroupInfoSync>();

            var result = await sync.RunOnceAsync(ct).ConfigureAwait(false);
            Report(result.Outcome, started, Describe(result));

            return result.Outcome;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return SyncOutcome.Quiet;
        }
        catch (Exception e)
        {
            _log.Error(e, "Group-info sync failed. The recorded snapshot is unchanged");
            Report(SyncOutcome.Failed, started, e.Message);

            return SyncOutcome.Failed;
        }
    }

    private void Report(SyncOutcome outcome, DateTimeOffset started, string summary) =>
        _diagnostics.RecordGroupInfoRun(
            new SyncRunReport(outcome, started, _clock.UtcNow - started, summary));

    private static string Describe(GroupInfoRunResult result) => result.Outcome switch
    {
        SyncOutcome.Produced when result.Baseline => "recorded the group's baseline",
        SyncOutcome.Produced => $"changed: {string.Join(", ", result.Changed)}",
        SyncOutcome.Quiet => "unchanged",
        _ => result.Message ?? result.Outcome.ToString(),
    };

    private async Task<bool> WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await _delays.DelayAsync(delay, ct).ConfigureAwait(false);
            return !ct.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
