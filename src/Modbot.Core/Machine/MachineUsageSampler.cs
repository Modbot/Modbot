using Modbot.Core.Time;

namespace Modbot.Core.Machine;

/// <summary>
/// One moment in the window: what the machine was doing between the reading before this one and
/// this one.
/// </summary>
/// <param name="At">When the reading was taken, from Modbot's clock.</param>
/// <param name="ProcessorPercent">
/// How much of the processor this server used over that time, where 100 is every core flat out.
/// Null when the processor time cannot be read on this host.
/// </param>
/// <param name="MemoryBytes">Memory this server was holding at that moment. Null when unreadable.</param>
/// <param name="DiskReadBytesPerSecond">Bytes a second read from disk over that time, or null when unreadable.</param>
/// <param name="DiskWrittenBytesPerSecond">Bytes a second written to disk over that time, or null when unreadable.</param>
public sealed record MachineUsagePoint(
    DateTimeOffset At,
    double? ProcessorPercent,
    long? MemoryBytes,
    double? DiskReadBytesPerSecond,
    double? DiskWrittenBytesPerSecond);

/// <summary>
/// How hard this machine has been working over the last half hour, kept in memory.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A diagnostic, not a metrics system.</strong> The window is a fixed number of samples in
/// memory: nothing is written to the database, nothing survives a restart, and nothing grows. An
/// operator opens the screen to answer "is this server struggling right now", and the honest cost
/// of that answer is a few kilobytes and one timer.
/// </para>
/// <para>
/// <strong>Ten seconds, thirty minutes.</strong> Ten seconds is fine enough that a sync pass or a
/// large import shows as a bump rather than being averaged away, and coarse enough that the
/// sampling itself is free. Thirty minutes is as far back as the question reaches — somebody who
/// has just noticed the app is slow wants the last few minutes, and anybody asking about last week
/// is asking a question this was never meant to answer. That is 180 points, each five numbers.
/// </para>
/// <para>
/// <strong>A rate needs two moments.</strong> Processor time and the disk counters are running
/// totals, so the first reading after start only becomes a baseline; the first point appears one
/// interval later. That is why <see cref="Take"/> can return null.
/// </para>
/// </remarks>
public sealed class MachineUsageSampler(IModbotClock clock)
{
    /// <summary>How long between readings.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(10);

    /// <summary>How far back the window reaches.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    /// <summary>The most points kept. The oldest is dropped to make room.</summary>
    public static int Capacity => (int)(Window.Ticks / Every.Ticks);

    private readonly object _gate = new();
    private readonly Queue<MachineUsagePoint> _points = new();
    private (DateTimeOffset At, MachineCounters Counters)? _previous;
    private long? _memoryLimitBytes;

    /// <summary>
    /// How many processors the percentage is measured against. Inside a container this is the share
    /// the container was given, not the machine's, which is what the runtime reports and what the
    /// operator is paying for.
    /// </summary>
    public int Processors { get; init; } = Environment.ProcessorCount;

    /// <summary>The most memory this server may use, as of the last reading. Null when nothing says.</summary>
    public long? MemoryLimitBytes
    {
        get { lock (_gate) return _memoryLimitBytes; }
    }

    /// <summary>The window, oldest first.</summary>
    public IReadOnlyList<MachineUsagePoint> Points
    {
        get { lock (_gate) return [.. _points]; }
    }

    /// <summary>
    /// Records one reading and returns the point it produced, or null when there is nothing to
    /// compare it against yet.
    /// </summary>
    public MachineUsagePoint? Take(MachineCounters counters)
    {
        var now = clock.UtcNow;

        lock (_gate)
        {
            _memoryLimitBytes = counters.MemoryLimitBytes;

            var previous = _previous;
            _previous = (now, counters);

            if (previous is not { } before)
                return null;

            var seconds = (now - before.At).TotalSeconds;

            // A clock that has not moved, or has stepped backwards, gives a rate of nothing over
            // nothing. The reading is still kept as the next baseline.
            if (seconds <= 0)
                return null;

            var point = new MachineUsagePoint(
                now,
                Percent(before.Counters.ProcessorTime, counters.ProcessorTime, seconds),
                counters.MemoryBytes,
                PerSecond(before.Counters.DiskReadBytes, counters.DiskReadBytes, seconds),
                PerSecond(before.Counters.DiskWrittenBytes, counters.DiskWrittenBytes, seconds));

            _points.Enqueue(point);

            while (_points.Count > Capacity)
                _points.Dequeue();

            return point;
        }
    }

    private double? Percent(TimeSpan? before, TimeSpan? after, double seconds)
    {
        if (before is not { } earlier || after is not { } later || Processors <= 0)
            return null;

        var used = (later - earlier).TotalSeconds / (seconds * Processors) * 100;

        // Clamped rather than reported raw: a reading that straddles a step in either counter can
        // come out slightly over 100 or below zero, and neither is a thing a processor can do.
        return Math.Clamp(used, 0, 100);
    }

    private static double? PerSecond(long? before, long? after, double seconds)
    {
        if (before is not { } earlier || after is not { } later)
            return null;

        // These counters only ever climb; a fall means the reading is not comparable, and nothing
        // is a better answer than a negative rate.
        var moved = later - earlier;
        return moved < 0 ? null : moved / seconds;
    }
}
