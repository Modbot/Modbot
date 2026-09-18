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
    [InlineData("a_b", @"a\_b")]
    [InlineData("<@123>", @"\<@123>")]
    public void AName_HasItsMarkdownEscaped(string name, string expected)
        => Assert.Equal(expected, CardText.EscapeName(name));

    /// <summary>
    /// Neither is markdown, and a backslash in front of one is a backslash a moderator sees the
    /// moment the text is not drawn as markdown. A name spelled like a mention is text because
    /// mentions are off on every message the bot sends (Discord embeds design §4).
    /// </summary>
    [Theory]
    [InlineData("@everyone")]
    [InlineData("Banned at 10:15")]
    [InlineData("E-Ray")]
    public void AName_KeepsWhatDiscordDoesNotReadAsFormatting(string name)
        => Assert.Equal(name, CardText.EscapeName(name));

    /// <summary>
    /// A heading, a quote and a list start at the start of a line and nowhere else, so that is the
    /// only place they are escaped -- otherwise every hyphenated name in the group carries a
    /// backslash. A name has one line start, its own, because its control characters are gone.
    /// </summary>
    [Theory]
    [InlineData("- item", @"\- item")]
    [InlineData("# Heading", @"\# Heading")]
    [InlineData("> quote", @"\> quote")]
    [InlineData("  - item", @"\- item")]
    [InlineData("Ada - Rin", "Ada - Rin")]
    public void AName_EscapesAListOrAHeadingOnlyWhereALineCouldStart(string name, string expected)
        => Assert.Equal(expected, CardText.EscapeName(name));

    /// <summary>
    /// The change that mattered: a name goes inside <c>[…](…)</c> now, so a bracket in one has to
    /// be escaped or it ends the link and puts the address on screen.
    /// </summary>
    [Fact]
    public void AName_HasItsBracketsEscaped()
        => Assert.Equal(@"ada\]\(x\)", CardText.EscapeName("ada](x)"));

    /// <summary>
    /// The line breaks are what made the <c>#</c> dangerous, and they are gone: it is left as
    /// written because there is no longer a line for it to head.
    /// </summary>
    [Fact]
    public void AName_LosesItsControlCharacters()
        => Assert.Equal("Ada   # Heading", CardText.EscapeName("Ada\n\r # Heading"));

    /// <summary>
    /// Free text is a sentence somebody wrote to be read. Escaping every colon and hyphen in one
    /// would put backslashes through the middle of it, so both are left alone (Discord embeds
    /// design §2.3) -- but the brackets still go, because a description can sit beside a link.
    /// </summary>
    [Fact]
    public void FreeText_KeepsItsPunctuationAndLosesItsMarkdown()
    {
        Assert.Equal(
            @"Banned at 10:15 - see #rules \[again\]",
            CardText.EscapeText("Banned at 10:15 - see #rules [again]"));
    }

    /// <summary>
    /// Free text keeps its line breaks, so unlike a name it has a line start after every one of
    /// them -- and a reason typed as a list is a list the reader did not ask for.
    /// </summary>
    [Fact]
    public void FreeText_EscapesAListOrAHeadingOnEveryLineItStarts()
    {
        Assert.Equal(
            "Two reasons:\n\\- one\n\\> two",
            CardText.EscapeText("Two reasons:\n- one\n> two"));
    }

    /// <summary>
    /// A reason is a paragraph rather than a link's label, so its parentheses are left as typed.
    /// With the square brackets escaped there is nothing for one to close.
    /// </summary>
    [Fact]
    public void FreeText_KeepsItsParenthesesAndBreaksItsMentions()
    {
        Assert.Equal(
            @"Told them (twice) to stop pinging \<@123>",
            CardText.EscapeText("Told them (twice) to stop pinging <@123>"));
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
