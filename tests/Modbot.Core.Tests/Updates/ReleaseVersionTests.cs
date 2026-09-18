namespace Modbot.Core.Tests.Updates;

/// <summary>
/// Comparing YYYY.M.PATCH, which is the thing that decides whether an operator is told to update.
/// </summary>
public class ReleaseVersionTests
{
    [Theory]
    [InlineData("2026.9.1", "2026.9.2")]
    [InlineData("2026.9.2", "2026.10.0")]
    [InlineData("2026.12.4", "2027.1.0")]
    // The reason this is not a string comparison: ordinal sorting puts 2026.9.10 before 2026.9.2.
    [InlineData("2026.9.2", "2026.9.10")]
    public void ALaterReleaseIsNewer(string running, string offered)
    {
        Assert.True(ReleaseVersion.IsNewer(running, offered));
    }

    [Fact]
    public void TheSameReleaseIsNotNewer()
    {
        Assert.False(ReleaseVersion.IsNewer("2026.9.1", "2026.9.1"));
    }

    /// <summary>
    /// Running something later than the newest published release — a build from master, or a
    /// release rolled back — is not an update, and the screen must not offer one.
    /// </summary>
    [Theory]
    [InlineData("2026.9.3", "2026.9.1")]
    [InlineData("2026.10.0", "2026.9.9")]
    [InlineData("2026.9.10", "2026.9.9")]
    public void SomethingNewerLocallyIsNotAnUpdate(string running, string offered)
    {
        Assert.False(ReleaseVersion.IsNewer(running, offered));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2026.9")]
    [InlineData("2026.9.1.2")]
    [InlineData("v2026.9.1")]
    [InlineData("2026.9.1-beta")]
    [InlineData("nightly")]
    public void AVersionNobodyCanReadIsNeverNewer(string? offered)
    {
        Assert.Null(ReleaseVersion.Parse(offered));
        Assert.False(ReleaseVersion.IsNewer("2026.9.1", offered));
        Assert.False(ReleaseVersion.IsNewer(offered, "2026.9.1"));
    }

    [Fact]
    public void TheThreeNumbersComeBackAsTheyWereWritten()
    {
        Assert.Equal((2026, 9, 1), ReleaseVersion.Parse("2026.9.1"));
        Assert.Equal((2026, 12, 0), ReleaseVersion.Parse(" 2026.12.0 "));
    }
}
