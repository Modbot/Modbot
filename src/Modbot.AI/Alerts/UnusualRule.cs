using Modbot.Core.Data.Entities;

namespace Modbot.AI.Alerts;

/// <summary>Which way round a watcher is looking.</summary>
public enum AlertDirection
{
    /// <summary>Far more than usual -- a spike.</summary>
    Above,

    /// <summary>Far less than usual -- a drop.</summary>
    Below,
}

/// <summary>
/// One watcher's figure for the window just ended, and the matching earlier windows to judge it
/// against.
/// </summary>
/// <param name="Now">The figure for the window being judged.</param>
/// <param name="Earlier">
/// The same figure for each matching earlier window -- the same hour of the day on each of the last
/// fourteen days, or the four weeks before this one. Order does not matter.
/// </param>
/// <param name="Sensitivity">Off, low, normal or high, as the watcher is set.</param>
/// <param name="Minimum">
/// The smallest figure worth alerting on, whatever the history says. Keeps a quiet group from
/// alerting on two joins.
/// </param>
/// <param name="LeastEarlier">
/// How many earlier windows must be there before normal means anything. A deployment two days old
/// does not know what its own Friday night looks like.
/// </param>
public sealed record UnusualCheck(
    decimal Now,
    IReadOnlyList<decimal> Earlier,
    string Sensitivity,
    int Minimum,
    AlertDirection Direction = AlertDirection.Above,
    int LeastEarlier = UnusualRule.LeastEarlierWindows);

/// <summary>What the rule made of one window.</summary>
/// <param name="Unusual">Whether this is worth an alert.</param>
/// <param name="Normal">The middle of the earlier windows.</param>
/// <param name="Spread">How far the earlier windows usually sat from normal, never less than one.</param>
/// <param name="Score">
/// How many spreads this window sits from normal, on the side the watcher is looking. Zero when it
/// is on the other side. This is what "much worse" is measured in.
/// </param>
public sealed record UnusualVerdict(bool Unusual, decimal Now, decimal Normal, decimal Spread, decimal Score);

/// <summary>
/// The one place that decides whether a figure is unusual (AI insights design §8.2).
/// </summary>
/// <remarks>
/// <para>
/// Always against the deployment's own recent history, never against a fixed number: a group of
/// fifty and a group of five thousand have nothing in common except that each knows what its own
/// Tuesday evening looks like.
/// </para>
/// <para>
/// Normal is the <em>middle</em> of the earlier windows, not their average, and the spread is the
/// middle of how far each one sat from that middle. One past spike therefore does not raise the
/// bar high enough to hide the next one, which is exactly what an average would do.
/// </para>
/// <para>
/// Pure: no clock, no database. Every rule in here can be read and tested on its own.
/// </para>
/// </remarks>
public static class UnusualRule
{
    /// <summary>
    /// How many earlier windows a watcher wants before it will say anything at all.
    /// </summary>
    public const int LeastEarlierWindows = 5;

    /// <summary>The smallest a spread is ever treated as, so a perfectly steady figure still has room.</summary>
    public const decimal LeastSpread = 1m;

    /// <summary>
    /// A spike must also be at least half again the normal figure. Without it a big steady number
    /// -- three hundred joins an hour, give or take two -- would alert on a wobble of six.
    /// </summary>
    public const decimal LeastTimesNormal = 1.5m;

    /// <summary>
    /// How much worse an alert has to get before it is posted again inside its quiet time: twice as
    /// far from normal as the one that was posted.
    /// </summary>
    public const decimal MuchWorseTimes = 2m;

    /// <summary>How many spreads from normal a watcher must be, at each sensitivity.</summary>
    public static decimal Spreads(string sensitivity) => sensitivity switch
    {
        AlertSensitivities.Low => 6m,
        AlertSensitivities.Normal => 4m,
        AlertSensitivities.High => 2.5m,
        _ => 0m,
    };

    /// <summary>
    /// How much of normal is still alright for a drop. Below this share of normal, it alerts.
    /// </summary>
    public static decimal DropShare(string sensitivity) => sensitivity switch
    {
        AlertSensitivities.Low => 0.5m,
        AlertSensitivities.Normal => 0.65m,
        AlertSensitivities.High => 0.75m,
        _ => 0m,
    };

    /// <summary>
    /// How many people make a room busy: the group's own normal busiest room, eased by sensitivity,
    /// and never below <paramref name="minimum"/>.
    /// </summary>
    /// <remarks>
    /// Its own small rule because "nobody is watching a busy room" is a state, not a change: a room
    /// exactly as full as every other Friday still wants somebody in it.
    /// </remarks>
    public static decimal BusyEnough(decimal normalRoom, string sensitivity, int minimum)
    {
        var times = sensitivity switch
        {
            AlertSensitivities.Low => 2m,
            AlertSensitivities.Normal => 1.5m,
            AlertSensitivities.High => 1m,
            _ => decimal.MaxValue,
        };

        return times == decimal.MaxValue ? decimal.MaxValue : Math.Max(normalRoom * times, minimum);
    }

    /// <summary>Whether a fresh alert is much worse than the one already posted for that watcher.</summary>
    public static bool MuchWorseThan(decimal score, decimal postedScore)
        => postedScore <= 0 ? score > 0 : score >= postedScore * MuchWorseTimes;

    /// <summary>Judges one window.</summary>
    public static UnusualVerdict Check(UnusualCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);

        var normal = Middle(check.Earlier);
        var spread = Math.Max(Middle([.. check.Earlier.Select(v => Math.Abs(v - normal))]), LeastSpread);

        var distance = check.Direction == AlertDirection.Above ? check.Now - normal : normal - check.Now;
        var score = distance <= 0 ? 0m : distance / spread;

        var verdict = new UnusualVerdict(false, check.Now, normal, spread, score);

        if (!AlertSensitivities.IsKnown(check.Sensitivity) || check.Sensitivity == AlertSensitivities.Off)
            return verdict;

        if (check.Earlier.Count < check.LeastEarlier)
            return verdict;

        var unusual = check.Direction == AlertDirection.Above
            ? check.Now >= check.Minimum
                && check.Now >= normal + Spreads(check.Sensitivity) * spread
                && check.Now >= normal * LeastTimesNormal
            // A drop is judged against what normal was: a week with three active members out of a
            // usual five hundred is the alert, and the minimum applies to the five hundred.
            : normal >= check.Minimum
                && check.Now <= normal * DropShare(check.Sensitivity);

        return verdict with { Unusual = unusual };
    }

    /// <summary>The middle value, or the average of the middle two. Zero for nothing at all.</summary>
    public static decimal Middle(IReadOnlyList<decimal> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
            return 0m;

        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;

        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2m;
    }
}
