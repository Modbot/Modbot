using System.Diagnostics;

namespace Modbot.Companion.Presentation;

/// <summary>
/// How long each step of one piece of work took, for the log, so a slow one can be named.
/// </summary>
/// <remarks>
/// Timed with the stopwatch's counter, not the clock: only how long things took matters, and the
/// counter never jumps when the PC's time is changed.
/// </remarks>
public sealed class StepTimes
{
    private readonly Func<long> _now;
    private readonly long _started;
    private readonly List<(string Step, TimeSpan Took)> _steps = [];
    private long _last;

    /// <param name="now">The counter to read; the stopwatch's unless a test hands one in.</param>
    public StepTimes(Func<long>? now = null)
    {
        _now = now ?? Stopwatch.GetTimestamp;
        _started = _last = _now();
    }

    /// <summary>The steps so far, in order, each with how long it took.</summary>
    public IReadOnlyList<(string Step, TimeSpan Took)> Steps => _steps;

    /// <summary>From the start to the end of the last step.</summary>
    public TimeSpan Total => Stopwatch.GetElapsedTime(_started, _last);

    /// <summary>A step has just finished: it took the time since the one before it.</summary>
    public void Done(string step)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step);

        var now = _now();
        _steps.Add((step, Stopwatch.GetElapsedTime(_last, now)));
        _last = now;
    }

    /// <summary>Each step and its milliseconds, the slowest first, for one log line.</summary>
    public string Describe()
        => string.Join(
            ", ",
            _steps
                .OrderByDescending(s => s.Took)
                .Select(s => FormattableString.Invariant($"{s.Step} {s.Took.TotalMilliseconds:0} ms")));
}
