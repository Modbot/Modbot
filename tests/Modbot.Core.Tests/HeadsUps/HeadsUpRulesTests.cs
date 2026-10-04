using Modbot.Shared.HeadsUps;

namespace Modbot.Core.Tests.HeadsUps;

/// <summary>
/// What a heads-up may say: plain text, short, no links, and the right parts for its kind. The
/// server and the companion both read these rules.
/// </summary>
public class HeadsUpRulesTests
{
    [Theory]
    [InlineData("  need   backup  ", "need backup")]
    [InlineData("line\none", "line one")]
    [InlineData("a‮b", "ab")]
    [InlineData("zero​width", "zerowidth")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void CleaningTakesOutControlAndFormattingCharactersAndSquashesSpace(string? text, string? expected)
        => Assert.Equal(expected, HeadsUpRules.Clean(text));

    [Theory]
    [InlineData("see https://evil.example")]
    [InlineData("go to www.thing")]
    [InlineData("join discord.gg/abc")]
    [InlineData("bit.ly/xyz")]
    [InlineData("EVIL.COM now")]
    public void LinksAreFound(string text) => Assert.True(HeadsUpRules.HasLink(text));

    [Theory]
    [InlineData("need backup at the bar")]
    [InlineData("loud guy. be careful")]
    [InlineData("e.g. the tall one")]
    [InlineData("3.5 stars")]
    public void OrdinarySentencesAreNotLinks(string text) => Assert.False(HeadsUpRules.HasLink(text));

    [Fact]
    public void APinNeedsWords()
    {
        Assert.NotNull(HeadsUpRules.Problem(HeadsUpKind.Pin, null, null, null));
        Assert.Null(HeadsUpRules.Problem(HeadsUpKind.Pin, null, "event starts at 9", null));
    }

    [Fact]
    public void KeepAnEyeNeedsAPersonButNoWords()
    {
        Assert.NotNull(HeadsUpRules.Problem(HeadsUpKind.KeepAnEye, null, null, null));
        Assert.Null(HeadsUpRules.Problem(HeadsUpKind.KeepAnEye, "usr_1", null, null));
    }

    [Fact]
    public void AskForHelpNeedsAListedPlace()
    {
        Assert.NotNull(HeadsUpRules.Problem(HeadsUpKind.AskForHelp, null, null, null));
        Assert.NotNull(HeadsUpRules.Problem(HeadsUpKind.AskForHelp, null, null, "Rooftop"));
        Assert.Null(HeadsUpRules.Problem(HeadsUpKind.AskForHelp, null, null, "bar"));
        Assert.Equal("Bar", HeadsUpRules.Place(" bar "));
    }

    [Fact]
    public void TooLongAndLinksAreRefused()
    {
        Assert.NotNull(HeadsUpRules.Problem(HeadsUpKind.Message, null, new string('a', HeadsUpRules.MaxTextLength + 1), null));
        Assert.NotNull(HeadsUpRules.Problem(HeadsUpKind.Message, null, "look at evil.com", null));
        Assert.Null(HeadsUpRules.Problem(HeadsUpKind.Message, null, new string('a', HeadsUpRules.MaxTextLength), null));
    }

    [Theory]
    [InlineData(HeadsUpKind.Pin)]
    [InlineData(HeadsUpKind.KeepAnEye)]
    [InlineData(HeadsUpKind.Message)]
    [InlineData(HeadsUpKind.AskForHelp)]
    public void EveryKindGoesThereAndBackOnTheWire(HeadsUpKind kind)
        => Assert.Equal(kind, HeadsUpRules.Parse(HeadsUpRules.Word(kind)));

    [Fact]
    public void AnUnknownWordIsNoKind() => Assert.Null(HeadsUpRules.Parse("shout"));
}
