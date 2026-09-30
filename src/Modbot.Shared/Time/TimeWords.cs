namespace Modbot.Core.Time;

/// <summary>
/// A length of time or an age, in the six units everything Modbot writes for a person uses:
/// y, mth, d, h, m, s.
/// </summary>
/// <remarks>
/// <para>
/// The web app writes lengths and ages the same way (<c>lengthOfTime</c> and <c>ago</c> in its
/// <c>format.ts</c>), and this is the one copy for a Discord card, an alert, an overlay line or a
/// case file, so a moderator reads "4h 40m" everywhere and never "4 h 40 min" in one place and
/// "4.7 hours" in another. There are no weeks: a ten-day length is "10d".
/// </para>
/// <para>
/// The unit sits against its number, one space parts two units, and the smaller is left out when it
/// is zero: "4h 40m", "3h". Never a decimal, because "3.1h" makes the reader work out that .1 of an
/// hour is six minutes. A month is 30.44 days and a year 365, as in the web app.
/// </para>
/// </remarks>
public static class TimeWords
{
    private const double DaysInMonth = 30.44;
    private const int DaysInYear = 365;

    /// <summary>
    /// A length, two units at most: "45s", "16m", "4h 40m", "2d 4h", "1mth 3d", "1y 2mth".
    /// </summary>
    /// <remarks>
    /// Rounded to the second under a minute, to the minute under a day, to the hour under a month
    /// and to the day past it, before the unit is chosen, so 59.7 minutes reads "1h" and not "60m".
    /// From one month (30.44 days) on it is the nearest month, not the month begun, so a 90-day
    /// setting reads "3mth" and not "2mth 29d". A negative span reads "0s", as it does in the app.
    /// Step for step the same as <c>lengthOfTime</c> in the web app's <c>format.ts</c>.
    /// </remarks>
    public static string Length(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        var seconds = Whole(span.TotalSeconds);
        if (seconds < 60)
            return Units(seconds, "s");

        var minutes = Whole(span.TotalMinutes);
        if (minutes < 60)
            return Units(minutes, "m");
        if (minutes < 24 * 60)
            return Units(minutes / 60, "h", minutes % 60, "m");

        var days = minutes / (24d * 60);
        if (days < DaysInMonth)
        {
            var hours = Whole(span.TotalHours);
            return Units(hours / 24, "d", hours % 24, "h");
        }

        var wholeDays = Whole(days);
        if (wholeDays < DaysInYear)
        {
            var months = Whole(days / DaysInMonth);
            if (months >= 12)
                return Units(1, "y");
            return Units(months, "mth", Math.Max(0, Whole(days - months * DaysInMonth)), "d");
        }

        var years = wholeDays / DaysInYear;
        var restMonths = Whole((wholeDays - years * DaysInYear) / DaysInMonth);
        return restMonths >= 12 ? Units(years + 1, "y") : Units(years, "y", restMonths, "mth");
    }

    /// <summary>
    /// An age, in the one largest unit that still reads at a glance: "40s", "12m", "5h", "30d",
    /// "3mth", "1y". Days up to 45, because "38d" is easier to picture than "1mth"; past that,
    /// whole months and then whole years that have passed, the way people say an age.
    /// </summary>
    public static string Age(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        var seconds = Whole(span.TotalSeconds);
        if (seconds < 60)
            return Units(seconds, "s");
        if (seconds < 3600)
            return Units(Whole(span.TotalMinutes), "m");
        if (seconds < 86_400)
            return Units(Whole(span.TotalHours), "h");

        var days = Whole(span.TotalDays);
        if (days < 45)
            return Units(days, "d");
        if (days < DaysInYear)
            return Units((long)Math.Floor(days / DaysInMonth), "mth");
        return Units(days / DaysInYear, "y");
    }

    /// <summary>Rounded the way the web app's <c>Math.round</c> rounds: a half goes up, not to the even number.</summary>
    private static long Whole(double value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);

    private static string Units(long big, string bigUnit, long small = 0, string smallUnit = "")
        => small > 0 ? $"{big}{bigUnit} {small}{smallUnit}" : $"{big}{bigUnit}";
}
