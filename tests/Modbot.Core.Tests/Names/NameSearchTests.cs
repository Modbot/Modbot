using Modbot.Core.Names;

namespace Modbot.Core.Tests.Names;

/// <summary>The patterns a search term becomes, and the plain name a row carries.</summary>
public class NameSearchTests
{
    [Fact]
    public void ThePatternMatchesTheTermLiterallyAnywhere()
    {
        Assert.Equal("%alice%", NameSearch.Pattern("alice"));
        Assert.Equal("%50\\%\\_off\\\\%", NameSearch.Pattern("50%_off\\"));
        Assert.Equal("b\\%b", NameSearch.Escape("b%b"));
    }

    [Fact]
    public void TheSearchablePatternIsTheTermsSearchableForm()
    {
        Assert.Equal("%alex%", NameSearch.SearchablePattern("𝕬𝖑𝖊𝖝"));
        Assert.Equal("%adderall%", NameSearch.SearchablePattern("Addеrаll"));
        Assert.Equal("%bob\\_builder%", NameSearch.SearchablePattern("Bob_Builder"));
        Assert.Equal("%boba.tea%", NameSearch.SearchablePattern("Boba․tea"));
    }

    [Theory]
    [InlineData("༒")]
    [InlineData("💙")]
    [InlineData("『』")]
    [InlineData("̷̷")]
    public void ATermThatIsDecorationAloneHasNoSearchablePattern(string term)
        => Assert.Null(NameSearch.SearchablePattern(term));

    [Theory]
    [InlineData("𝕬𝖑𝖊𝖝", "Alex")]
    [InlineData("༻sᴜɢᴀʀʙᴜɴɴɪᴇ༺", "sugarbunnie")]
    [InlineData("Addеrаll", "Adderall")]
    [InlineData("Łęmøń Çãkę", "Lemon Cake")]
    public void ThePlainNameIsTheReadableFormWhenItDiffers(string name, string plain)
        => Assert.Equal(plain, PlainName.Of(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Alice Wonder")]
    [InlineData("Bob_Builder")]
    [InlineData("|~Принцесса Кира~|")]
    [InlineData("翔宇")]
    [InlineData("༝༚༝༚༝༚")]
    public void ThereIsNoPlainNameForAPlainNameOrNothing(string? name)
        => Assert.Null(PlainName.Of(name));
}
