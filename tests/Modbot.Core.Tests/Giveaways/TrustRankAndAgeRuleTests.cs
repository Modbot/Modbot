using System.Text.Json.Nodes;
using Modbot.Core.Giveaways;
using Modbot.Core.Users;

namespace Modbot.Core.Tests.Giveaways;

/// <summary>
/// The two rules auto-invites added to the shared vocabulary (auto-invites design §3.1): a trust
/// rank named rather than numbered, and the sticky 18+ flag.
/// </summary>
public class TrustRankAndAgeRuleTests
{
    [Fact]
    public void ATrustRankRuleIsReadAndWrittenBackByName()
    {
        var rule = GiveawayRules.Read(
            JsonNode.Parse("""{"kind":"trustRankAtLeast","id":"TrustedUser"}"""), out var error);

        Assert.Null(error);
        Assert.NotNull(rule);
        Assert.Equal("TrustedUser", rule.Id);
        Assert.Equal("""{"kind":"trustRankAtLeast","id":"TrustedUser"}""", GiveawayRules.Store(rule));
    }

    [Fact]
    public void ATrustRankRuleReadsInWordsAMemberCanUnderstand()
    {
        var rule = new GiveawayRule { Kind = GiveawayRuleKinds.TrustRankAtLeast, Id = "KnownUser" };

        Assert.Equal("trust rank Known User or better", GiveawayRules.Describe(rule));
    }

    [Fact]
    public void ATrustRankRuleWithNoRankIsRefused()
    {
        GiveawayRules.Read(JsonNode.Parse("""{"kind":"trustRankAtLeast"}"""), out var error);

        Assert.Equal("'trustRankAtLeast' needs a trust rank.", error);
    }

    [Fact]
    public void ARankModbotDoesNotKnowIsRefusedRatherThanReadAsVisitor()
    {
        // TrustRanks.Parse answers Visitor for anything it does not know, which would have turned
        // a typo into a rule that lets everybody through.
        GiveawayRules.Read(
            JsonNode.Parse("""{"kind":"trustRankAtLeast","id":"SuperUser"}"""), out var error);

        Assert.Equal("'trustRankAtLeast' needs a trust rank.", error);
    }

    [Theory]
    [InlineData("Nuisance")]
    [InlineData("VRChatTeam")]
    public void ARankThatIsNotOnTheLadderCannotBeAskedFor(string rank)
    {
        GiveawayRules.Read(
            JsonNode.Parse($$"""{"kind":"trustRankAtLeast","id":"{{rank}}"}"""), out var error);

        Assert.NotNull(error);
        Assert.Contains("is not a rank a rule can ask for", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The18PlusRuleNeedsNothingBesideItsKind()
    {
        var rule = GiveawayRules.Read(JsonNode.Parse("""{"kind":"age18Plus"}"""), out var error);

        Assert.Null(error);
        Assert.NotNull(rule);
        Assert.Equal("18+ verified", GiveawayRules.Describe(rule));
    }

    // ── The ladder comparison itself ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(TrustRank.TrustedUser, TrustRank.TrustedUser, true)]
    [InlineData(TrustRank.Legend, TrustRank.TrustedUser, true)]
    [InlineData(TrustRank.KnownUser, TrustRank.TrustedUser, false)]
    [InlineData(TrustRank.Visitor, TrustRank.Visitor, true)]
    public void MeetsComparesAlongTheLadder(TrustRank held, TrustRank needed, bool expected)
        => Assert.Equal(expected, TrustRanks.Meets(held, needed));

    [Theory]
    [InlineData(TrustRank.Nuisance)]
    [InlineData(TrustRank.VRChatTeam)]
    public void ARankAboveTheLadderNeverCountsAsTrustedOrBetter(TrustRank held)
    {
        // These are numbered above Legend because they override the ladder on a nameplate, not
        // because they sit on top of it. A plain >= would let a known troll through.
        Assert.False(TrustRanks.Meets(held, TrustRank.TrustedUser));
        Assert.False(TrustRanks.Meets(held, TrustRank.Visitor));
    }

    [Fact]
    public void ARankNobodyHasReadIsNotVisitor()
        => Assert.False(TrustRanks.Meets(null, TrustRank.Visitor));
}
