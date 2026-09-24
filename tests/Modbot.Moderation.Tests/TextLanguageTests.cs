using Modbot.Moderation;

namespace Modbot.Moderation.Tests;

/// <summary>
/// The language marked on every flag (AI moderation design §18). Offline: no request goes anywhere.
/// </summary>
public class TextLanguageTests : IDisposable
{
    private readonly TextLanguage _language = new();

    public void Dispose()
    {
        _language.Dispose();
        GC.SuppressFinalize(this);
    }

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

    [Fact]
    public void NothingIsLoadedUntilSomethingAsks()
    {
        using var language = new TextLanguage();

        Assert.False(language.Loaded);

        language.Of("Please stop posting that link in this channel, it is a scam.");

        Assert.True(language.Loaded);
    }

    [Fact]
    public void TextTooShortToTellLoadsNothing()
    {
        using var language = new TextLanguage();

        Assert.Null(language.Of("lol"));
        Assert.False(language.Loaded);
    }

    [Fact]
    public void ProfilesArePutDownAfterTheQuietAndBuiltBackOnTheNextQuestion()
    {
        using var language = new TextLanguage();
        const string sentence = "Please stop posting that link in this channel, it is a scam.";

        Assert.Equal("eng", language.Of(sentence));
        Assert.True(language.Loaded);

        language.PutDown();
        Assert.False(language.Loaded);

        // The point of the whole arrangement: the answer is the same either way.
        Assert.Equal("eng", language.Of(sentence));
        Assert.True(language.Loaded);
    }

    [Fact]
    public void AskingFromManyThreadsAtOnceBuildsOneSetOfProfiles()
    {
        using var language = new TextLanguage();
        const string sentence = "Please stop posting that link in this channel, it is a scam.";

        var answers = new string?[32];
        Parallel.For(0, answers.Length, i => answers[i] = language.Of(sentence));

        Assert.All(answers, a => Assert.Equal("eng", a));
        Assert.True(language.Loaded);
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
