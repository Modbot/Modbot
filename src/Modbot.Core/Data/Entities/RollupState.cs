namespace Modbot.Core.Data.Entities;

/// <summary>
/// Where the incremental rollup run got to. One row, like <see cref="Settings"/>.
/// </summary>
/// <remarks>
/// <para>
/// The watermark is on <c>observed_at</c> -- when Modbot learned of a fact -- and not on
/// <c>occurred_at</c>, because facts arrive out of order: a sync diff dates a departure to before
/// the process started, and a moderator client reconnecting after an outage reports events from
/// an hour ago. A watermark on <c>occurred_at</c> would skip every one of them silently.
/// </para>
/// <para>
/// Losing this row costs nothing but time: a full rebuild reconstructs every computed rollup from
/// the fact log (spec 5.2).
/// </para>
/// </remarks>
public class RollupState
{
    /// <summary>Always 1. Enforced by a database check constraint.</summary>
    public int Id { get; set; } = 1;

    /// <summary>
    /// The highest <c>observed_at</c> already folded into the rollups, or null before the first
    /// run. The next run re-scans a little behind this (see <c>RollupJob</c>).
    /// </summary>
    public DateTimeOffset? ObservedThrough { get; set; }

    /// <summary>When the last run finished, from <c>IModbotClock</c>. Operational detail only.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}
