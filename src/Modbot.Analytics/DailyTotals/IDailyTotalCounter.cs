namespace Modbot.Analytics.DailyTotals;

/// <summary>
/// Increments a daily total directly, with no fact behind it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Spec 5.2.1 -- the one deliberate exception to "everything is a fact".</strong> Some
/// events are high-cardinality and individually worthless: nobody will ever ask who sent a Discord
/// message at 14:32:07, only how many someone sent this week. For those, Modbot counts and writes
/// no fact at all.
/// </para>
/// <para>
/// The test for this path is one question: <em>would anyone ever query an individual one of
/// these?</em> If yes it is a fact, and it goes through <c>IFactWriter</c>. Kicks, bans, joins,
/// leaves and voice sessions are all facts. Message counts are not.
/// </para>
/// <para>
/// <strong>What it costs.</strong> These rows are not recomputable. The invariant that a metric
/// bug is a re-run rather than lost data (spec 5.2) does not hold here and cannot be made to:
/// there is nothing to re-read. A rebuild therefore leaves these rows alone, and a bug in one of
/// these counters loses data permanently. That is accepted <em>only</em> where the aggregate is
/// the datum.
/// </para>
/// <para>
/// It buys two things besides volume. A fact per message would dwarf every other source combined
/// for a query nobody runs -- and a per-message row carrying an author and a timestamp is a social
/// graph whether or not the text is attached. Counting leaves nothing to leak, subpoena, or
/// accidentally include in an export.
/// </para>
/// </remarks>
public interface IDailyTotalCounter
{
    /// <summary>
    /// Adds <paramref name="amount"/> to one daily total, creating the row if it is the first of
    /// the day.
    /// </summary>
    /// <param name="metric">
    /// Must not be a metric the daily totals job computes: the next rebuild would overwrite it.
    /// </param>
    /// <param name="dimension">A user id, channel id, or null for the undimensioned series.</param>
    /// <param name="day">
    /// The UTC day to count against. Defaults to today by <c>IModbotClock</c>; pass it explicitly
    /// when counting something whose own timestamp is known and may fall on another day.
    /// </param>
    Task IncrementAsync(
        string metric,
        string? dimension = null,
        decimal amount = 1m,
        DateOnly? day = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sets one daily total to a value observed now, replacing whatever the day held: the last
    /// reading of the day stands. For a snapshot such as a member count, where the reading itself
    /// is the datum and adding two readings together means nothing.
    /// </summary>
    Task SetAsync(
        string metric,
        decimal value,
        string? dimension = null,
        DateOnly? day = null,
        CancellationToken ct = default);
}
