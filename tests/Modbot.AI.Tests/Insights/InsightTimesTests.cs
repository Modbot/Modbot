using Modbot.AI.Insights;
using Modbot.Core.Data.Entities;
using NodaTime;

namespace Modbot.AI.Tests.Insights;

/// <summary>When a schedule names a moment, and which days an insight covers.</summary>
public class InsightTimesTests
{
    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0)
        => new(2029, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void ADailyScheduleWhoseTimeHasNotComeTodayNamesYesterday()
    {
        Assert.Equal(Utc(3, 11, 9), InsightTimes.LatestMoment(Utc(3, 12, 8, 59), InsightKinds.EveryDay, 9, 0, DateTimeZone.Utc));
        Assert.Equal(Utc(3, 12, 9), InsightTimes.LatestMoment(Utc(3, 12, 9), InsightKinds.EveryDay, 9, 0, DateTimeZone.Utc));
    }

    [Fact]
    public void AWeeklyScheduleNamesTheLatestOfItsWeekday()
    {
        // 12 March 2029 is a Monday.
        Assert.Equal(Utc(3, 12, 9), InsightTimes.LatestMoment(Utc(3, 14, 12), InsightKinds.EveryWeek, 9, 1, DateTimeZone.Utc));
        Assert.Equal(Utc(3, 5, 9), InsightTimes.LatestMoment(Utc(3, 12, 8), InsightKinds.EveryWeek, 9, 1, DateTimeZone.Utc));
        Assert.Equal(DayOfWeek.Monday, Utc(3, 12, 9).DayOfWeek);
    }

    [Fact]
    public void TheHourIsInTheScheduleTimeZone_AcrossTheClocksChanging()
    {
        var chicago = InsightTimes.FindTimeZone("America/Chicago");
        Assert.NotNull(chicago);

        // Clocks in Chicago go forward on 11 March 2029: 9 am is 15:00 UTC before, 14:00 after.
        Assert.Equal(Utc(3, 9, 15), InsightTimes.LatestMoment(Utc(3, 9, 16), InsightKinds.EveryDay, 9, 0, chicago));
        Assert.Equal(Utc(3, 12, 14), InsightTimes.LatestMoment(Utc(3, 12, 15), InsightKinds.EveryDay, 9, 0, chicago));
    }

    /// <summary>2 am does not exist in Chicago on the morning the clocks go forward; 3 am is used.</summary>
    [Fact]
    public void AnHourThatDoesNotExistThatDayIsTakenAsTheHourAfter()
    {
        var chicago = InsightTimes.FindTimeZone("America/Chicago")!;

        Assert.Equal(Utc(3, 11, 8), InsightTimes.LatestMoment(Utc(3, 11, 12), InsightKinds.EveryDay, 2, 0, chicago));
    }

    [Fact]
    public void OnlyRealTimeZoneNamesAreFound()
    {
        Assert.NotNull(InsightTimes.FindTimeZone("Europe/London"));
        Assert.Null(InsightTimes.FindTimeZone("Not/AZone"));
        Assert.Null(InsightTimes.FindTimeZone(""));
        Assert.Same(DateTimeZone.Utc, InsightTimes.ZoneOrUtc("Not/AZone"));
    }

    [Fact]
    public void AnInsightCoversWholeDaysEndingYesterday_AndTheSameLengthBefore()
    {
        var today = new DateOnly(2029, 3, 15);

        Assert.Equal(
            new InsightPeriod(new(2029, 3, 8), new(2029, 3, 14), new(2029, 3, 1), new(2029, 3, 7)),
            InsightPeriod.Ending(today, InsightKinds.EveryWeek));

        Assert.Equal(
            new InsightPeriod(new(2029, 3, 14), new(2029, 3, 14), new(2029, 3, 13), new(2029, 3, 13)),
            InsightPeriod.Ending(today, InsightKinds.EveryDay));
    }
}
