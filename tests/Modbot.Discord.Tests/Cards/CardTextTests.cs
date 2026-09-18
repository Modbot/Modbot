using Modbot.Discord.Cards;

namespace Modbot.Discord.Tests.Cards;

/// <summary>
/// Making text a card did not write inert, and cutting it to Discord's limits. Three passes,
/// because an embed has three kinds of slot and treating them alike breaks one of them.
/// </summary>
public class CardTextTests
{
    [Theory]
    [InlineData("Ada", "Ada")]
    [InlineData("**bold**", @"\*\*bold\*\*")]
    [InlineData("@everyone", @"\@everyone")]
    [InlineData("a_b", @"a\_b")]
    public void AName_HasItsMarkdownEscaped(string name, string expected)
        => Assert.Equal(expected, CardText.EscapeName(name));

    /// <summary>
    /// The change that mattered: a name goes inside <c>[…](…)</c> now, so a bracket in one has to
    /// be escaped or it ends the link and puts the address on screen.
    /// </summary>
    [Fact]
    public void AName_HasItsBracketsEscaped()
        => Assert.Equal(@"ada\]\(x\)", CardText.EscapeName("ada](x)"));

    [Fact]
    public void AName_LosesItsControlCharacters()
        => Assert.Equal(@"Ada   \# Heading", CardText.EscapeName("Ada\n\r # Heading"));

    /// <summary>
    /// Free text is a sentence somebody wrote to be read. Escaping every colon and hyphen in one
    /// would put backslashes through the middle of it, so this pass is lighter than a name's --
    /// but the brackets still go, because a description can sit beside a link.
    /// </summary>
    [Fact]
    public void FreeText_KeepsItsPunctuationAndLosesItsMarkdown()
    {
        Assert.Equal(
            @"Banned at 10:15 \- see \#rules \[again\]",
            CardText.EscapeText("Banned at 10:15 - see #rules [again]"));
    }

    /// <summary>
    /// A title, an author line and a footer are printed exactly as given, so escaping one leaves
    /// the backslashes on screen -- a person called <c>*nova*</c> would have read as
    /// <c>\*nova\*</c> in the title of their own card.
    /// </summary>
    [Fact]
    public void APlainSlot_IsNotEscapedAtAll()
        => Assert.Equal("*nova*", CardText.Plain("*nova*", 256));

    [Fact]
    public void APlainSlot_LosesItsControlCharactersAndItsEdges()
        => Assert.Equal("Ada   Heading", CardText.Plain("  Ada\n\r Heading  ", 256));

    [Fact]
    public void APlainSlot_IsCutToItsLimit()
    {
        var cut = CardText.Plain(new string('a', 500), 256);

        Assert.Equal(256, cut.Length);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);
    }

    [Fact]
    public void TextShorterThanTheLimitIsLeftAlone()
        => Assert.Equal("Ada", CardText.Fit("Ada", 100));

    /// <summary>
    /// A cut that landed between a backslash and what it escapes would turn the ellipsis into the
    /// thing being escaped, so the backslash goes with it.
    /// </summary>
    [Fact]
    public void ACutNeverLeavesADanglingBackslash()
    {
        var cut = CardText.Fit(@"abc\*def", 5);

        Assert.Equal("abc…", cut);
        Assert.DoesNotContain(@"\", cut, StringComparison.Ordinal);
    }
}
