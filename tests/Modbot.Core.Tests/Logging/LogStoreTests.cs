using Modbot.Core.Logging.Store;

namespace Modbot.Core.Tests.Logging;

public class LogStoreTests
{
    [Fact]
    public void SixMonthsIsTheDefaultWindow()
    {
        Assert.Equal(180, LogStore.DefaultRetentionDays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(180)]
    [InlineData(3650)]
    public void AWindowInsideTheBoundsIsAccepted(int days)
    {
        Assert.True(LogStore.IsValidRetention(days));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3651)]
    public void AWindowOutsideTheBoundsIsRefused(int days)
    {
        Assert.False(LogStore.IsValidRetention(days));
    }

    /// <summary>
    /// The second limit, added when the table started following <c>LOG_LEVEL</c> (2026-09-18).
    /// Keep-for is a promise about time and an operator may set it to "forever"; the ceiling is
    /// about the disk and nobody can turn it off.
    /// </summary>
    [Fact]
    public void TheTableHasACeilingOnLinesAsWellAsOnDays()
    {
        Assert.Equal(2_000_000, LogStore.MaxLines);
    }

    [Fact]
    public void TheCeilingIsDeletedInSlicesLikeEverythingElse()
    {
        Assert.True(LogStore.MaxLines > LogStore.DeleteSlice);
    }

    [Fact]
    public void OneRunCountsTheTwoReasonsSeparately()
    {
        var pruned = new LogPrune(PastTheWindow: 40, OverTheCeiling: 2);

        Assert.Equal(42, pruned.Total);
    }

    [Theory]
    [InlineData("password", true)]
    [InlineData("SmtpPasswordEncrypted", true)]
    [InlineData("ApiKeyPrefix", true)]
    [InlineData("SessionId", true)]
    [InlineData("Count", false)]
    [InlineData("SubjectId", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ANameIsASecretOrItIsNot(string? name, bool secret)
    {
        Assert.Equal(secret, LogSecrets.IsSecretName(name));
    }

    [Fact]
    public void ScrubbingLeavesAnOrdinaryLineAlone()
    {
        const string line = "Stored 42 facts for grp_1234 in 180 ms";

        Assert.Equal(line, LogSecrets.Scrub(line));
    }
}
