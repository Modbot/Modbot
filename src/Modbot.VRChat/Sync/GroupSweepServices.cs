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
/// The loop a sweep runs in: a page, a short wait, another page, until the sweep is over; then a
/// long rest; then again.
/// </summary>
/// <remarks>
/// <para>
/// One hosted service per list, on top of one shared loop, because spec 4.3.1's cold stop is
/// scoped to a bucket: a 429 on <c>groups.members</c> must stop the member sweep and nothing
/// else, and a service that ran both sweeps would couple them. The loop is shared because the
/// two lists are paced identically and differ only in numbers.
/// </para>
/// <para>
/// Two waits, both desynchronised (spec 4.2.2). Page ticks come from a <see cref="DesyncedSchedule"/>
/// at the page delay, so the first page after a process start falls at a random phase rather
/// than the instant the process is up, and a fleet restarting together re-spreads. The rest
/// between sweeps is a plain jittered wait: the length of a rest is a poll rate, not a comb to
/// stay aligned to.
/// </para>
/// <para>
/// The sync class checks the rest too, against the stored completion time, so a process
/// restarting in a loop cannot begin a new sweep on every boot. This loop's rest is what happens
/// in the ordinary case; the stored one is what happens in the bad case.
/// </para>
/// </remarks>
public abstract class GroupSweepService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IModbotClock _clock;
    private readonly IMonotonicClock _elapsed;
    private readonly ISyncPacingSource? _pacing;
    private readonly IDelayScheduler _delays;
    private readonly Random _random = Random.Shared;

    private DesyncedSchedule _pages;

    protected GroupSweepService(
        IServiceScopeFactory scopes,
        IModbotClock clock,
        SyncDiagnostics diagnostics,
        SweepPace pace,
        IMonotonicClock? elapsed = null,
        ISyncPacingSource? pacing = null,
        IDelayScheduler? delays = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(pace);

        _scopes = scopes;
        _clock = clock;
        Diagnostics = diagnostics;
        Pace = pace;
        _elapsed = elapsed ?? new StopwatchMonotonicClock();
        _pacing = pacing;
        _pages = new DesyncedSchedule(pace.PageDelay, _elapsed, jitterFraction: pace.JitterFraction);
        _delays = delays ?? new RealDelayScheduler();
        Log = (log ?? Serilog.Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    protected SyncDiagnostics Diagnostics { get; }

    protected ILogger Log { get; }

    /// <summary>The pace in force, which the operator may have changed since startup.</summary>
    internal SweepPace Pace { get; private set; }

    /// <summary>A word for the log and the health screen: "member" or "ban".</summary>
    protected abstract string Name { get; }

    /// <summary>The pace the current pacing snapshot holds for this sweep.</summary>
    protected abstract SweepPace PaceFrom(SyncPacing pacing);

    /// <summary>One pass -- one page, or the end of a sweep -- in its own scope.</summary>
    protected abstract Task<SweepRunResult> SweepAsync(IServiceProvider services, CancellationToken ct);

    protected abstract void Report(SyncRunReport report, SweepRunResult result);

    protected abstract void Announce(string phase, DateTimeOffset nextPassAt);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var next = _pages.NextDelay();
        Announce("sweeping", _clock.UtcNow + next);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (await RefreshPacingAsync(stoppingToken).ConfigureAwait(false))
                next = _pages.NextDelay();

            if (!await WaitAsync(next, stoppingToken).ConfigureAwait(false))
                return;

            var result = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            var now = _clock.UtcNow;

            string phase;

            switch (result)
            {
                case { Outcome: SyncOutcome.RateLimited }:
                    // Nothing will be sent until the stop lifts; a tighter loop would only
                    // produce refusals in the log (spec 4.3.1).
                    next = Pace.RateLimitedInterval;
                    phase = "cold-stopped";
                    break;

                case { Outcome: SyncOutcome.Failed }:
                    next = Pace.RetryInterval;
                    phase = "retrying";
                    break;

                case { Outcome: SyncOutcome.NotConfigured }:
                    next = Pace.RetryInterval;
                    phase = "idle";
                    break;

                case { RestUntil: { } until }:
                    next = Jittered(until > now ? until - now : Pace.PageDelay);
                    phase = "resting";
                    break;

                case { SweepComplete: true }:
                    next = Jittered(Pace.RestBetweenSweeps);
                    phase = "resting";
                    break;

                default:
                    next = _pages.NextDelay();
                    phase = "sweeping";
                    break;
            }

            Announce(phase, now + next);
        }
    }

    /// <summary>
    /// Adopts a pace the operator has changed. Returns true when the page schedule was rebuilt.
    /// </summary>
    /// <remarks>
    /// Rebuilt rather than adjusted, for the reason <c>GroupInfoSyncService</c> gives: the
    /// schedule counts ticks from a fixed interval, and reinterpreting its history under a new
    /// one produces either a burst or a long silence.
    /// </remarks>
    private async Task<bool> RefreshPacingAsync(CancellationToken ct)
    {
        if (_pacing is null)
            return false;

        try
        {
            var pace = PaceFrom(await _pacing.CurrentAsync(ct).ConfigureAwait(false));

            if (pace == Pace)
                return false;

            var rebuild = pace.PageDelay != Pace.PageDelay
                || Math.Abs(pace.JitterFraction - Pace.JitterFraction) > double.Epsilon;

            Pace = pace;

            if (!rebuild)
                return false;

            _pages = new DesyncedSchedule(Pace.PageDelay, _elapsed, jitterFraction: Pace.JitterFraction);
            Log.Debug("{Name} sweep page delay is now {Delay}", Name, Pace.PageDelay);

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<SweepRunResult> RunOnceAsync(CancellationToken ct)
    {
        var started = _clock.UtcNow;

        try
        {
            using var scope = _scopes.CreateScope();

            var result = await SweepAsync(scope.ServiceProvider, ct).ConfigureAwait(false);
            Report(new SyncRunReport(result.Outcome, started, _clock.UtcNow - started, Describe(result)), result);

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new SweepRunResult(SyncOutcome.Quiet, Message: "shutting down");
        }
        catch (Exception e)
        {
            // The cursor did not move -- the save is the last thing a pass does -- so nothing is
            // lost by carrying on. A sweep that died on one bad page and never came back would
            // leave the Members page frozen at whatever it last showed, with nothing to say so.
            Log.Error(e, "{Name} sweep failed. The cursor was not advanced; the next pass re-reads the page", Name);

            var result = new SweepRunResult(SyncOutcome.Failed, Message: e.Message);
            Report(new SyncRunReport(SyncOutcome.Failed, started, _clock.UtcNow - started, e.Message), result);

            return result;
        }
    }

    private static string Describe(SweepRunResult result) => result switch
    {
        { RestUntil: not null } => "resting between sweeps",
        { SweepComplete: true, FirstSweep: true } => "first sweep finished",
        { SweepComplete: true } =>
            $"sweep finished: {result.MarkedGone} no longer listed, {result.FactsWritten} recorded",
        { Outcome: SyncOutcome.Produced or SyncOutcome.Quiet } =>
            $"page read: {result.RowsRead} listed, {result.RowsChanged} changed",
        _ => result.Message ?? result.Outcome.ToString(),
    };

    /// <summary>A rest with spec 4.2.2's jitter on it, either way -- a rest is not a rate cap.</summary>
    private TimeSpan Jittered(TimeSpan interval)
    {
        if (Pace.JitterFraction <= 0)
            return interval;

        var swing = ((_random.NextDouble() * 2) - 1) * Pace.JitterFraction;
        return interval + (interval * swing);
    }

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

/// <summary>The numbers the shared loop needs from either options record.</summary>
public sealed record SweepPace(
    TimeSpan PageDelay,
    TimeSpan RestBetweenSweeps,
    TimeSpan RetryInterval,
    TimeSpan RateLimitedInterval,
    double JitterFraction)
{
    public static SweepPace Of(GroupMemberSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var o = options.Clamped();
        return new SweepPace(o.PageDelay, o.RestBetweenSweeps, o.RetryInterval, o.RateLimitedInterval, o.JitterFraction);
    }

    public static SweepPace Of(GroupBanSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var o = options.Clamped();
        return new SweepPace(o.PageDelay, o.RestBetweenSweeps, o.RetryInterval, o.RateLimitedInterval, o.JitterFraction);
    }
}

/// <summary>Runs the member sweep. Its own service so a cold stop on <c>groups.members</c> stops it and nothing else.</summary>
public sealed class GroupMemberSyncService : GroupSweepService
{
    public GroupMemberSyncService(
        IServiceScopeFactory scopes,
        IModbotClock clock,
        SyncDiagnostics diagnostics,
        IMonotonicClock? elapsed = null,
        GroupMemberSyncOptions? options = null,
        ISyncPacingSource? pacing = null,
        IDelayScheduler? delays = null,
        ILogger? log = null)
        : base(scopes, clock, diagnostics, SweepPace.Of(options ?? new GroupMemberSyncOptions()), elapsed, pacing, delays, log)
    {
    }

    protected override string Name => "Member";

    protected override SweepPace PaceFrom(SyncPacing pacing) => SweepPace.Of(pacing.MemberSweep);

    protected override Task<SweepRunResult> SweepAsync(IServiceProvider services, CancellationToken ct) =>
        services.GetRequiredService<GroupMemberSync>().RunOnceAsync(ct);

    protected override void Report(SyncRunReport report, SweepRunResult result) =>
        Diagnostics.RecordMemberSweepRun(report, result);

    protected override void Announce(string phase, DateTimeOffset nextPassAt) =>
        Diagnostics.RecordMemberSweepNext(phase, nextPassAt);
}

/// <summary>Runs the ban sweep. Its own service so a cold stop on <c>groups.bans</c> stops it and nothing else.</summary>
public sealed class GroupBanSyncService : GroupSweepService
{
    public GroupBanSyncService(
        IServiceScopeFactory scopes,
        IModbotClock clock,
        SyncDiagnostics diagnostics,
        IMonotonicClock? elapsed = null,
        GroupBanSyncOptions? options = null,
        ISyncPacingSource? pacing = null,
        IDelayScheduler? delays = null,
        ILogger? log = null)
        : base(scopes, clock, diagnostics, SweepPace.Of(options ?? new GroupBanSyncOptions()), elapsed, pacing, delays, log)
    {
    }

    protected override string Name => "Ban";

    protected override SweepPace PaceFrom(SyncPacing pacing) => SweepPace.Of(pacing.BanSweep);

    protected override Task<SweepRunResult> SweepAsync(IServiceProvider services, CancellationToken ct) =>
        services.GetRequiredService<GroupBanSync>().RunOnceAsync(ct);

    protected override void Report(SyncRunReport report, SweepRunResult result) =>
        Diagnostics.RecordBanSweepRun(report, result);

    protected override void Announce(string phase, DateTimeOffset nextPassAt) =>
        Diagnostics.RecordBanSweepNext(phase, nextPassAt);
}
