using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.VRChat.Pacing;
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
    private readonly IMonotonicClock _elapsed;
    private readonly ISyncPacingSource? _pacing;
    private readonly IDelayScheduler _delays;
    private readonly ILogger _log;

    private GroupInfoSyncOptions _options;
    private DesyncedSchedule _schedule;

    public GroupInfoSyncService(
        IServiceScopeFactory scopes,
        IModbotClock clock,
        SyncDiagnostics diagnostics,
        IMonotonicClock? elapsed = null,
        GroupInfoSyncOptions? options = null,
        ISyncPacingSource? pacing = null,
        IDelayScheduler? delays = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _scopes = scopes;
        _clock = clock;
        _diagnostics = diagnostics;
        _elapsed = elapsed ?? new StopwatchMonotonicClock();
        _pacing = pacing;
        _options = (options ?? new GroupInfoSyncOptions()).Clamped();

        // Spec 4.2.2's desynchronisation, measured on a monotonic source so an NTP step cannot
        // compress the schedule into a burst.
        _schedule = new DesyncedSchedule(
            _options.Interval, _elapsed, jitterFraction: _options.JitterFraction);

        _delays = delays ?? new RealDelayScheduler();
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>The interval in force, which the operator may have changed since startup.</summary>
    internal GroupInfoSyncOptions Options => _options;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var next = _schedule.NextDelay();

        while (!stoppingToken.IsCancellationRequested)
        {
            // Before the wait, so a changed interval applies to the next poll. This producer runs
            // every five minutes by default, so it is also the one where a change that needed a
            // restart would be least obvious and most annoying.
            if (await RefreshPacingAsync(stoppingToken).ConfigureAwait(false))
                next = _schedule.NextDelay();

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

    /// <summary>
    /// Adopts an interval the operator has changed. Returns true when the schedule was rebuilt.
    /// </summary>
    /// <remarks>
    /// The schedule is replaced rather than adjusted, because <see cref="DesyncedSchedule"/>
    /// counts ticks from a fixed interval and reinterpreting its history under a new one would
    /// produce either a burst or a long silence. A new schedule draws a fresh phase, which is
    /// what spec 4.2.2 wants anyway — and only when the interval actually changed, so an ordinary
    /// tick does not re-randomise itself into never running.
    /// </remarks>
    private async Task<bool> RefreshPacingAsync(CancellationToken ct)
    {
        if (_pacing is null)
            return false;

        try
        {
            var pacing = await _pacing.CurrentAsync(ct).ConfigureAwait(false);
            var options = pacing.GroupInfo;

            if (options == _options)
                return false;

            var rebuild = options.Interval != _options.Interval
                || Math.Abs(options.JitterFraction - _options.JitterFraction) > double.Epsilon;

            _options = options;

            if (!rebuild)
                return false;

            _schedule = new DesyncedSchedule(
                _options.Interval, _elapsed, jitterFraction: _options.JitterFraction);

            _log.Debug("Group-info interval is now {Interval}", _options.Interval);

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
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
