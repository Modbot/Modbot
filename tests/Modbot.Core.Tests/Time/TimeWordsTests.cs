using Modbot.Core.Time;

namespace Modbot.Core.Tests.Time;

/// <summary>
/// The server writes lengths and ages exactly as the web app's <c>lengthOfTime</c> and <c>ago</c>
/// do (<c>tests/format.test.ts</c> there), so the cases here are the same numbers.
/// </summary>
public class TimeWordsTests
{
    [Theory]
    [InlineData(0, "0s")]
    [InlineData(-5, "0s")]
    [InlineData(45, "45s")]
    [InlineData(59.6, "1m")]
    [InlineData(16 * 60, "16m")]
    [InlineData(186 * 60, "3h 6m")]
    [InlineData(180 * 60, "3h")]
    [InlineData(59.7 * 60, "1h")]
    [InlineData(52 * 3600, "2d 4h")]
    [InlineData(48 * 3600, "2d")]
    [InlineData(10 * 86400, "10d")]
    [InlineData(30 * 86400, "30d")]
    [InlineData(30.44 * 86400, "1mth")]
    [InlineData(31 * 86400, "1mth 1d")]
    [InlineData(45 * 86400, "1mth 15d")]
    [InlineData(61 * 86400, "2mth")]
    [InlineData(90 * 86400, "3mth")]
    [InlineData(180 * 86400, "6mth")]
    [InlineData(365 * 86400, "1y")]
    [InlineData(430 * 86400, "1y 2mth")]
    public void ALengthReadsInSixUnits_TwoAtMost(double seconds, string words)
    {
        Assert.Equal(words, TimeWords.Length(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(-5, "0s")]
    [InlineData(30.4, "30s")]
    [InlineData(30 * 60, "30m")]
    [InlineData(90, "2m")]
    [InlineData(6 * 3600, "6h")]
    [InlineData(30 * 86400, "30d")]
    [InlineData(44 * 86400, "44d")]
    [InlineData(60 * 86400, "1mth")]
    [InlineData(180 * 86400, "5mth")]
    [InlineData(365 * 86400, "1y")]
    [InlineData(730 * 86400, "2y")]
    public void AnAgeIsOneUnit_RoundedToWholeSecondsFirst(double seconds, string words)
    {
        Assert.Equal(words, TimeWords.Age(TimeSpan.FromSeconds(seconds)));
    }
}
