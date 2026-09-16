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
