using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;

namespace Modbot.Core.Tests.Posts;

/// <summary>
/// The words each site is sent (posts design §3.5, §4.3): Discord's mention line and bold title,
/// a destination's own text, and the counter at Discord's 2000.
/// </summary>
public class PostTextsTests
{
    [Fact]
    public void TheTitleIsBoldOnTheFirstLine()
    {
        Assert.Equal("**Movie night**\nBring snacks", PostTexts.Discord("Movie night", "Bring snacks", roleId: null));
    }

    [Fact]
    public void NoTitleIsJustTheText()
    {
        Assert.Equal("Bring snacks", PostTexts.Discord(null, "Bring snacks", null));
        Assert.Equal("Bring snacks", PostTexts.Discord("   ", "Bring snacks", null));
    }

    [Fact]
    public void TheRoleIsMentionedOnALineOfItsOwnAboveTheTitle()
    {
        Assert.Equal("<@&42>\n**Movie night**\nBring snacks", PostTexts.Discord("Movie night", "Bring snacks", "42"));
    }

    [Fact]
    public void LineEndsAreTidiedTheWayDiscordKeepsThem()
    {
        Assert.Equal("one\ntwo", PostTexts.Tidy("  one\r\ntwo \r\n"));
        Assert.Equal("A title", PostTexts.TidyTitle(" A\ntitle "));
        Assert.Null(PostTexts.TidyTitle("  "));
    }

    [Fact]
    public void ADestinationsOwnTextAndTitleWin()
    {
        var post = new Post { Title = "Movie night", Text = "Bring snacks" };
        var destination = new PostDestination { Network = PostNetworks.Discord, TextOverride = "Popcorn provided" };

        Assert.Equal("**Movie night**\nPopcorn provided", PostTexts.Discord(post, destination));

        destination.TitleOverride = "Film night";
        Assert.Equal("**Film night**\nPopcorn provided", PostTexts.Discord(post, destination));
    }

    [Fact]
    public void TheRoleComesFromTheDestinationsOptions()
    {
        var post = new Post { Text = "Bring snacks" };
        var destination = new PostDestination
        {
            Network = PostNetworks.Discord,
            Options = PostTexts.WriteDiscordOptions(new DiscordPostOptions("42", Publish: true)),
        };

        Assert.Equal("<@&42>\nBring snacks", PostTexts.Discord(post, destination));
        Assert.True(PostTexts.DiscordOptionsOf(destination).Publish);
    }

    [Fact]
    public void OptionsThatCannotBeReadAreTheDefaults()
    {
        Assert.Equal(new DiscordPostOptions(), PostTexts.ParseDiscordOptions("not json"));
        Assert.Equal(new DiscordPostOptions(), PostTexts.ParseDiscordOptions(null));
    }

    [Fact]
    public void TwoThousandFits_OneMoreDoesNot()
    {
        Assert.True(PostTexts.DiscordFits(new string('a', 2000)));
        Assert.False(PostTexts.DiscordFits(new string('a', 2001)));
    }

    [Fact]
    public void AnEmojiCountsAsTheTwoUnitsItTakes()
    {
        // 1000 smiling faces are 2000 UTF-16 units: the most that fits.
        var faces = string.Concat(Enumerable.Repeat("\U0001F600", 1000));
        Assert.Equal(2000, PostTexts.DiscordLength(faces));
        Assert.True(PostTexts.DiscordFits(faces));
        Assert.False(PostTexts.DiscordFits(faces + "a"));
    }

    [Fact]
    public void AJoinedEmojiCountsEveryUnitInIt()
    {
        // A family: four people joined by three zero-width joiners, eleven units.
        const string Family = "\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466";
        Assert.Equal(11, PostTexts.DiscordLength(Family));
    }

    [Fact]
    public void CjkCountsOneUnitACharacter()
    {
        Assert.Equal(4, PostTexts.DiscordLength("映画の夜"));
        Assert.True(PostTexts.DiscordFits(new string('夜', 2000)));
    }

    [Fact]
    public void TheMentionAndTheTitleCountTowardsTheLimit()
    {
        var text = new string('a', 1990);
        Assert.True(PostTexts.DiscordFits(PostTexts.Discord(null, text, null)));
        Assert.False(PostTexts.DiscordFits(PostTexts.Discord("Movie night", text, "123456789012345678")));
    }

    [Fact]
    public void ADiscordLinkNamesTheServerChannelAndMessage()
    {
        Assert.Equal("https://discord.com/channels/1/2/3", PostTexts.DiscordLink("1", "2", "3"));
    }
}
