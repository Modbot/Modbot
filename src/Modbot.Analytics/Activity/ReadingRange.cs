namespace Modbot.Analytics.Activity;

/// <summary>
/// The four ranges a chart of readings offers, and how far apart its points end up.
/// </summary>
/// <remarks>
/// <para>
/// A chart of readings is not a chart of days. Instance head counts change every half minute while
/// an instance is busy, so a week is tens of thousands of moments and a line eight hundred pixels
/// wide can draw about five hundred. The window is therefore cut into equal steps and the last
/// reading in each step is kept — the last and not an average, so every point on the line is a
/// number that was true at the time shown.
/// </para>
/// <para>
/// The same four words the member count chart uses, because a moderator switching between the two
/// charts should not have to learn a second vocabulary for the same idea.
/// </para>
/// </remarks>
public static class ReadingRange
{
    public const string Day = "day";
    public const string Week = "week";
    public const string Month = "month";
    public const string All = "all";

    /// <summary>The most points a range is served as.</summary>
    public const int MaxPoints = 500;

    public static bool IsRange(string? range) => range is Day or Week or Month or All;

    /// <summary>How far back a range reaches, or null for all recorded time.</summary>
    /// <exception cref="ArgumentException">The range is not one of the four.</exception>
    public static TimeSpan? SpanOf(string range) => range switch
    {
        Day => TimeSpan.FromDays(1),
        Week => TimeSpan.FromDays(7),
        Month => TimeSpan.FromDays(30),
        All => null,
        _ => throw new ArgumentException($"`{range}` is not a range.", nameof(range)),
    };

    /// <summary>
    /// The length of one step, in whole seconds, so a window of <paramref name="span"/> comes out at
    /// no more than <see cref="MaxPoints"/> points. Never shorter than a second.
    /// </summary>
    public static int StepSeconds(TimeSpan span)
        => Math.Max(1, (int)Math.Ceiling(span.TotalSeconds / MaxPoints));
}
