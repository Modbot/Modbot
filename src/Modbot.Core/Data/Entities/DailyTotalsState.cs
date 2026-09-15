namespace Modbot.Core.Data.Entities;

/// <summary>
/// Where the incremental daily totals run got to. One row, like <see cref="Settings"/>.
/// </summary>
/// <remarks>
/// <para>
/// The watermark is on <c>observed_at</c> -- when Modbot learned of a fact -- and not on
/// <c>occurred_at</c>, because facts arrive out of order: a sync diff dates a departure to before
/// the process started, and a moderator client reconnecting after an outage reports events from
/// an hour ago. A watermark on <c>occurred_at</c> would skip every one of them silently.
/// </para>
/// <para>
/// Losing this row costs nothing but time: a full rebuild reconstructs every computed daily total from
/// the fact log (spec 5.2).
/// </para>
/// </remarks>
public class DailyTotalsState
{
    /// <summary>Always 1. Enforced by a database check constraint.</summary>
    public int Id { get; set; } = 1;

    /// <summary>
    /// The highest <c>observed_at</c> already folded into the daily totals, or null before the first
    /// run. The next run re-scans a little behind this (see <c>DailyTotalsJob</c>).
    /// </summary>
    public DateTimeOffset? ObservedThrough { get; set; }

    /// <summary>
    /// The highest <c>stored_at</c> of a Discord message already folded in, or null before the first
    /// run. Messages are not facts, so they need a mark of their own; read back from years ago, a
    /// message is stored today and belongs to a day long past.
    /// </summary>
    public DateTimeOffset? MessagesStoredThrough { get; set; }

    /// <summary>When the last run finished, from <c>IModbotClock</c>. Operational detail only.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}
