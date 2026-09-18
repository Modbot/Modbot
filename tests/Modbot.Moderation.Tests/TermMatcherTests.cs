using Modbot.Moderation;
using Modbot.Core.Moderation;

namespace Modbot.Moderation.Tests;

/// <summary>Term matching (AI moderation design §4.1): no database, no network.</summary>
public class TermMatcherTests
{
    private static IReadOnlyList<TermHit> Check(string text, params StoredTerm[] terms)
        => TermMatcher.Check(TermMatcher.Compile(terms), text, ModerationTargets.DiscordMessage);

    [Fact]
    public void AWholeWordMatchesOnlyAsAWord()
    {
        var term = new StoredTerm("t1", TermKind.Word, Text: "ass");

        Assert.Single(Check("what an ass.", term));
        Assert.Empty(Check("a classic passage", term));
    }

    [Fact]
    public void ContainsMatchesInsideLongerWords()
    {
        var term = new StoredTerm("t1", TermKind.Contains, Text: "ass");

        var hit = Assert.Single(Check("a classic passage", term));
        Assert.Equal("t1", hit.TermKey);
    }

    [Fact]
    public void LookAlikeDigitsAccentsAndInvisibleCharactersStillMatch_AndTheQuoteIsThePersonsOwnWords()
    {
        var term = new StoredTerm("t1", TermKind.Word, Text: "free nitro");

        var hit = Assert.Single(Check("get FR​EE  N1TRÖ here", term));

        Assert.Equal("FR​EE  N1TRÖ", hit.Matched);
    }

    [Theory]
    [InlineData("ＦＲＥＥ nitro")]
    [InlineData("𝐟𝐫𝐞𝐞 nitro")]
    [InlineData("frее nitro")]
    [InlineData("frée nitro")]
    public void FullWidthFancyCyrillicAndCombiningLettersStillMatch(string text)
    {
        Assert.Single(Check(text, new StoredTerm("t1", TermKind.Word, Text: "free nitro")));
    }

    [Fact]
    public void APatternRunsAgainstLowerCaseText()
    {
        var term = new StoredTerm("t1", TermKind.Regex, Pattern: @"\bdiscord\.gg/\w+");

        var hit = Assert.Single(Check("Join DISCORD.GG/abc now", term));
        Assert.Equal("DISCORD.GG/abc", hit.Matched);
    }

    [Fact]
    public void APatternTheFasterEngineCannotRunStillWorks()
    {
        var term = new StoredTerm("t1", TermKind.Regex, Pattern: @"(\w)\1{3}");

        Assert.NotNull(TermMatcher.CompilePattern(term.Pattern!, out _));
        Assert.Single(Check("heyyyy", term));
    }

    [Fact]
    public void ABrokenPatternIsRefusedWithAReason()
    {
        Assert.Null(TermMatcher.CompilePattern("(unclosed", out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void ACombinationNeedsItsWordsCloseTogether_AndAnExcusePhraseCancelsIt()
    {
        var term = new StoredTerm(
            "c1", TermKind.Combination,
            AllOf: ["you"], AnyOf: ["kill", "stab"], NoneOf: ["murder mystery"], WithinWords: 3);

        Assert.Single(Check("I will kill you tonight", term));
        Assert.Empty(Check("kill the boss, then later maybe somebody else will find you", term));
        Assert.Empty(Check("in murder mystery I will kill you", term));
    }

    [Fact]
    public void ASwitchedOffTermDoesNotMatch()
    {
        var terms = new[]
        {
            new StoredTerm("keep", TermKind.Word, Text: "spam"),
            new StoredTerm("off", TermKind.Word, Text: "eggs"),
        };

        var hits = TermMatcher.Check(TermMatcher.Compile(terms, ["off"]), "spam and eggs", ModerationTargets.Bio);

        Assert.Equal(["keep"], hits.Select(h => h.TermKey));
    }

    [Fact]
    public void AHubRuleForDisplayNamesDoesNotRunOnChat()
    {
        var term = new StoredTerm("t1", TermKind.Word, Text: "moderator", Fields: ["displayName"]);
        var list = TermMatcher.Compile([term]);

        Assert.Empty(TermMatcher.Check(list, "ask a moderator", ModerationTargets.DiscordMessage));
        Assert.Single(TermMatcher.Check(list, "Official Moderator", ModerationTargets.DisplayName));
    }
}
