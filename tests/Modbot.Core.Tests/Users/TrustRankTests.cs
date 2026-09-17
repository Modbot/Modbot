using System.Text.Json;
using Modbot.Core.Users;

namespace Modbot.Core.Tests.Users;

/// <summary>
/// The tag list to rank rule: every tag, the off-by-one naming, highest wins, and the two
/// overrides. Research: <c>.agent/research/2026-09-16-vrchat-trust-ranks.md</c>.
/// </summary>
public class TrustRankTests
{
    [Theory]
    [InlineData("system_trust_basic", TrustRank.NewUser)]
    [InlineData("system_trust_known", TrustRank.User)]
    [InlineData("system_trust_trusted", TrustRank.KnownUser)]
    [InlineData("system_trust_veteran", TrustRank.TrustedUser)]
    [InlineData("system_trust_legend", TrustRank.Legend)]
    [InlineData("system_probable_troll", TrustRank.Nuisance)]
    [InlineData("system_troll", TrustRank.Nuisance)]
    [InlineData("admin_moderator", TrustRank.VRChatTeam)]
    public void EachTagMeansTheRankVRChatShowsNotTheRankItIsNamedAfter(string tag, TrustRank expected)
    {
        Assert.Equal(expected, TrustRanks.FromTags([tag]));
    }

    [Fact]
    public void NoTagsIsAVisitor()
    {
        Assert.Equal(TrustRank.Visitor, TrustRanks.FromTags([]));
        Assert.Equal(TrustRank.Visitor, TrustRanks.FromTags(null));
    }

    [Theory]
    [InlineData("system_trust_intermediate")]
    [InlineData("system_trust_advanced")]
    [InlineData("system_supporter")]
    [InlineData("system_early_adopter")]
    [InlineData("language_eng")]
    [InlineData("show_social_rank")]
    [InlineData("")]
    [InlineData("SYSTEM_TRUST_VETERAN")]
    public void TagsThatAreNotRanksSayNothing(string tag)
    {
        Assert.Equal(TrustRank.Visitor, TrustRanks.FromTags([tag]));
        Assert.Equal(TrustRank.User, TrustRanks.FromTags([tag, "system_trust_known"]));
    }

    [Fact]
    public void ANullTagIsSkippedRatherThanThrown()
    {
        Assert.Equal(TrustRank.KnownUser, TrustRanks.FromTags([null, "system_trust_trusted", null]));
    }

    [Fact]
    public void TheHighestLadderTagWinsWhateverTheOrder()
    {
        string[] ascending = ["system_trust_basic", "system_trust_known", "system_trust_trusted", "system_trust_veteran"];

        Assert.Equal(TrustRank.TrustedUser, TrustRanks.FromTags(ascending));
        Assert.Equal(TrustRank.TrustedUser, TrustRanks.FromTags(ascending.Reverse()));
        Assert.Equal(TrustRank.Legend, TrustRanks.FromTags([.. ascending, "system_trust_legend"]));
    }

    [Theory]
    [InlineData("system_troll")]
    [InlineData("system_probable_troll")]
    public void ANuisanceTagOverridesTheLadderRankBesideIt(string nuisance)
    {
        Assert.Equal(TrustRank.Nuisance, TrustRanks.FromTags(["system_trust_veteran", nuisance]));
        Assert.Equal(TrustRank.Nuisance, TrustRanks.FromTags([nuisance, "system_trust_basic"]));
        Assert.Equal(TrustRank.Nuisance, TrustRanks.FromTags(["system_trust_legend", nuisance]));
    }

    [Fact]
    public void VRChatTeamOverridesEverythingNuisanceIncluded()
    {
        Assert.Equal(TrustRank.VRChatTeam, TrustRanks.FromTags(["system_trust_veteran", "admin_moderator"]));
        Assert.Equal(TrustRank.VRChatTeam, TrustRanks.FromTags(["admin_moderator", "system_troll"]));
        Assert.Equal(TrustRank.VRChatTeam, TrustRanks.FromTags(["system_probable_troll", "admin_moderator", "system_trust_basic"]));
    }

    [Fact]
    public void TheLadderIsOrderedSoThatLessThanMeansLowerRank()
    {
        Assert.True(TrustRank.Visitor < TrustRank.NewUser);
        Assert.True(TrustRank.NewUser < TrustRank.User);
        Assert.True(TrustRank.User < TrustRank.KnownUser);
        Assert.True(TrustRank.KnownUser < TrustRank.TrustedUser);
        Assert.True(TrustRank.TrustedUser < TrustRank.Legend);
        Assert.True(TrustRank.Legend < TrustRank.Nuisance);
        Assert.True(TrustRank.Nuisance < TrustRank.VRChatTeam);
    }

    [Theory]
    [InlineData(TrustRank.Visitor, "Visitor", "#CCCCCC")]
    [InlineData(TrustRank.NewUser, "New User", "#1778FF")]
    [InlineData(TrustRank.User, "User", "#2BCF5C")]
    [InlineData(TrustRank.KnownUser, "Known User", "#FF7B42")]
    [InlineData(TrustRank.TrustedUser, "Trusted User", "#8143E6")]
    [InlineData(TrustRank.Legend, "Legend", "#FFD000")]
    [InlineData(TrustRank.Nuisance, "Nuisance", "#782F2F")]
    [InlineData(TrustRank.VRChatTeam, "VRChat Team", "#FF2626")]
    public void EveryRankHasVRChatsNameAndColour(TrustRank rank, string name, string colour)
    {
        Assert.Equal(name, TrustRanks.Name(rank));
        Assert.Equal(colour, TrustRanks.Colour(rank));
    }

    [Fact]
    public void ARankGoesOverTheWireByNameNotByNumber()
    {
        Assert.Equal("\"KnownUser\"", JsonSerializer.Serialize(TrustRank.KnownUser));
        Assert.Equal(TrustRank.VRChatTeam, JsonSerializer.Deserialize<TrustRank>("\"VRChatTeam\""));
    }

    [Theory]
    [InlineData("TrustedUser", TrustRank.TrustedUser)]
    [InlineData("trusteduser", TrustRank.TrustedUser)]
    [InlineData("Nuisance", TrustRank.Nuisance)]
    [InlineData("", TrustRank.Visitor)]
    [InlineData(null, TrustRank.Visitor)]
    [InlineData("Wizard", TrustRank.Visitor)]
    [InlineData("42", TrustRank.Visitor)]
    public void ParsingAStoredNameNeverThrows(string? text, TrustRank expected)
    {
        Assert.Equal(expected, TrustRanks.Parse(text));
    }
}
