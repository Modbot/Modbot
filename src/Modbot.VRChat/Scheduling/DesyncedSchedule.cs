namespace Modbot.VRChat.Scheduling;

/// <summary>
/// Paces one sync type: <c>processStart + randomOffset(type) + n × interval ± jitter</c>.
/// </summary>
/// <remarks>
/// <para>
/// Spec 4.2.2. If Modbot scheduled from a fixed clock — every two seconds on the even second —
/// then every Modbot server in the world would hit VRChat simultaneously. The total volume
/// would be unchanged and the shape would be far worse: synchronised spikes, from many hosts, on
/// a schedule. That also happens to be exactly what a coordinated botnet looks like, which is not
/// an impression this project can afford.
/// </para>
/// <para>
/// Three things spread the traffic, and all three are needed:
/// </para>
/// <list type="bullet">
/// <item>the offset is drawn per process, so a fleet restarting together — a platform event, say
/// — re-randomises instead of marching in lockstep;</item>
/// <item>it is drawn per sync type, so one server's own six types do not fire as a burst;</item>
/// <item>per-tick jitter keeps the pattern from settling into a regular comb that would slowly
/// re-align with other servers.</item>
/// </list>
/// <para>
/// Nothing here reads a clock of any kind. Time comes from <see cref="IMonotonicClock"/>, and the
/// offset from an injected <see cref="Random"/> so that a test can assert on a distribution
/// rather than on one lucky draw.
/// </para>
/// </remarks>
public sealed class DesyncedSchedule
{
    private readonly IMonotonicClock _clock;
    private readonly Random _random;
    private readonly double _jitterFraction;

    private long _tick;

    /// <param name="interval">The pacing interval for this sync type (spec 4.2's table).</param>
    /// <param name="clock">Elapsed time since process start.</param>
    /// <param name="random">
    /// The offset and jitter source. Injected rather than <see cref="Random.Shared"/> so tests
    /// are deterministic; production passes the shared instance, which is what makes separate
    /// deployments disagree with one another.
    /// </param>
    /// <param name="jitterFraction">Per-tick jitter, as a fraction of the interval (spec 4.2.2: ~10%).</param>
    public DesyncedSchedule(
        TimeSpan interval,
        IMonotonicClock clock,
        Random? random = null,
        double jitterFraction = 0.1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegative(jitterFraction);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(jitterFraction, 1.0);

        _clock = clock;
        _random = random ?? Random.Shared;
        _jitterFraction = jitterFraction;

        Interval = interval;

        // Uniform in [0, interval): the whole interval is a valid starting phase, and anything
        // narrower would leave the servers clustered.
        Offset = interval * _random.NextDouble();

        // A schedule built part-way through a process's life -- which is what re-reading a
        // changed interval produces (spec 4.2.1) -- starts counting from where that process has
        // already got to. Without this, NextDelay would walk tick by tick from zero to the
        // present: correct, and on a long-running host, millions of iterations to return one
        // delay. A schedule built at startup is unaffected, because elapsed is still inside the
        // first offset.
        var elapsed = clock.Elapsed;
        if (elapsed > Offset)
            _tick = (elapsed - Offset).Ticks / interval.Ticks;
    }

    public TimeSpan Interval { get; }

    /// <summary>This process's phase for this sync type. Never persisted (spec 4.2.2).</summary>
    public TimeSpan Offset { get; }

    /// <summary>How many ticks have been handed out.</summary>
    public long Tick => _tick;

    /// <summary>
    /// How long to wait before the next run, measured from now.
    /// </summary>
    /// <remarks>
    /// If the process has fallen behind — a long sync, a paused container, a machine that slept —
    /// the missed slots are skipped rather than run back to back. Catching up would produce
    /// exactly the burst the pacing exists to prevent, and the data it would fetch is the same
    /// data the next tick fetches anyway.
    /// </remarks>
    public TimeSpan NextDelay()
    {
        var elapsed = _clock.Elapsed;

        while (true)
        {
            var target = Offset + (Interval * _tick) + Jitter();
            _tick++;

            if (target > elapsed)
                return target - elapsed;
        }
    }

    private TimeSpan Jitter()
    {
        if (_jitterFraction <= 0)
            return TimeSpan.Zero;

        var swing = ((_random.NextDouble() * 2) - 1) * _jitterFraction;
        return Interval * swing;
    }
}
