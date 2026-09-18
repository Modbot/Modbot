namespace Modbot.Core.Data.Entities;

/// <summary>
/// Where the last detection run got to. One row, like <see cref="DailyTotalsState"/>.
/// </summary>
/// <remarks>
/// The watermark is on <c>observed_at</c> for the same reason the daily totals one is: facts arrive
/// out of order, and a run keyed on when things <em>happened</em> would skip the audit entry that
/// the catch-up walk handed over an hour late. Losing this row costs a rebuild and nothing else.
/// </remarks>
public class ReviewRunState
{
    /// <summary>Always 1. Enforced by a database check constraint.</summary>
    public int Id { get; set; } = 1;

    /// <summary>The highest <c>observed_at</c> already looked at, or null before the first run.</summary>
    public DateTimeOffset? ObservedThrough { get; set; }

    /// <summary>When the last run finished, from <c>IModbotClock</c>.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// Which set of linked-fact pairs the log has been read under (spec 5.3.2).
    /// </summary>
    /// <remarks>
    /// Zero on a deployment that has never linked anything, which is every deployment the first
    /// time it runs a version that does. Behind the current number, the next run reads the whole
    /// log again and finds every decision in it — which is how the facts recorded before linking
    /// existed get their links, and how a release that learns about a new pair applies it to the
    /// history as well as to what arrives next.
    /// </remarks>
    public int LinkVersion { get; set; }
}
