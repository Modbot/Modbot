using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Data;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Scheduling;
using Serilog;

namespace Modbot.VRChat.Sync;

/// <summary>
/// The loop the two place producers share: wait, run once, wait again.
/// </summary>
/// <remarks>
/// <para>
/// Simpler than the sweep and audit-log loops, and deliberately so. Those have an operator-facing
/// poll rate because there is a real trade to make -- how much of a shared budget to spend on
/// freshness. These two have neither. The group instance poll runs at exactly the measured limit,
/// because running it slower only means an unattended event shows up later and buys nothing; the
/// world sweep is idle almost always, since its queue is "worlds that have never been named" and
/// that is empty once a group has settled.
/// </para>
/// <para>
/// One service per bucket all the same, because a cold stop is scoped to a bucket (spec 4.3.1):
/// a 429 on <c>worlds.read</c> must not stop the poll that is the only view Modbot has of an instance
/// nobody is standing in.
/// </para>
/// <para>
/// The wait is desynchronised (spec 4.2.2) on a monotonic source, so a fleet of Modbots restarting
/// together does not poll in step, and an NTP step cannot compress the schedule into a burst.
/// </para>
/// </remarks>
public abstract class PlaceSyncService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IDelayScheduler _delays;
    private readonly DesyncedSchedule _schedule;

    protected PlaceSyncService(
        IServiceScopeFactory scopes,
        TimeSpan interval,
        IMonotonicClock? elapsed = null,
        IDelayScheduler? delays = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        _scopes = scopes;
        _schedule = new DesyncedSchedule(interval, elapsed ?? new StopwatchMonotonicClock(), jitterFraction: 0.1);
        _delays = delays ?? new RealDelayScheduler();
        Log = (log ?? Serilog.Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    protected ILogger Log { get; }

    /// <summary>How long to wait after a cold stop before looking again.</summary>
    /// <remarks>
    /// Longer than the ordinary interval, because nothing will be sent until the stop lifts and
    /// polling through it would produce only refusals in the log (spec 4.3.1).
    /// </remarks>
    protected virtual TimeSpan RateLimitedInterval => TimeSpan.FromMinutes(5);

    protected virtual TimeSpan RetryInterval => TimeSpan.FromMinutes(1);

    /// <summary>A word for the log: "group instance poll" or "world sweep".</summary>
    protected abstract string What { get; }

    /// <summary>Runs one pass inside a scope of its own.</summary>
    protected abstract Task<SyncOutcome> RunOnceAsync(IServiceProvider scope, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var next = _schedule.NextDelay();

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await WaitAsync(next, stoppingToken).ConfigureAwait(false))
                return;

            var outcome = await RunSafelyAsync(stoppingToken).ConfigureAwait(false);

            next = outcome switch
            {
                SyncOutcome.RateLimited => RateLimitedInterval,
                SyncOutcome.Failed => RetryInterval,
                _ => _schedule.NextDelay(),
            };
        }
    }

    private async Task<SyncOutcome> RunSafelyAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            return await RunOnceAsync(scope.ServiceProvider, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return SyncOutcome.Quiet;
        }
        catch (Exception ex)
        {
            // A producer that throws must not take the host down with it, and must not stop
            // trying: the next pass is a fresh scope and a fresh context.
            Log.Error(ex, "The {What} failed; it will try again", What);
            return SyncOutcome.Failed;
        }
    }

    private async Task<bool> WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await _delays.DelayAsync(delay, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>
/// Polls which instances the managed group has open, every ten seconds.
/// </summary>
/// <remarks>
/// Ten seconds is the measured limit for <c>/groups/{groupId}/instances</c>, and this poll is the
/// only way Modbot sees an instance nobody from the moderation team is standing in -- so it runs at the
/// limit rather than below it. See <see cref="GroupInstanceSync"/>.
/// </remarks>
public sealed class GroupInstanceSyncService : PlaceSyncService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    public GroupInstanceSyncService(
        IServiceScopeFactory scopes,
        IMonotonicClock? elapsed = null,
        IDelayScheduler? delays = null,
        ILogger? log = null)
        : base(scopes, Interval, elapsed, delays, log)
    {
    }

    protected override string What => "group instance poll";

    protected override async Task<SyncOutcome> RunOnceAsync(IServiceProvider scope, CancellationToken ct)
    {
        var sync = scope.GetRequiredService<GroupInstanceSync>();
        var result = await sync.RunOnceAsync(ct).ConfigureAwait(false);
        return result.Outcome;
    }
}

/// <summary>
/// Reads open group instances' own pages for their head counts, a few instances a pass.
/// </summary>
/// <remarks>
/// Every five seconds, because a pass only reads instances whose last read is thirty seconds old
/// (<see cref="InstanceHeadCountSync.ReadEvery"/>): the loop comes round often and usually finds
/// little to do, which keeps each instance close to its thirty seconds without a timer per instance.
/// </remarks>
public sealed class InstanceHeadCountSyncService : PlaceSyncService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    public InstanceHeadCountSyncService(
        IServiceScopeFactory scopes,
        IMonotonicClock? elapsed = null,
        IDelayScheduler? delays = null,
        ILogger? log = null)
        : base(scopes, Interval, elapsed, delays, log)
    {
    }

    protected override string What => "instance head count read";

    protected override async Task<SyncOutcome> RunOnceAsync(IServiceProvider scope, CancellationToken ct)
    {
        var sync = scope.GetRequiredService<InstanceHeadCountSync>();
        var result = await sync.RunOnceAsync(ct).ConfigureAwait(false);
        return result.Outcome;
    }
}

/// <summary>
/// Puts names to worlds that have only ever been seen as an id, and closes instances that have gone
/// quiet for long enough to count as finished.
/// </summary>
/// <remarks>
/// <para>
/// Both jobs are in one service because both are housekeeping that nobody is waiting on, and
/// because closing a long-quiet instance needs no VRChat call at all -- so it cannot be what a cold
/// stop on <c>worlds.read</c> takes down. The world read is attempted first and the closing runs
/// either way.
/// </para>
/// <para>
/// Five minutes, because the queue is nearly always empty: a world is read once, when it is first
/// seen, and never again unless it turns up in a new instance (<see cref="GroupInstanceSync"/>).
/// </para>
/// </remarks>
public sealed class WorldSyncService : PlaceSyncService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public WorldSyncService(
        IServiceScopeFactory scopes,
        IMonotonicClock? elapsed = null,
        IDelayScheduler? delays = null,
        ILogger? log = null)
        : base(scopes, Interval, elapsed, delays, log)
    {
    }

    protected override string What => "world sweep";

    protected override async Task<SyncOutcome> RunOnceAsync(IServiceProvider scope, CancellationToken ct)
    {
        var sync = scope.GetRequiredService<WorldSync>();
        var result = await sync.RunOnceAsync(ct).ConfigureAwait(false);

        // Runs whatever the world read did, including after a cold stop: it asks VRChat nothing,
        // and an instance that has been quiet for three days is finished regardless.
        var places = scope.GetRequiredService<PlaceStore>();
        var db = scope.GetRequiredService<ModbotContext>();

        var closed = await places.CloseLongQuietInstancesAsync(ct).ConfigureAwait(false);

        if (closed > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            Log.Information("Closed {Closed} instances that had been quiet for {Quiet}", closed, VRChatInstanceQuiet);
        }

        return result.Outcome;
    }

    private static string VRChatInstanceQuiet => $"{Core.Data.Entities.VRChatInstance.CountsAsNewAfter.TotalHours:0} hours";
}
