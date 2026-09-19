namespace Modbot.Analytics.Activity;

/// <summary>
/// How much of a window Modbot was actually counting, so a peak can be read for what it is.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the honesty half of the peaks, and it is not optional.</strong> A peak drawn from
/// two hours of counting in a week is not the week's peak, and nothing about the number "48" says
/// which of those two it is. The numbers here say it instead — in the data, where a reader can see
/// them beside the peak, rather than in a paragraph on screen.
/// </para>
/// <para>
/// <strong>The denominator is open time, not calendar time.</strong> A ninety-day window in which
/// the group ran instances on ten evenings is not thin coverage; it is a quiet group, and measuring
/// it against ninety days would call every small community's figures unreliable. What matters is
/// the share of the time instances were actually open that Modbot had a count for.
/// </para>
/// <para>
/// <strong>Where the counts come from.</strong> The instance head counts are VRChat's own numbers,
/// read from each open group instance's page about every thirty seconds, and they need no
/// moderator's companion at all — unlike the presence figures on the Worlds and Team pages. So
/// coverage here is thin only when the sync was off, cold-stopped by a 429, or newer than the
/// window, and that is worth saying plainly rather than assuming it never happens.
/// </para>
/// </remarks>
/// <param name="WindowDays">Days in the window asked for.</param>
/// <param name="DaysCounted">Days in the window on which at least one instance was being counted.</param>
/// <param name="InstancesOpen">The group's instances that were open at some point in the window.</param>
/// <param name="InstancesCounted">How many of those ever had a head count recorded.</param>
/// <param name="MinutesInstancesWereOpen">
/// Minutes of instance time inside the window, added up across instances — two instances open for
/// an hour each is a hundred and twenty.
/// </param>
/// <param name="MinutesCounted">
/// Of those minutes, the ones after each instance's first head count. An instance Modbot never read
/// contributes nothing; one it began reading halfway through contributes its second half.
/// </param>
public sealed record InstanceCoverage(
    int WindowDays,
    int DaysCounted,
    int InstancesOpen,
    int InstancesCounted,
    decimal MinutesInstancesWereOpen,
    decimal MinutesCounted)
{
    /// <summary>Nothing open and nothing counted — what an empty window comes back as.</summary>
    public static InstanceCoverage Nothing { get; } = new(0, 0, 0, 0, 0m, 0m);

    /// <summary>Below this share of open time counted, the figures are shown but called thin.</summary>
    public const decimal ThinBelow = 0.5m;

    /// <summary>
    /// Whether the peaks rest on less than half the time instances were open.
    /// </summary>
    /// <remarks>
    /// A single flag rather than a ratio on screen, because the screen's job is to mark the number
    /// and not to teach the arithmetic; the minutes are all here for anyone who wants to check it.
    /// A window with no open instances is not thin — there is nothing to have missed.
    /// </remarks>
    public bool Thin =>
        MinutesInstancesWereOpen > 0m && MinutesCounted < MinutesInstancesWereOpen * ThinBelow;
}
