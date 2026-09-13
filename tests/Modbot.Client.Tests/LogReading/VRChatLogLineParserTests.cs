using Modbot.Client.LogReading;

namespace Modbot.Client.Tests.LogReading;

public class VRChatLogLineParserTests
{
    [Fact]
    public void ParsesTheStandardEnvelope()
    {
        var ok = VRChatLogLineParser.TryParse(
            "2026.09.03 20:27:14 Debug      -  [Behaviour] OnPlayerJoined bin¹ (usr_f2049d71)",
            out var line);

        Assert.True(ok);
        Assert.Equal(new DateTime(2026, 9, 3, 20, 27, 14, DateTimeKind.Unspecified), line.Timestamp);
        Assert.Equal("Debug", line.Level);
        Assert.Equal("Behaviour", line.Tag);
        Assert.Equal("OnPlayerJoined bin¹ (usr_f2049d71)", line.Message);
    }

    [Fact]
    public void TimestampsAreUnspecifiedBecauseTheLogCarriesNoOffset()
    {
        Assert.True(VRChatLogLineParser.TryParse(
            "2026.09.03 20:27:14 Debug      -  [Behaviour] anything", out var line));

        Assert.Equal(DateTimeKind.Unspecified, line.Timestamp.Kind);
    }

    [Theory]
    [InlineData("    VRChat Build: 2026.2.3p2-1866-3a6adf31e2-Release")]
    [InlineData("")]
    [InlineData("General Settings:")]
    [InlineData("2026.09.03")]
    public void ContinuationLinesAreRejectedRatherThanTreatedAsCorrupt(string raw)
    {
        // Some records wrap across several lines; research doc section 5.3. A wrapped line is not a
        // parse failure worth alarming about, it is simply not a record start.
        Assert.False(VRChatLogLineParser.TryParse(raw, out _));
    }

    [Fact]
    public void KeepsTagLessMessagesWithoutInventingATag()
    {
        Assert.True(VRChatLogLineParser.TryParse(
            "2026.09.03 21:30:33 Debug      -  uSpeak [2][-1566331307]: OnDestroy", out var line));

        Assert.Null(line.Tag);
        Assert.Equal("uSpeak [2][-1566331307]: OnDestroy", line.Message);
    }

    [Fact]
    public void KeepsMarkupInsideTheMessage()
    {
        Assert.True(VRChatLogLineParser.TryParse(
            "2026.09.03 20:45:28 Debug      -  [Behaviour] <color=red>Resetting game flow</color>",
            out var line));

        Assert.Equal("Behaviour", line.Tag);
        Assert.Equal("<color=red>Resetting game flow</color>", line.Message);
    }

    [Fact]
    public void WarningAndErrorLevelsParseToo()
    {
        Assert.True(VRChatLogLineParser.TryParse(
            "2026.09.03 20:27:14 Warning    -  [Behaviour] something odd", out var line));

        Assert.Equal("Warning", line.Level);
    }
}
