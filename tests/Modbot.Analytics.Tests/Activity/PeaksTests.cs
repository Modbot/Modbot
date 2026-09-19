using Modbot.Analytics.Activity;

namespace Modbot.Analytics.Tests.Activity;

/// <summary>
/// The picking rules behind every peak on the Instances page.
/// </summary>
/// <remarks>
/// No database: choosing the highest out of a set of day rows is arithmetic, and the rules that
/// make it repeatable — earliest wins a tie, nought is not a peak, nothing recorded answers null —
/// are exactly the ones a reader would otherwise have to take on trust.
/// </remarks>
public class PeaksTests
{
    private static readonly DateOnly Day1 = new(2026, 9, 14);
    private static readonly DateOnly Day2 = new(2026, 9, 15);
    private static readonly DateOnly Day3 = new(2026, 9, 16);

    private static DateTimeOffset At(DateOnly day, int hour, int minute = 0)
        => new(day.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.Zero);

    /// <summary>A day row with only the numbers a test cares about set, and the rest consistent.</summary>
    private static ActivityDay Day(
        DateOnly day,
        int mostPeople,
        int peopleAtHour,
        decimal peopleMinutes,
        int mostInstances = 1,
        int instancesAtHour = 19,
        decimal bestHourMinutes = 0m,
        int bestHour = 20,
        int bestHourMostPeople = 0) => new(
            day,
            mostPeople,
            At(day, peopleAtHour),
            mostInstances,
            At(day, instancesAtHour),
            peopleMinutes,
            At(day, bestHour),
            bestHourMinutes == 0m ? peopleMinutes : bestHourMinutes,
            bestHourMostPeople == 0 ? mostPeople : bestHourMostPeople);

    [Fact]
    public void MostPeople_IsTheHighest_AndCarriesWhenItHappened()
    {
        var days = new[]
        {
            Day(Day1, mostPeople: 12, peopleAtHour: 20, peopleMinutes: 300m),
            Day(Day2, mostPeople: 48, peopleAtHour: 21, peopleMinutes: 900m),
            Day(Day3, mostPeople: 31, peopleAtHour: 19, peopleMinutes: 600m),
        };

        var peak = Peaks.MostPeople(days);

        Assert.NotNull(peak);
        Assert.Equal(48, peak.Value);
        Assert.Equal(At(Day2, 21), peak.At);
    }

    /// <summary>
    /// Two evenings that both reached the same height are ordinary. Without a rule the page would
    /// name whichever the database happened to return first, and move between two loads with no new
    /// data behind it.
    /// </summary>
    [Fact]
    public void MostPeople_BreaksATieTowardsTheEarliestMoment()
    {
        var days = new[]
        {
            Day(Day3, mostPeople: 40, peopleAtHour: 18, peopleMinutes: 100m),
            Day(Day1, mostPeople: 40, peopleAtHour: 22, peopleMinutes: 100m),
            Day(Day2, mostPeople: 40, peopleAtHour: 9, peopleMinutes: 100m),
        };

        var peak = Peaks.MostPeople(days);

        Assert.NotNull(peak);
        Assert.Equal(At(Day1, 22), peak.At);
    }

    [Fact]
    public void MostInstances_IsTheHighest_AndCarriesItsOwnMoment()
    {
        var days = new[]
        {
            Day(Day1, mostPeople: 5, peopleAtHour: 20, peopleMinutes: 50m, mostInstances: 1, instancesAtHour: 20),
            Day(Day2, mostPeople: 5, peopleAtHour: 20, peopleMinutes: 50m, mostInstances: 4, instancesAtHour: 17),
        };

        var peak = Peaks.MostInstances(days);

        Assert.NotNull(peak);
        Assert.Equal(4, peak.Value);
        Assert.Equal(At(Day2, 17), peak.At);
    }

    /// <summary>
    /// Busiest is people-minutes, not the peak: a night that touched forty for five minutes is not
    /// a busier night than one that held twenty for six hours.
    /// </summary>
    [Fact]
    public void Busiest_IsPeopleMinutes_AndNotTheHighestPeak()
    {
        var days = new[]
        {
            Day(Day1, mostPeople: 40, peopleAtHour: 20, peopleMinutes: 200m),
            Day(Day2, mostPeople: 20, peopleAtHour: 20, peopleMinutes: 7200m),
        };

        var busiest = Peaks.Busiest(days);

        Assert.NotNull(busiest);
        Assert.Equal(Day2, busiest.Day);
        Assert.Equal(7200m, busiest.PeopleMinutes);
        Assert.Equal(20, busiest.MostPeopleAtOnce);
    }

    [Fact]
    public void Busiest_BreaksATieTowardsTheEarlierDay()
    {
        var days = new[]
        {
            Day(Day3, mostPeople: 10, peopleAtHour: 20, peopleMinutes: 500m),
            Day(Day1, mostPeople: 10, peopleAtHour: 20, peopleMinutes: 500m),
        };

        Assert.Equal(Day1, Peaks.Busiest(days)!.Day);
    }

    /// <summary>
    /// The window's busiest hour is the best of the days' own busiest hours, which is what keeps the
    /// answer to a few hundred rows rather than one row per hour in the range.
    /// </summary>
    [Fact]
    public void BusiestHour_IsTheBestOfTheDaysOwnBestHours()
    {
        var days = new[]
        {
            Day(Day1, mostPeople: 10, peopleAtHour: 20, peopleMinutes: 900m, bestHourMinutes: 300m, bestHour: 20, bestHourMostPeople: 9),
            Day(Day2, mostPeople: 12, peopleAtHour: 21, peopleMinutes: 400m, bestHourMinutes: 380m, bestHour: 22, bestHourMostPeople: 12),
        };

        var hour = Peaks.BusiestHour(days);

        Assert.NotNull(hour);
        Assert.Equal(At(Day2, 22), hour.StartedAt);
        Assert.Equal(380m, hour.PeopleMinutes);
        Assert.Equal(12, hour.MostPeopleAtOnce);
    }

    [Fact]
    public void BusiestHour_BreaksATieTowardsTheEarliestHour()
    {
        var days = new[]
        {
            Day(Day2, mostPeople: 10, peopleAtHour: 20, peopleMinutes: 100m, bestHourMinutes: 100m, bestHour: 18),
            Day(Day1, mostPeople: 10, peopleAtHour: 20, peopleMinutes: 100m, bestHourMinutes: 100m, bestHour: 23),
        };

        Assert.Equal(At(Day1, 23), Peaks.BusiestHour(days)!.StartedAt);
    }

    /// <summary>
    /// A range with nothing in it answers "nothing recorded", not "the peak was nought" — and
    /// certainly not an exception. The two statements are different and only the first is true.
    /// </summary>
    [Fact]
    public void NothingRecorded_AnswersNull_RatherThanZeroOrThrowing()
    {
        Assert.Null(Peaks.MostPeople([]));
        Assert.Null(Peaks.MostInstances([]));
        Assert.Null(Peaks.Busiest([]));
        Assert.Null(Peaks.BusiestHour([]));
    }

    /// <summary>
    /// An evening Modbot counted all the way through and that nobody attended is a real record of an
    /// empty evening. "0 people at 19:04" is true and says nothing, so it is not a peak.
    /// </summary>
    [Fact]
    public void ADayThatNeverRoseAboveNought_IsNotAPeak()
    {
        var days = new[] { Day(Day1, mostPeople: 0, peopleAtHour: 19, peopleMinutes: 0m, mostInstances: 0) };

        Assert.Null(Peaks.MostPeople(days));
        Assert.Null(Peaks.MostInstances(days));
        Assert.Null(Peaks.Busiest(days));
        Assert.Null(Peaks.BusiestHour(days));
    }
}
