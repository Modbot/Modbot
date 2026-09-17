using Modbot.Companion.Time;

namespace Modbot.Companion.Tests.Time;

public class LogTimestampConverterTests
{
    /// <summary>
    /// The zone the fixture session was recorded in, built explicitly rather than looked up:
    /// the projects run with InvariantGlobalization, and a test that depends on the host's
    /// timezone database passes or fails for reasons that have nothing to do with Modbot.
    /// </summary>
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.CreateCustomTimeZone(
        "Test/Central",
        TimeSpan.FromHours(-6),
        "Central",
        "CST",
        "CDT",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date,
                DateTime.MaxValue.Date,
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                    new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                    new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)),
        ]);

    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.CreateCustomTimeZone(
        "Test/Tokyo", TimeSpan.FromHours(9), "Japan", "JST");

    [Fact]
    public void MatchesTheOnlyAbsoluteTimeInTheRealLog()
    {
        // The fixture session was recorded in America/Chicago. An invite notification elsewhere in
        // the untrimmed log carries the same moment in UTC -- local 20:31:55 is 01:31:55 UTC the
        // next day -- so this conversion is checkable against VRChat's own arithmetic rather than
        // against an assumption.
        var converter = new LogTimestampConverter(Chicago);

        var instant = converter.ToInstant(new DateTime(2026, 9, 3, 20, 31, 55));

        Assert.Equal(new DateTimeOffset(2026, 9, 4, 1, 31, 55, TimeSpan.Zero), instant.ToUniversalTime());
    }

    [Fact]
    public void AppliesStandardTimeOutsideDaylightSaving()
    {
        var converter = new LogTimestampConverter(Chicago);

        var instant = converter.ToInstant(new DateTime(2026, 1, 15, 12, 0, 0));

        Assert.Equal(TimeSpan.FromHours(-6), instant.Offset);
    }

    [Fact]
    public void ResolvesTheHourThatHappensTwice()
    {
        // 01:30 on the autumn transition is two different instants. The log cannot say which, so
        // the first pass is taken and the error is bounded to one hour rather than unbounded.
        var converter = new LogTimestampConverter(Chicago);

        var instant = converter.ToInstant(new DateTime(2026, 11, 1, 1, 30, 0));

        Assert.Equal(TimeSpan.FromHours(-5), instant.Offset);
    }

    [Fact]
    public void ResolvesTheHourThatNeverHappened()
    {
        // 02:30 on the spring transition does not exist. VRChat can still write it if the machine's
        // clock was adjusted mid-session, and throwing would stop presence reporting over an hour
        // that happens twice a year.
        var converter = new LogTimestampConverter(Chicago);

        var instant = converter.ToInstant(new DateTime(2026, 3, 8, 2, 30, 0));

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 8, 30, 0, TimeSpan.Zero), instant.ToUniversalTime());
    }

    [Fact]
    public void TwoModeratorsInDifferentTimezonesAgreeOnTheInstant()
    {
        // The same wall-clock text in two places is two different moments. This is exactly the
        // hazard that makes a cross-client deduplication window possible or impossible.
        var chicago = new LogTimestampConverter(Chicago);
        var tokyo = new LogTimestampConverter(Tokyo);

        var sameText = new DateTime(2026, 9, 3, 20, 27, 14);

        Assert.NotEqual(chicago.ToInstant(sameText), tokyo.ToInstant(sameText));
        Assert.Equal(TimeSpan.FromHours(14), chicago.ToInstant(sameText) - tokyo.ToInstant(sameText));
    }
}
