using Modbot.Core.Users;

namespace Modbot.Core.Tests.Users;

/// <summary>
/// What a username may be made of (username rules and deleting accounts design §2). Pure: no
/// database, no host.
/// </summary>
public class UsernameRulesTests
{
    [Theory]
    [InlineData("alice")]
    [InlineData("Alice")]
    [InlineData("ALICE")]
    [InlineData("alice_smith")]
    [InlineData("_alice")]
    [InlineData("alice99")]
    [InlineData("99")]
    [InlineData("_")]
    [InlineData("a")]
    public void LettersNumbersAndUnderscoresAreAccepted(string value)
        => Assert.True(UsernameRules.LooksLike(value));

    [Theory]
    [InlineData("alice smith")]              // a space
    [InlineData("alice.smith")]              // a dot
    [InlineData("alice-smith")]              // a dash
    [InlineData("alice@example.com")]        // an address in the username box
    [InlineData("alice!")]                   // punctuation
    [InlineData("alice#1234")]               // a Discord tag
    [InlineData("alice/bob")]                // a slash
    [InlineData("renée")]                    // an accent
    [InlineData("аlice")]                    // a Cyrillic а, which reads as a Latin one
    [InlineData("アリス")]                     // another script
    [InlineData("alice​")]              // a zero-width space
    [InlineData("al\tice")]                  // an inner tab; the trim only takes the ends
    public void EverythingElseIsRefused(string value)
    {
        Assert.False(UsernameRules.LooksLike(value));
        Assert.Equal(UsernameRules.WrongCharacters, UsernameRules.Validate(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingTypedIsAskedFor(string? value)
        => Assert.Equal("A username is required.", UsernameRules.Validate(value));

    [Fact]
    public void AnythingLongerThanTheColumnIsRefused()
    {
        var tooLong = new string('a', UsernameRules.MaximumLength + 1);

        Assert.Contains("longer than", UsernameRules.Validate(tooLong), StringComparison.Ordinal);
        Assert.True(UsernameRules.LooksLike(new string('a', UsernameRules.MaximumLength)));
    }

    /// <summary>The rule reads the trimmed name, because the trimmed name is what gets stored.</summary>
    [Fact]
    public void SpacesAtTheEndsAreTidiedAwayRatherThanRefused()
    {
        Assert.True(UsernameRules.LooksLike("  alice  "));
        Assert.False(UsernameRules.LooksLike("  al ice  "));
    }

    /// <summary>A moderator gets a sentence, not a regular expression.</summary>
    [Fact]
    public void TheComplaintIsInPlainWords()
    {
        Assert.Equal("A username can only have letters, numbers and underscores in it.", UsernameRules.WrongCharacters);
        Assert.DoesNotContain('[', UsernameRules.WrongCharacters);
    }
}
