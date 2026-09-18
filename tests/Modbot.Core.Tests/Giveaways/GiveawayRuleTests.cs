using System.Text.Json.Nodes;
using Modbot.Core.Giveaways;

namespace Modbot.Core.Tests.Giveaways;

/// <summary>
/// Giveaways design §2: one record for a question and for a group of them, read strictly, written
/// back as it came, and describable in words a member can read.
/// </summary>
public class GiveawayRuleTests
{
    [Fact]
    public void AnEmptyTreeLetsEverybodyIn()
    {
        var rule = GiveawayRules.Read(JsonNode.Parse("""{"kind":"allOf","rules":[]}"""), out var error);

        Assert.Null(error);
        Assert.NotNull(rule);
        Assert.True(rule.LetsEveryoneIn);
        Assert.Equal("Everyone", GiveawayRules.Describe(rule));
    }

    [Fact]
    public void ARuleModbotDoesNotKnowIsRefusedByName()
    {
        var rule = GiveawayRules.Read(JsonNode.Parse("""{"kind":"hasNiceHair"}"""), out var error);

        Assert.Null(rule);
        Assert.Equal("'hasNiceHair' is not a rule Modbot knows.", error);
    }

    [Fact]
    public void ARuleThatNeedsANumberAndHasNoneIsRefused()
    {
        GiveawayRules.Read(JsonNode.Parse("""{"kind":"instanceHours"}"""), out var error);

        Assert.Equal("'instanceHours' needs a number.", error);
    }

    [Fact]
    public void ARuleThatNeedsARoleAndHasNoneIsRefused()
    {
        GiveawayRules.Read(JsonNode.Parse("""{"kind":"groupRole"}"""), out var error);

        Assert.Equal("'groupRole' needs a role.", error);
    }

    [Fact]
    public void AWindowOnARuleThatIsNotCountedOverTimeIsRefused()
    {
        GiveawayRules.Read(
            JsonNode.Parse("""{"kind":"groupMemberDays","amount":30,"withinDays":10}"""), out var error);

        Assert.Equal("'groupMemberDays' is not counted over a window.", error);
    }

    [Fact]
    public void NestingDeeperThanThreeIsRefused()
    {
        var deep = """
            {"kind":"allOf","rules":[
              {"kind":"anyOf","rules":[
                {"kind":"allOf","rules":[
                  {"kind":"noneOf","rules":[]}]}]}]}
            """;

        GiveawayRules.Read(JsonNode.Parse(deep), out var error);

        Assert.Equal("Rules can be grouped at most 3 deep.", error);
    }

    /// <summary>
    /// Spread across groups, so no one group is over its own limit of twenty and the tree total is
    /// what refuses it. Piling them all into a single group tests the other limit instead.
    /// </summary>
    [Fact]
    public void ATreeOfMoreThanSixtyRulesIsRefused()
    {
        var groups = new JsonArray();

        for (var g = 0; g < 4; g++)
        {
            var leaves = new JsonArray();
            for (var i = 0; i < 15; i++)
                leaves.Add(new JsonObject { ["kind"] = "inGroup" });

            groups.Add(new JsonObject { ["kind"] = "anyOf", ["rules"] = leaves });
        }

        GiveawayRules.Read(new JsonObject { ["kind"] = "allOf", ["rules"] = groups }, out var error);

        Assert.Equal("A giveaway can have at most 60 rules.", error);
    }

    [Fact]
    public void AGroupOfMoreThanTwentyRulesIsRefused()
    {
        var rules = new JsonArray();
        for (var i = 0; i < 21; i++)
            rules.Add(new JsonObject { ["kind"] = "inGroup" });

        GiveawayRules.Read(new JsonObject { ["kind"] = "allOf", ["rules"] = rules }, out var error);

        Assert.Equal("A group can hold at most 20 rules.", error);
    }

    [Fact]
    public void ATreeSurvivesBeingWrittenAndReadBack()
    {
        const string Json = """
            {"kind":"allOf","rules":[
              {"kind":"discordMemberDays","amount":30},
              {"kind":"instanceHours","amount":10,"withinDays":90},
              {"kind":"anyOf","rules":[
                {"kind":"groupRole","id":"grol_regulars"},
                {"kind":"voiceHours","amount":20}]}]}
            """;

        var first = GiveawayRules.Read(JsonNode.Parse(Json), out var error);
        Assert.Null(error);
        Assert.NotNull(first);

        var again = GiveawayRules.ReadStored(GiveawayRules.Store(first));

        Assert.Equal(GiveawayRules.Store(first), GiveawayRules.Store(again));
    }

    [Fact]
    public void UnreadableStoredTextBecomesEveryoneRatherThanThrowing()
    {
        Assert.True(GiveawayRules.ReadStored("not json at all").LetsEveryoneIn);
        Assert.True(GiveawayRules.ReadStored(null).LetsEveryoneIn);
    }

    [Fact]
    public void EachRuleReadsAsASentence()
    {
        var rule = GiveawayRules.ReadStored("""
            {"kind":"allOf","rules":[
              {"kind":"discordMemberDays","amount":30},
              {"kind":"instanceHours","amount":10,"withinDays":90},
              {"kind":"oneInstanceHours","amount":3},
              {"kind":"noTrouble"},
              {"kind":"groupRole","id":"grol_regulars"}]}
            """);

        var lines = GiveawayRules.DescribeLines(rule, new Dictionary<string, string> { ["grol_regulars"] = "Regulars" });

        Assert.Equal(
            [
                "in Discord for 30 days or more",
                "10 hours or more in our instances in the last 90 days",
                "3 hours or more in one single instance",
                "no bans, kicks or flags",
                "holds the group role Regulars",
            ],
            lines);
    }

    [Fact]
    public void ARoleWithNoKnownNameFallsBackToItsId()
    {
        var rule = GiveawayRules.ReadStored("""{"kind":"discordRole","id":"123456"}""");

        Assert.Equal("holds the Discord role 123456", GiveawayRules.Describe(rule));
    }

    /// <summary>
    /// There is no "does not hold" on a single rule; `noneOf` around it says the same thing, and it
    /// has to read that way in the card.
    /// </summary>
    [Fact]
    public void NoneOfReadsAsNoneOf()
    {
        var rule = GiveawayRules.ReadStored("""
            {"kind":"noneOf","rules":[{"kind":"discordRole","id":"123"}]}
            """);

        Assert.Equal("none of: holds the Discord role 123", GiveawayRules.Describe(rule));
    }

    [Fact]
    public void NumbersAreWrittenWithoutTrailingZeros()
    {
        Assert.Equal("10", GiveawayRules.Plain(10m));
        Assert.Equal("10", GiveawayRules.Plain(10.000m));
        Assert.Equal("10.5", GiveawayRules.Plain(10.5m));
    }

    [Fact]
    public void ExclusionsSurviveBeingWrittenAndReadBack()
    {
        var exclusions = new GiveawayExclusions
        {
            Staff = true,
            PastWinners = false,
            BannedMembers = true,
            People = ["usr_1", "usr_2"],
        };

        var again = GiveawayExclusions.ReadStored(exclusions.Store());

        Assert.True(again.Staff);
        Assert.False(again.PastWinners);
        Assert.True(again.BannedMembers);
        Assert.Equal(["usr_1", "usr_2"], again.People);
    }

    [Fact]
    public void ExclusionsReadAsWords()
    {
        var exclusions = new GiveawayExclusions { Staff = true, BannedMembers = true, People = ["usr_1"] };

        Assert.Equal(["staff", "banned members", "1 person named"], exclusions.Describe());
    }
}
