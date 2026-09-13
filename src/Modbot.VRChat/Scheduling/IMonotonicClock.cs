using System.Diagnostics;

namespace Modbot.VRChat.Scheduling;

/// <summary>
/// Elapsed time since this process started, from a source nothing can move.
/// </summary>
/// <remarks>
/// <para>
/// Spec 4.2.2: intervals are measured from a monotonic source, so that an NTP correction or a VM
/// migration cannot compress or stretch the schedule. A machine whose clock jumps forward an hour
/// would otherwise discover that every sync in the next hour is already overdue and fire them all
/// at once — a burst, aimed at the API Modbot is trying not to annoy.
/// </para>
/// <para>
/// This does not compete with <c>IModbotClock</c>, which remains the authority for timestamps
/// (spec 4.4). Elapsed-time scheduling is a different question from "what time is it", and
/// answering it with wall-clock arithmetic is the mistake this interface exists to prevent.
/// </para>
/// </remarks>
public interface IMonotonicClock
{
    TimeSpan Elapsed { get; }
}

/// <summary>The production implementation. A stopwatch started when the process did.</summary>
public sealed class StopwatchMonotonicClock : IMonotonicClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public TimeSpan Elapsed => _stopwatch.Elapsed;
}
