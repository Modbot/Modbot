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
/// Runs the profile sync: one pass per tick on the users lane, faster when there is work and
/// slower when there is not.
/// </summary>
/// <remarks>
/// <para>
/// Its own hosted service, like the other two producers, so that a cold stop on
/// <c>users.read</c> stops profile fetches and nothing else -- and so that a cold stop on the
/// group endpoints leaves this lane running (spec 4.3.1). The lane is the reason the service is
/// separate; the queue is the reason there is only one of it.
/// </para>
/// <para>
/// Two schedules, both desynchronised (spec 4.2.2): the working one at the pacing interval, and
/// an idle one for when nobody is waiting. Each idle pass still discovers new people, so a fresh
/// join on a quiet deployment is noticed within the idle interval and refreshed on the next tick.
/// </para>
/// <para>
/// Housekeeping -- re-reading the discovery overlap, topping the queue up from the database and
/// re-counting the table -- runs once a minute and whenever the queue has emptied. The rest of
/// the time a pass is a cursor read and one fetch.
/// </para>
/// </remarks>
public sealed class UserProfileSyncService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IModbotClock _clock;
    private readonly SyncDiagnostics _diagnostics;
    private readonly UserRefreshQueue _queue;
    private readonly IMonotonicClock _elapsed;
    private readonly ISyncPacingSource? _pacing;
    private readonly IDelayScheduler _delays;
    private readonly ILogger _log;

    private UserProfileSyncOptions _options;
    private DesyncedSchedule _working;
    private DesyncedSchedule _idle;
    private DateTimeOffset? _lastHousekeeping;

    public UserProfileSyncService(
        IServiceScopeFactory scopes,
        IModbotClock clock,
        SyncDiagnostics diagnostics,
        UserRefreshQueue queue,
        IMonotonicClock? elapsed = null,
        UserProfileSyncOptions? options = null,
        ISyncPacingSource? pacing = null,
        IDelayScheduler? delays = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(queue);

        _scopes = scopes;
        _clock = clock;
        _diagnostics = diagnostics;
        _queue = queue;
        _elapsed = elapsed ?? new StopwatchMonotonicClock();
        _pacing = pacing;
        _options = (options ?? new UserProfileSyncOptions()).Clamped();

        _working = new DesyncedSchedule(_options.Interval, _elapsed, jitterFraction: _options.JitterFraction);
        _idle = new DesyncedSchedule(_options.IdleInterval, _elapsed, jitterFraction: _options.JitterFraction);

        _delays = delays ?? new RealDelayScheduler();
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>The options in force, which the operator may have changed since startup.</summary>
    internal UserProfileSyncOptions Options => _options;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var next = _idle.NextDelay();

        while (!stoppingToken.IsCancellationRequested)
        {
            if (await RefreshPacingAsync(stoppingToken).ConfigureAwait(false))
                next = _idle.NextDelay();

            if (!await WaitAsync(next, stoppingToken).ConfigureAwait(false))
                return;

            var now = _clock.UtcNow;
            var housekeeping = _lastHousekeeping is not { } last
                || now - last >= _options.TopUpInterval
                || _queue.Count == 0;

            var result = await RunOnceAsync(housekeeping, stoppingToken).ConfigureAwait(false);

            if (housekeeping && result.Outcome is not SyncOutcome.Failed)
                _lastHousekeeping = now;

            next = result.Outcome switch
            {
                // Nothing will be sent until the stop lifts, so waiting the ordinary interval
                // would only produce refusals in the log (spec 4.3.1).
                SyncOutcome.RateLimited => _options.RateLimitedInterval,
                SyncOutcome.Failed => _options.RetryInterval,
                SyncOutcome.NotConfigured => _options.RetryInterval,
                _ when result.Refreshed => _working.NextDelay(),
                _ => _idle.NextDelay(),
            };
        }
    }

    /// <summary>Adopts options the operator has changed. Returns true when a schedule was rebuilt.</summary>
    private async Task<bool> RefreshPacingAsync(CancellationToken ct)
    {
        if (_pacing is null)
            return false;

        try
        {
            var pacing = await _pacing.CurrentAsync(ct).ConfigureAwait(false);
            var options = pacing.UserProfile;

            if (options == _options)
                return false;

            var rebuild = options.Interval != _options.Interval
                || options.IdleInterval != _options.IdleInterval
                || Math.Abs(options.JitterFraction - _options.JitterFraction) > double.Epsilon;

            _options = options;

            if (!rebuild)
                return false;

            _working = new DesyncedSchedule(_options.Interval, _elapsed, jitterFraction: _options.JitterFraction);
            _idle = new DesyncedSchedule(_options.IdleInterval, _elapsed, jitterFraction: _options.JitterFraction);

            _log.Debug("Profile sync interval is now {Interval}", _options.Interval);

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<UserProfileRunResult> RunOnceAsync(bool housekeeping, CancellationToken ct)
    {
        var started = _clock.UtcNow;

        try
        {
            using var scope = _scopes.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<UserProfileSync>();

            var result = await sync.RunOnceAsync(housekeeping, ct).ConfigureAwait(false);
            Report(result, started);

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new UserProfileRunResult(SyncOutcome.Quiet, Message: "shutting down");
        }
        catch (Exception e)
        {
            _log.Error(e, "Profile sync failed. The cursor and the queue are unchanged; the next pass tries again");

            var result = new UserProfileRunResult(SyncOutcome.Failed, Message: e.Message);
            Report(result, started);

            return result;
        }
    }

    private void Report(UserProfileRunResult result, DateTimeOffset started) =>
        _diagnostics.RecordUserProfileRun(
            new SyncRunReport(result.Outcome, started, _clock.UtcNow - started, Describe(result)),
            result.Refreshed);

    private static string Describe(UserProfileRunResult result)
    {
        var who = result.UserId is null ? string.Empty : $" {result.UserId} ({Reason(result.Reason)})";

        var what = result switch
        {
            { NotFound: true } => "not found on VRChat",
            { FirstSeen: true } => "profile recorded for the first time",
            { Changed.Count: > 0 } => $"changed: {string.Join(", ", result.Changed)}",
            { Refreshed: true, Message: { } message } => message,
            { Refreshed: true } => "unchanged",
            { Outcome: SyncOutcome.Quiet } => result.Message ?? "nobody waiting",
            _ => result.Message ?? result.Outcome.ToString(),
        };

        var flag = result.AgeVerifiedObserved ? "; seen as 18+ verified" : string.Empty;
        var found = result.Discovered > 0 ? $"; {result.Discovered} people seen in new facts" : string.Empty;

        return $"{what}{who}{flag}{found}";
    }

    private static string Reason(RefreshReason? reason) => reason switch
    {
        RefreshReason.SeenInInstance => "seen in an instance",
        RefreshReason.OpenedInModbot => "opened in Modbot",
        RefreshReason.SeenInFactLog => "seen in the fact log",
        RefreshReason.ProfileIsOld => "profile is old",
        RefreshReason.NeverRefreshed => "never refreshed",
        _ => "no reason",
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
