using Modbot.Api.Features.GroupPage;

namespace Modbot.Api.Tests.Features.GroupPage;

/// <summary>VRChat's documented limits, checked before anything is sent, and what counts as a change.</summary>
public class GroupPageRulesTests
{
    private static readonly GroupProfileState Before = new(
        "Test Group", "A group", "Be kind", ["eng"], ["https://example.com/"], "open");

    [Theory]
    [InlineData("ab")]
    [InlineData("  ab  ")]
    public void ANameUnderThreeCharactersIsRefused(string name)
        => Assert.NotNull(GroupPageRules.Tidy(new GroupProfileEdit(Name: name)).Problem);

    [Fact]
    public void ANameOverSixtyFourCharactersIsRefused()
        => Assert.NotNull(GroupPageRules.Tidy(new GroupProfileEdit(Name: new string('n', 65))).Problem);

    [Fact]
    public void TheDescriptionMayBeTwoHundredAndFiftyCharacters_AndNoMore()
    {
        Assert.Null(GroupPageRules.Tidy(new GroupProfileEdit(Description: new string('d', 250))).Problem);
        Assert.NotNull(GroupPageRules.Tidy(new GroupProfileEdit(Description: new string('d', 251))).Problem);
    }

    [Fact]
    public void RulesHaveNoLimitOfModbotsOwn()
        => Assert.Null(GroupPageRules.Tidy(new GroupProfileEdit(Rules: new string('r', 10_000))).Problem);

    [Fact]
    public void LanguagesAreLowerCased_RepeatsDropped_AndAtMostThree()
    {
        var (edit, problem) = GroupPageRules.Tidy(new GroupProfileEdit(Languages: ["ENG", "eng", " jpn ", ""]));

        Assert.Null(problem);
        Assert.Equal(["eng", "jpn"], edit!.Languages);

        Assert.NotNull(GroupPageRules.Tidy(new GroupProfileEdit(Languages: ["eng", "jpn", "deu", "fra"])).Problem);
        Assert.NotNull(GroupPageRules.Tidy(new GroupProfileEdit(Languages: ["english"])).Problem);
    }

    [Fact]
    public void LinksMustBeWebAddresses_AndAtMostThree()
    {
        var (edit, problem) = GroupPageRules.Tidy(new GroupProfileEdit(Links: [" https://discord.gg/example ", "", "http://example.com"]));

        Assert.Null(problem);
        Assert.Equal(["https://discord.gg/example", "http://example.com/"], edit!.Links);

        Assert.NotNull(GroupPageRules.Tidy(new GroupProfileEdit(Links: ["javascript:alert(1)"])).Problem);
        Assert.NotNull(GroupPageRules.Tidy(new GroupProfileEdit(Links: ["discord.gg/example"])).Problem);
        Assert.NotNull(GroupPageRules.Tidy(new GroupProfileEdit(
            Links: ["https://a.example", "https://b.example", "https://c.example", "https://d.example"])).Problem);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("Request")]
    [InlineData("invite")]
    [InlineData("closed")]
    public void JoinStateIsOneOfVRChatsFourWords(string word)
        => Assert.Null(GroupPageRules.Tidy(new GroupProfileEdit(JoinState: word)).Problem);

    [Fact]
    public void AnyOtherJoinStateIsRefused()
        => Assert.NotNull(GroupPageRules.Tidy(new GroupProfileEdit(JoinState: "public")).Problem);

    [Fact]
    public void OnlyWhatDiffersIsAChange()
    {
        var (edit, _) = GroupPageRules.Tidy(new GroupProfileEdit(
            Name: "Test Group",
            Description: "New words",
            Languages: ["eng"],
            Links: ["https://example.com"],
            JoinState: "request"));

        var changes = GroupPageRules.Changes(Before, edit!);

        Assert.Equal(["description", "joinState"], changes.Select(c => c.Field).ToList());
        Assert.Equal("A group", changes[0].Old!.GetValue<string>());
        Assert.Equal("New words", changes[0].New!.GetValue<string>());
    }

    [Fact]
    public void ClearingAFieldIsAChange_LeavingItOutIsNot()
    {
        var (cleared, _) = GroupPageRules.Tidy(new GroupProfileEdit(Rules: "", Links: []));
        var changes = GroupPageRules.Changes(Before, cleared!);

        Assert.Equal(["rules", "links"], changes.Select(c => c.Field).ToList());
        Assert.Empty(GroupPageRules.Changes(Before, new GroupProfileEdit()));
    }

    [Fact]
    public void APostNeedsATitleAndText_AndIsForMembersUnlessSaidOtherwise()
    {
        Assert.NotNull(GroupPageRules.TidyPost(new GroupPostBody(null, " ", "Words", null, null)).Problem);
        Assert.NotNull(GroupPageRules.TidyPost(new GroupPostBody(null, "Title", "", null, null)).Problem);
        Assert.NotNull(GroupPageRules.TidyPost(new GroupPostBody(null, "Title", "Words", "friends", null)).Problem);

        var (post, problem) = GroupPageRules.TidyPost(new GroupPostBody(null, " Title ", "Words", null, ["grol_1", "grol_1", " "]));

        Assert.Null(problem);
        Assert.Equal("Title", post!.Title);
        Assert.Equal("group", post.Visibility);
        Assert.Equal(["grol_1"], post.RoleIds);
    }
}
