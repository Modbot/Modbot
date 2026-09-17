using Modbot.Core.Data.Entities;
using Modbot.Discord.Instances;

namespace Modbot.Discord.Tests.Instances;

/// <summary>
/// The card's list of who is here: capped, escaped, inside Discord's field limit, and absent when
/// nobody is watching.
/// </summary>
public class InstanceCardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 21, 0, 0, TimeSpan.Zero);

    private static VRChatInstance Instance(int? headCount = 4, int? listCount = 2) => new()
    {
        Id = Guid.NewGuid(),
        Location = "wrld_a:68681~group(grp_a)~groupAccessType(plus)~region(us)",
        WorldId = "wrld_a",
        VRChatInstanceId = "68681",
        GroupAccessType = "plus",
        Region = "us",
        OpenedAt = Now.AddHours(-1),
        LastSeenAt = Now,
        LastUserCount = listCount,
        HeadCount = headCount,
        PeakUserCount = headCount,
        SeenInGroupList = true,
    };

    [Fact]
    public void TheHeadCount_IsTheInstancesOwnCount()
    {
        var card = InstanceCard.For(Instance(headCount: 4, listCount: 2), null, Now);

        Assert.Equal("4 people", card.Fields.Single(f => f.Name == "People here now").Value);
    }

    [Fact]
    public void BeforeTheInstancesPageIsRead_TheListsCountIsShown()
    {
        var card = InstanceCard.For(Instance(headCount: null, listCount: 2), null, Now);

        Assert.Equal("2 people", card.Fields.Single(f => f.Name == "People here now").Value);
    }

    [Fact]
    public void NobodyWatching_ShowsTheHeadCountOnly()
    {
        var card = InstanceCard.For(Instance(), null, Now, names: null);

        Assert.DoesNotContain(card.Fields, f => f.Name == "Who is here");
    }

    [Fact]
    public void WhileWatched_TheNamesAreListed()
    {
        var card = InstanceCard.For(Instance(), null, Now, ["Rin", "Ada"]);

        Assert.Equal("Ada\nRin", card.Fields.Single(f => f.Name == "Who is here").Value);
    }

    [Fact]
    public void MoreThanTwenty_AreCappedWithAndNMore()
    {
        var names = Enumerable.Range(1, 27).Select(i => (string?)$"Person {i:00}").ToList();

        var value = InstanceCard.NameList(names)!;
        var lines = value.Split('\n');

        Assert.Equal(InstanceCard.NamesListed + 1, lines.Length);
        Assert.Equal("and 7 more", lines[^1]);
    }

    [Fact]
    public void PeopleWithNoKnownName_AreCountedButNeverShownById()
    {
        var value = InstanceCard.NameList(["Ada", null, null])!;

        Assert.Equal("Ada\nand 2 more", value);
    }

    [Fact]
    public void LongNames_NeverPushTheFieldPastDiscordsLimit()
    {
        var names = Enumerable.Range(1, 40).Select(i => (string?)(new string('W', 100) + i)).ToList();

        var value = InstanceCard.NameList(names)!;

        Assert.True(value.Length <= InstanceCard.FieldValueLimit, $"{value.Length} characters");
        Assert.EndsWith("more", value, StringComparison.Ordinal);
    }

    [Fact]
    public void AStackOfMarkdownCharacters_StillFits()
    {
        // Escaping doubles every one of these, so the limit has to be checked on the escaped text.
        var names = Enumerable.Range(1, 20).Select(_ => (string?)new string('*', 60)).ToList();

        var value = InstanceCard.NameList(names)!;

        Assert.True(value.Length <= InstanceCard.FieldValueLimit, $"{value.Length} characters");
    }

    [Theory]
    [InlineData("**bold**", @"\*\*bold\*\*")]
    [InlineData("__under__", @"\_\_under\_\_")]
    [InlineData("~~gone~~", @"\~\~gone\~\~")]
    [InlineData("`code`", @"\`code\`")]
    [InlineData("||spoiler||", @"\|\|spoiler\|\|")]
    [InlineData("> quote", @"\> quote")]
    [InlineData("# Heading", @"\# Heading")]
    [InlineData("- item", @"\- item")]
    [InlineData("[click](https://evil.example)", @"\[click\]\(https\://evil.example\)")]
    [InlineData("@everyone", @"\@everyone")]
    [InlineData("<@123456>", @"\<\@123456\>")]
    [InlineData(@"back\slash", @"back\\slash")]
    public void DisplayNames_AreEscaped(string name, string expected)
        => Assert.Equal(expected, InstanceCard.Escape(name));

    [Fact]
    public void ALineBreakInAName_CannotStartALineOfItsOwn()
    {
        Assert.Equal(@"Ada   \# Heading", InstanceCard.Escape("Ada\n\r # Heading"));
    }

    [Fact]
    public void AClosedInstanceListsNobody()
    {
        var instance = Instance();
        instance.ClosedAt = Now;

        var card = InstanceCard.For(instance, null, Now, ["Ada"]);

        Assert.DoesNotContain(card.Fields, f => f.Name == "Who is here");
    }

    /// <summary>The address format the maintainer gave, character for character.</summary>
    [Fact]
    public void TheJoinLink_IsVRChatsLaunchPage_WithTheFullInstanceId()
    {
        var instance = Instance();
        instance.Location = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd:26093~group(grp_0a17232e-6ad4-4889-8e1e-6e0c5fa815fd)~groupAccessType(plus)~region(us)";
        instance.WorldId = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";

        Assert.Equal(
            "https://vrchat.com/home/launch?worldId=wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd&instanceId=26093~group(grp_0a17232e-6ad4-4889-8e1e-6e0c5fa815fd)~groupAccessType(plus)~region(us)",
            InstanceCard.JoinLink(instance));
    }

    [Fact]
    public void TheJoinLink_EscapesAnythingThatWouldSplitTheAddress()
    {
        var instance = Instance();
        instance.Location = "wrld_a:1&x=2#frag";

        Assert.Equal("https://vrchat.com/home/launch?worldId=wrld_a&instanceId=1%26x%3D2%23frag", InstanceCard.JoinLink(instance));
    }

    [Fact]
    public void AnOpenInstance_LinksTheTitleAndCarriesAJoinButtonAndTheWorldsPicture()
    {
        var instance = Instance();
        var world = new VRChatWorld { WorldId = "wrld_a", Name = "VRChat Home", ImageUrl = "https://api.vrchat.cloud/api/1/file/file_a/1/file" };

        var card = InstanceCard.For(instance, world, Now);

        Assert.Equal(InstanceCard.JoinLink(instance), card.Url);
        Assert.Equal("https://api.vrchat.cloud/api/1/file/file_a/1/file", card.ImageUrl);

        var join = Assert.Single(InstanceCard.Links(instance));
        Assert.Equal("Join", join.Label);
        Assert.Equal(InstanceCard.JoinLink(instance), join.Url);
    }

    [Fact]
    public void AWorldWithOnlyAThumbnail_UsesTheThumbnail()
    {
        var world = new VRChatWorld { WorldId = "wrld_a", ThumbnailImageUrl = "https://api.vrchat.cloud/api/1/image/file_a/1/256" };

        Assert.Equal("https://api.vrchat.cloud/api/1/image/file_a/1/256", InstanceCard.For(Instance(), world, Now).ImageUrl);
    }

    [Fact]
    public void AClosedInstance_HasNoJoinLinkAndNoButton()
    {
        var instance = Instance();
        instance.ClosedAt = Now;

        Assert.Null(InstanceCard.For(instance, null, Now).Url);
        Assert.Empty(InstanceCard.Links(instance));
    }

    /// <summary>A group can set an instance id to any text; it must not render as formatting.</summary>
    [Fact]
    public void AnInstanceIdWithFormatting_IsEscapedOnTheCard()
    {
        var instance = Instance();
        instance.VRChatInstanceId = "**@everyone** __big__";

        var value = InstanceCard.For(instance, null, Now).Fields.Single(f => f.Name == "Instance").Value;

        Assert.Equal(InstanceCard.Escape("**@everyone** __big__"), value);
        Assert.DoesNotContain("**@everyone**", value, StringComparison.Ordinal);
    }

    [Fact]
    public void AVeryLongInstanceId_GetsACardButNoJoinLink()
    {
        var instance = Instance();
        instance.Location = "wrld_a:" + new string('9', 600);
        instance.VRChatInstanceId = new string('9', 2000);

        var card = InstanceCard.For(instance, null, Now);

        Assert.Null(InstanceCard.JoinLink(instance));
        Assert.Null(card.Url);
        Assert.Empty(InstanceCard.Links(instance));
        Assert.True(card.Fields.Single(f => f.Name == "Instance").Value.Length <= InstanceCard.FieldValueLimit);
    }

    [Fact]
    public void TheWorldsCapacity_ShowsWhenItsPageSaysIt()
    {
        var world = new VRChatWorld { WorldId = "wrld_a", Capacity = 32 };

        Assert.Equal("32", InstanceCard.For(Instance(), world, Now).Fields.Single(f => f.Name == "Capacity").Value);
        Assert.DoesNotContain(InstanceCard.For(Instance(), null, Now).Fields, f => f.Name == "Capacity");
    }
}
