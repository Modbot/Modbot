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

    private static VRChatInstance Room(int? headCount = 4, int? listCount = 2) => new()
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
    public void TheHeadCount_IsTheRoomsOwnCount()
    {
        var card = InstanceCard.For(Room(headCount: 4, listCount: 2), null, Now);

        Assert.Equal("4 people", card.Fields.Single(f => f.Name == "People here now").Value);
    }

    [Fact]
    public void BeforeTheRoomsPageIsRead_TheListsCountIsShown()
    {
        var card = InstanceCard.For(Room(headCount: null, listCount: 2), null, Now);

        Assert.Equal("2 people", card.Fields.Single(f => f.Name == "People here now").Value);
    }

    [Fact]
    public void NobodyWatching_ShowsTheHeadCountOnly()
    {
        var card = InstanceCard.For(Room(), null, Now, names: null);

        Assert.DoesNotContain(card.Fields, f => f.Name == "Who is here");
    }

    [Fact]
    public void WhileWatched_TheNamesAreListed()
    {
        var card = InstanceCard.For(Room(), null, Now, ["Rin", "Ada"]);

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
    public void AClosedRoomListsNobody()
    {
        var room = Room();
        room.ClosedAt = Now;

        var card = InstanceCard.For(room, null, Now, ["Ada"]);

        Assert.DoesNotContain(card.Fields, f => f.Name == "Who is here");
    }
}
