using Modbot.Core.Bluesky;

namespace Modbot.Core.Tests.Bluesky;

/// <summary>
/// The tags on a calendar event's Bluesky post: the one a category's name makes, what a typed tag is
/// cleaned to, the list in Settings → Bluesky, and how many fit on a post.
/// </summary>
public class BlueskyTagsTests
{
    [Theory]
    [InlineData("Dance night", "DanceNight")]
    [InlineData("karaoke", "Karaoke")]
    [InlineData("Movie  night!", "MovieNight")]
    [InlineData("open-mic night", "OpenMicNight")]
    [InlineData("Café", "Café")]
    [InlineData("VRChat", "VRChat")]
    [InlineData("Dance night 2", "DanceNight2")]
    [InlineData("2026", "")]
    [InlineData("!!!", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void ANameMakesItsWordsInCamelCase(string? name, string expected) =>
        Assert.Equal(expected, BlueskyTags.FromName(name));

    [Fact]
    public void ANameWhoseTagWouldBeLongerThanBlueskyTakesGivesNone() =>
        Assert.Equal(string.Empty, BlueskyTags.FromName(new string('a', BlueskyText.TagLimit + 1)));

    [Theory]
    [InlineData("#VRChat", "VRChat")]
    [InlineData("  #Dance night ", "Dancenight")]
    [InlineData("Karaoke!", "Karaoke")]
    [InlineData("#", "")]
    [InlineData("12345", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ATypedTagIsCleanedOfHashSpacesAndPunctuation(string? typed, string expected) =>
        Assert.Equal(expected, BlueskyTags.Clean(typed));

    [Fact]
    public void ATypedTagLongerThanBlueskyTakesIsLeftAsItIsToBeRefused()
    {
        var tag = BlueskyTags.Clean(new string('a', BlueskyText.TagLimit + 1));

        Assert.True(BlueskyTags.IsTooLong(tag));
    }

    [Fact]
    public void ACategoryHasItsOwnTagElseItsNamesAndAnEmptyOneIsNone()
    {
        Assert.Equal("DanceNight", BlueskyTags.ForCategory("Dance night", null));
        Assert.Equal("Dance", BlueskyTags.ForCategory("Dance night", "#Dance"));
        Assert.Equal(string.Empty, BlueskyTags.ForCategory("Dance night", string.Empty));
    }

    [Fact]
    public void TheListIsSplitAtSpacesAndCommas_WithNoTagTwiceWhateverItsCase()
    {
        Assert.Equal(["VRChat", "Karaoke", "Music"], BlueskyTags.ParseList("#VRChat, karaoke  #vrchat #Music"));
        Assert.Empty(BlueskyTags.ParseList(null));
        Assert.Empty(BlueskyTags.ParseList("  "));
    }

    [Fact]
    public void TheListTakesAtMostFiveTags_AndIsKeptAsHashAndTag()
    {
        var tags = BlueskyTags.ParseList("a1 b2 c3 d4 e5 f6 g7");

        Assert.Equal(BlueskyTags.MostEveryPost, tags.Count);
        Assert.Equal("#a1 #b2 #c3 #d4 #e5", BlueskyTags.Write(tags));
    }

    [Fact]
    public void TooManyTagsOrOneTooLongIsAProblem_AndAGoodListIsNot()
    {
        Assert.Null(BlueskyTags.ProblemWith(null));
        Assert.Null(BlueskyTags.ProblemWith(string.Empty));
        Assert.Null(BlueskyTags.ProblemWith("#VRChat #Karaoke"));
        Assert.Null(BlueskyTags.ProblemWith("#a1 #A1 #b2 #c3 #d4 #e5"));
        Assert.Equal(BlueskyTags.TooMany, BlueskyTags.ProblemWith("a1 b2 c3 d4 e5 f6"));
        Assert.Equal(BlueskyTags.TooLong, BlueskyTags.ProblemWith("#" + new string('a', BlueskyText.TagLimit + 1)));
    }

    [Fact]
    public void TagsGoOnALastLineOfTheText_InOrder()
    {
        Assert.Equal("Hangout\n#VRChat #Karaoke", BlueskyTags.Append("Hangout", ["VRChat", "Karaoke"]));
        Assert.Equal("Hangout", BlueskyTags.Append("Hangout", []));
        Assert.Equal("Hangout\n#VRChat", BlueskyTags.Append("Hangout", ["VRChat", "vrchat"]));
    }

    [Fact]
    public void TagsThatDoNotFitAreDroppedFromTheEnd_AndTheTextIsNeverCut()
    {
        var text = new string('x', BlueskyText.GraphemeLimit - 20);

        var result = BlueskyTags.Append(text, ["Short", "AVeryLongTagThatWillNotFit", "Last"]);

        Assert.StartsWith(text, result, StringComparison.Ordinal);
        Assert.Equal(text + "\n#Short", result);
        Assert.True(BlueskyText.Fits(result));
    }

    [Fact]
    public void ATextThatFillsThePostTakesNoTag()
    {
        var text = new string('x', BlueskyText.GraphemeLimit);

        Assert.Equal(text, BlueskyTags.Append(text, ["VRChat"]));
    }
}
