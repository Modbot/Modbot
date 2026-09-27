using Modbot.Analytics.DailyTotals;

namespace Modbot.Api.Features.Analytics;

/// <summary>
/// What one analytics page draws from: the daily totals it charts, the fact types it reads, and
/// the tables of its own it keeps. "All time" on that page starts at the first day any of these
/// has something for.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Per page, not per store.</strong> "All time" used to start at the first day of anything
/// at all. The Discord bot reads message history back when it signs in, and on a live server that
/// reached 2015, so the Instances, VRChat, Team and Worlds pages each opened on eleven years of
/// nothing before their own first day in 2026. Each page now starts where its own data does, and
/// the Discord page still starts in 2015 because its messages really do.
/// </para>
/// <para>
/// The fact types behind each daily total are counted as well as the totals themselves: facts
/// arrive before the daily totals job has run over them, and totals outlive facts a retention
/// window has deleted, so either can be the earlier.
/// </para>
/// </remarks>
/// <param name="Metrics">The daily total metrics the page charts.</param>
/// <param name="FactTypes">
/// Every fact type the page reads, including those behind <paramref name="Metrics"/> — build with
/// <see cref="Of"/> so those are never left out.
/// </param>
/// <param name="GroupInstances">The page reads the group's rows in <c>vrchat_instance</c>.</param>
/// <param name="HeadCounts">The page reads <c>instance_head_count</c>.</param>
/// <param name="MemberCountReadings">The page reads the group's rows in <c>group_member_count</c>.</param>
public sealed record PageSources(
    IReadOnlyList<string> Metrics,
    IReadOnlyList<string> FactTypes,
    bool GroupInstances = false,
    bool HeadCounts = false,
    bool MemberCountReadings = false)
{
    /// <summary>
    /// A page's sources: its metrics, the fact types they are counted from, and the other fact
    /// types it reads directly.
    /// </summary>
    public static PageSources Of(
        IReadOnlyList<string> metrics,
        IEnumerable<string> factTypes,
        bool groupInstances = false,
        bool headCounts = false,
        bool memberCountReadings = false)
        => new(
            metrics,
            TypesBehind(metrics).Concat(factTypes).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            groupInstances,
            headCounts,
            memberCountReadings);

    /// <summary>
    /// The fact types the daily totals job counts the named metrics from. Metrics counted from
    /// stored Discord messages or set as readings have none: their daily totals are their record.
    /// </summary>
    public static IEnumerable<string> TypesBehind(IEnumerable<string> metrics)
    {
        foreach (var metric in metrics)
        {
            foreach (var count in DailyTotalMetrics.FactCounts.Where(m => m.Name == metric))
            foreach (var type in count.Types)
                yield return type;

            if (DailyTotalMetrics.VoiceMinutes.Any(m => m.Name == metric))
                foreach (var type in DailyTotalMetrics.VoiceTypes)
                    yield return type;

            foreach (var running in DailyTotalMetrics.Cumulative.Where(m => m.Name == metric))
            foreach (var type in TypesBehind([running.Plus, running.Minus]))
                yield return type;
        }
    }
}
