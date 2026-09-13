namespace Modbot.Core.Data.Entities;

/// <summary>
/// One daily aggregate: <c>(day, metric, dimension) -&gt; value</c>.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.4. Deliberately generic: a metric is a string and a dimension is a string, so a metric
/// invented next year needs no migration -- it needs a registry entry and a backfill run.
/// </para>
/// <para>
/// <strong>Rollups are derived data and are always recomputable from facts</strong> (spec 5.2) --
/// with the one exception recorded in <see cref="RollupOrigin.Counted"/>. A bug in an aggregation
/// is fixed by re-running the job, never by hand-editing a value here.
/// </para>
/// <para>
/// Rollups are never aged out, whatever retention is set to (spec 5.5). They outlive the facts
/// they were computed from, so an operator who does configure a window keeps the charts built on
/// the pruned days -- which is what keeps "who are our regulars over two years" answerable even
/// then.
/// </para>
/// </remarks>
public class RollupDaily
{
    /// <summary>
    /// The UTC day being summarised. UTC and not a group-local timezone, because the fact log is
    /// partitioned on UTC months and two different day boundaries in one system is a bug farm.
    /// </summary>
    public DateOnly Day { get; set; }

    /// <summary>
    /// Dotted name, e.g. <c>members.joined</c>. See <c>RollupMetrics</c> for the ones the rollup
    /// job computes.
    /// </summary>
    public string Metric { get; set; } = string.Empty;

    /// <summary>
    /// What the value is broken down by -- a moderator id, a user id, an instance id -- or the
    /// empty string for the undimensioned series.
    /// </summary>
    /// <remarks>
    /// Spec 5.4 writes this column as nullable. It is <c>NOT NULL</c> with an empty-string
    /// sentinel instead, because PostgreSQL will not accept a NULL in a primary key column and
    /// the alternative (a surrogate key plus a <c>NULLS NOT DISTINCT</c> unique index) buys
    /// nothing but a second way to say "no dimension".
    /// </remarks>
    public string Dimension { get; set; } = string.Empty;

    /// <summary>
    /// <c>numeric</c>, not an integer count, because imprecise facts are apportioned across the
    /// days their window covers and that apportionment is fractional (see <c>RollupJob</c>).
    /// </summary>
    public decimal Value { get; set; }

    public RollupOrigin Origin { get; set; }
}

/// <summary>
/// Whether a rollup row can be rebuilt from the fact log, or is the only copy of its data.
/// </summary>
/// <remarks>
/// <para><strong>Persisted as smallint. Never renumber a member.</strong></para>
/// </remarks>
public enum RollupOrigin : short
{
    /// <summary>
    /// Derived from facts by the rollup job. Safe to delete and recompute at any time; that
    /// recomputation is the supported fix for every aggregation bug (spec 5.2).
    /// </summary>
    Computed = 1,

    /// <summary>
    /// Incremented directly, with no fact behind it (spec 5.2.1).
    /// </summary>
    /// <remarks>
    /// <strong>These rows are not recomputable.</strong> Deleting one destroys data that exists
    /// nowhere else, so a rebuild must leave them alone. The path is accepted only for metrics
    /// where the aggregate <em>is</em> the datum -- Discord message volume being the case it was
    /// built for, where a per-message fact would dwarf every other source combined, answer a
    /// question nobody asks, and amount to storing a social graph.
    /// </remarks>
    Counted = 2,
}
