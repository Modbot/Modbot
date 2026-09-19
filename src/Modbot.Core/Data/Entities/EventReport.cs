namespace Modbot.Core.Data.Entities;

/// <summary>
/// A second client's report of a fact that was already recorded: who else saw the same thing.
/// </summary>
/// <remarks>
/// <para>
/// Four to six moderators standing in one instance all see the same person walk in and all report
/// it. Only the first report becomes a fact -- writing the rest would turn one arrival into six,
/// and every time-spent figure with it (see <c>FactWriter</c>). Until this table existed the rest
/// were counted in the ingest response and then dropped, so nothing could ever say that two
/// independent clients agreed, which is stronger evidence than one client saying it alone.
/// </para>
/// <para>
/// <strong>One row per extra client, never a second fact.</strong> The fact keeps its own reporter
/// in its payload; this table holds only the clients that reported it afterwards. Nothing counts
/// from here -- no arrival, no minute, no action -- so a fact with five supporting reports is still
/// exactly one event to everything that adds anything up.
/// </para>
/// <para>
/// <strong>Derived, like <see cref="LinkedFact"/>.</strong> Facts are never mutated
/// (see <see cref="ModbotEvent"/>), so the extra reporters are written beside the fact rather than
/// appended to it. The rows are pruned with the facts they describe.
/// </para>
/// </remarks>
public class EventReport
{
    /// <summary>The fact that was already there when this report arrived.</summary>
    public long FactId { get; set; }

    /// <summary>
    /// The fact's own <c>occurred_at</c>. Kept here because <c>modbot_event</c> is partitioned by
    /// it, and because retention prunes these rows by the same bound it drops partitions at.
    /// </summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Which paired client reported it. Never the one already named on the fact.</summary>
    public Guid DeviceId { get; set; }

    /// <summary>When this report reached the server, from <c>IModbotClock</c>.</summary>
    public DateTimeOffset ReportedAt { get; set; }
}
