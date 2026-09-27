namespace Modbot.Analytics.Activity;

/// <summary>
/// The four ranges a chart of readings offers, and how far apart its points end up.
/// </summary>
/// <remarks>
/// <para>
/// A chart of readings is not a chart of days. Instance head counts change every half minute while
/// an instance is busy, so a week is tens of thousands of moments and a line eight hundred pixels
/// wide can draw about five hundred. The window is therefore cut into equal steps and a few
/// readings in each step are kept. Which ones depends on what the chart is of: a level such as a
/// member count keeps each step's last reading, and a head count also keeps each step's highest,
/// so the line reaches every peak the page prints above it.
/// </para>
/// <para>
/// <strong>Steps sit on fixed boundaries.</strong> A step is a whole multiple of its length since
/// the Unix epoch, not a distance from "now minus the range". Counted from the moving start, every
/// reload cut the same readings into different steps and drew the same evening at different
/// heights. The length itself is picked from a short list of clock-friendly lengths, so it stays
/// the same while "all time" grows, instead of changing by a second every few minutes and moving
/// every boundary with it.
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

    /// <summary>The most steps a range is cut into.</summary>
    public const int MaxPoints = 500;

    /// <summary>
    /// The step lengths, in seconds, shorter than a day. Past a day the step is a whole number of days.
    /// </summary>
    private static readonly int[] Lengths =
    [
        1, 2, 3, 5, 10, 15, 20, 30,
        60, 120, 180, 300, 600, 900, 1200, 1800,
        3600, 7200, 10800, 14400, 21600, 43200, 86400,
    ];

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
    /// The length of one step, in whole seconds: the shortest length on the list that cuts a window
    /// of <paramref name="span"/> into no more than <see cref="MaxPoints"/> steps, counting the part
    /// steps at either end. Never shorter than a second.
    /// </summary>
    /// <remarks>
    /// A window that does not start on a boundary touches one step more than its length divides
    /// into, so the length is worked out against <c>MaxPoints - 1</c>.
    /// </remarks>
    public static int StepSeconds(TimeSpan span)
    {
        var least = Math.Max(1, (long)Math.Ceiling(Math.Max(0, span.TotalSeconds) / (MaxPoints - 1)));

        foreach (var length in Lengths)
        {
            if (length >= least)
                return length;
        }

        const int DaySeconds = 86400;
        return (int)Math.Min(int.MaxValue, (least + DaySeconds - 1) / DaySeconds * DaySeconds);
    }

    /// <summary>
    /// Which step a moment falls in: whole steps since the Unix epoch. The same moment is always in
    /// the same step, whatever window it is read in.
    /// </summary>
    public static long StepOf(DateTimeOffset at, int stepSeconds)
        => (long)Math.Floor(at.ToUnixTimeMilliseconds() / 1000d / stepSeconds);
}
