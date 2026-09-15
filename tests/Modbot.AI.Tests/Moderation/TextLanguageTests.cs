using Modbot.AI.Moderation;

namespace Modbot.AI.Tests.Moderation;

/// <summary>
/// The language marked on every flag (AI moderation design §18). Offline: no request goes anywhere.
/// </summary>
public class TextLanguageTests
{
    private readonly TextLanguage _language = new();

    [Theory]
    [InlineData("Please stop posting that link in this channel, it is a scam.", "eng")]
    [InlineData("Пожалуйста, перестаньте публиковать эту ссылку, это мошенничество.", "rus")]
    [InlineData("Por favor, deja de publicar ese enlace, es una estafa.", "spa")]
    public void TheLanguageOfASentenceIsWorkedOut(string text, string expected)
        => Assert.Equal(expected, _language.Of(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("lol")]
    [InlineData("ok thanks")]
    [InlineData(null)]
    public void TextTooShortToTellIsLeftUnknown(string? text)
        => Assert.Null(_language.Of(text));

    [Fact]
    public void AVeryLongTextIsStillAnswered()
    {
        var text = string.Join(' ', Enumerable.Repeat("the quick brown fox jumps over the lazy dog", 400));

        Assert.Equal("eng", _language.Of(text));
    }

    [Theory]
    [InlineData("eng", "English")]
    [InlineData("rus", "Russian")]
    [InlineData(null, "Unknown")]
    // A code Modbot has no word for is shown as it is, rather than as nothing.
    [InlineData("xyz", "xyz")]
    public void ALanguageIsShownInWords(string? code, string expected)
        => Assert.Equal(expected, LanguageNames.Label(code));
}
