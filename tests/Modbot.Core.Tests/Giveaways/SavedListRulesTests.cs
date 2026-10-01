using System.Text.Json.Nodes;
using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;

namespace Modbot.Core.Tests.Giveaways;

/// <summary>
/// Lists design §3 and §4: the rules saved lists added to the giveaway rule tree, read strictly and
/// written back as they came, and a list's rules standing in for the list wherever it is named.
/// </summary>
public class SavedListRulesTests
{
    private const string Regulars = "0192a8f0-0000-7000-8000-000000000001";
    private const string Newcomers = "0192a8f0-0000-7000-8000-000000000002";

    private static GiveawayRule Read(string json)
    {
        var rule = GiveawayRules.Read(JsonNode.Parse(json), out var error);
        Assert.Null(error);
        return rule!;
    }

    private static string? ErrorOf(string json)
    {
        GiveawayRules.Read(JsonNode.Parse(json), out var error);
        return error;
    }

    // ── Reading the new rules ────────────────────────────────────────────────────────────

    [Fact]
    public void AJoinDateRuleKeepsItsDayAndWritesItBackTheSameWay()
    {
        var rule = Read("""{"kind":"groupJoinedBefore","date":"2026-06-01"}""");

        Assert.Equal(new DateOnly(2026, 6, 1), rule.Date);
        Assert.Equal("2026-06-01", GiveawayRules.Write(rule)["date"]!.GetValue<string>());
        Assert.Equal("joined the group before 1 Jun 2026", GiveawayRules.Describe(rule));
    }

    [Theory]
    [InlineData("""{"kind":"groupJoinedSince"}""")]
    [InlineData("""{"kind":"groupJoinedSince","date":"June"}""")]
    [InlineData("""{"kind":"groupJoinedSince","date":20260601}""")]
    public void AJoinDateRuleWithoutADayIsRefused(string json)
        => Assert.Equal("'groupJoinedSince' needs a day.", ErrorOf(json));

    [Fact]
    public void AModerationCountNamesOneOfTheFiveKinds()
    {
        var rule = Read("""{"kind":"moderationCount","id":"rejection","amount":2,"withinDays":30}""");

        Assert.Equal(ModerationKinds.Rejection, rule.Id);
        Assert.Equal("join request turned down 2 times or more in the last 30d", GiveawayRules.Describe(rule));
        Assert.Equal(
            "'moderationCount' needs a kind of moderation.",
            ErrorOf("""{"kind":"moderationCount","id":"glare","amount":1}"""));
    }

    [Fact]
    public void BannedOnceReadsAsOnce()
        => Assert.Equal(
            "banned once or more",
            GiveawayRules.Describe(Read("""{"kind":"moderationCount","id":"ban","amount":1}""")));

    /// <summary>The count is the repeat-offender numbers' own, from the same fact types.</summary>
    [Fact]
    public void EachModerationKindCountsTheFactsAProfileCounts()
    {
        Assert.Equal([FactType.MemberBanned], ModerationKinds.FactTypes(ModerationKinds.Ban));
        Assert.Equal([FactType.MemberKicked], ModerationKinds.FactTypes(ModerationKinds.Removal));
        Assert.Equal([FactType.GroupInstanceKick], ModerationKinds.FactTypes(ModerationKinds.InstanceKick));
        Assert.Equal([FactType.GroupInstanceWarn], ModerationKinds.FactTypes(ModerationKinds.Warn));
        Assert.Equal(
            [FactType.JoinRequestRejected, FactType.JoinRequestBlocked],
            ModerationKinds.FactTypes(ModerationKinds.Rejection));
    }

    [Fact]
    public void AListRuleNeedsAListAndWritesItsIdOneWay()
    {
        var rule = Read("""{"kind":"inList","id":"0192A8F0-0000-7000-8000-000000000001"}""");

        Assert.Equal(Regulars, rule.Id);
        Assert.Equal("Pick a list.", ErrorOf("""{"kind":"inList"}"""));
        Assert.Equal("Pick a list.", ErrorOf("""{"kind":"inList","id":"regulars"}"""));
    }

    [Fact]
    public void AListRuleSaysTheListsNameWhenItIsKnown()
    {
        var rule = Read($$"""{"kind":"inList","id":"{{Regulars}}"}""");

        Assert.Equal("in the list “Regulars”", GiveawayRules.Describe(rule, new Dictionary<string, string> { [Regulars] = "Regulars" }));
        Assert.Equal("in a list that no longer exists", GiveawayRules.Describe(rule, new Dictionary<string, string>()));
        Assert.Equal("in a saved list", GiveawayRules.Describe(rule));
    }

    [Fact]
    public void DaysSeenIsCountedOverAWindowAndLapsedIsNot()
    {
        Assert.Equal(
            "seen on 4 different days or more in the last 30d",
            GiveawayRules.Describe(Read("""{"kind":"daysSeen","amount":4,"withinDays":30}""")));

        Assert.Equal(
            "'notSeenWithinDays' is not counted over a window.",
            ErrorOf("""{"kind":"notSeenWithinDays","amount":30,"withinDays":10}"""));
    }

    // ── Lists standing in for themselves ─────────────────────────────────────────────────

    private static readonly IReadOnlyDictionary<string, GiveawayRule> Lists = new Dictionary<string, GiveawayRule>
    {
        [Regulars] = new()
        {
            Rules =
            [
                new GiveawayRule { Kind = GiveawayRuleKinds.InGroup },
                new GiveawayRule { Kind = GiveawayRuleKinds.DaysSeen, Amount = 4, WithinDays = 30 },
            ],
        },
        [Newcomers] = new()
        {
            Kind = GiveawayRuleKinds.AnyOf,
            Rules = [new GiveawayRule { Kind = GiveawayRuleKinds.GroupJoinedWithinDays, Amount = 30 }],
        },
    };

    /// <summary>
    /// "All of: in Regulars, linked" is "all of: in the group, seen on 4 days, linked": the list's
    /// own lines, flat, the way the Discord card should print them.
    /// </summary>
    [Fact]
    public void AListOfAllOfInsideAllOfIsWrittenFlat()
    {
        var rule = Read($$"""{"kind":"allOf","rules":[{"kind":"inList","id":"{{Regulars}}"},{"kind":"linkedAccounts"}]}""");

        var expanded = SavedListRules.Expand(rule, Lists);

        Assert.Equal(
            [GiveawayRuleKinds.InGroup, GiveawayRuleKinds.DaysSeen, GiveawayRuleKinds.LinkedAccounts],
            expanded.Rules.Select(r => r.Kind));
    }

    /// <summary>"None of: in Regulars" must stay "not (a and b)", never become "not a, not b".</summary>
    [Fact]
    public void AListUnderNoneOfKeepsItsOwnGroup()
    {
        var rule = Read($$"""{"kind":"noneOf","rules":[{"kind":"inList","id":"{{Regulars}}"}]}""");

        var expanded = SavedListRules.Expand(rule, Lists);

        var inner = Assert.Single(expanded.Rules);
        Assert.Equal(GiveawayRuleKinds.AllOf, inner.Kind);
        Assert.Equal(2, inner.Rules.Count);
    }

    [Fact]
    public void AnAnyOfListKeepsItsOwnGroupInsideAllOf()
    {
        var rule = Read($$"""{"kind":"allOf","rules":[{"kind":"inList","id":"{{Newcomers}}"}]}""");

        var inner = Assert.Single(SavedListRules.Expand(rule, Lists).Rules);
        Assert.Equal(GiveawayRuleKinds.AnyOf, inner.Kind);
    }

    /// <summary>A list that is gone stays a list rule, which the checker lets nobody through.</summary>
    [Fact]
    public void AListThatIsGoneIsLeftAsItIs()
    {
        var gone = "0192a8f0-0000-7000-8000-0000000000ff";
        var rule = Read($$"""{"kind":"allOf","rules":[{"kind":"inList","id":"{{gone}}"}]}""");

        var inner = Assert.Single(SavedListRules.Expand(rule, Lists).Rules);
        Assert.Equal(GiveawayRuleKinds.InList, inner.Kind);
        Assert.Equal(gone, inner.Id);
    }

    [Fact]
    public void ListsInFindsEveryListOnce()
    {
        var rule = Read($$"""
            {"kind":"allOf","rules":[
              {"kind":"inList","id":"{{Regulars}}"},
              {"kind":"anyOf","rules":[{"kind":"inList","id":"{{Newcomers}}"},{"kind":"inList","id":"{{Regulars}}"}]}
            ]}
            """);

        Assert.Equal([Regulars, Newcomers], GiveawayRules.ListsIn(rule));
    }

    /// <summary>
    /// A draw keeps its rules with every list written out, which can be deeper than a tree anybody
    /// may submit. Read back as "everyone" it would say the draw had no rules at all.
    /// </summary>
    [Fact]
    public void AStoredTreeDeeperThanTheLimitsIsStillReadAsItIs()
    {
        var deep = new GiveawayRule
        {
            Rules =
            [
                new GiveawayRule
                {
                    Kind = GiveawayRuleKinds.AnyOf,
                    Rules =
                    [
                        new GiveawayRule
                        {
                            Kind = GiveawayRuleKinds.NoneOf,
                            Rules =
                            [
                                new GiveawayRule
                                {
                                    Kind = GiveawayRuleKinds.AnyOf,
                                    Rules = [new GiveawayRule { Kind = GiveawayRuleKinds.InGroup }],
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        var stored = GiveawayRules.Store(deep);

        GiveawayRules.Read(JsonNode.Parse(stored), out var error);
        Assert.NotNull(error);

        var read = GiveawayRules.ReadStored(stored);
        Assert.False(read.LetsEveryoneIn);
        Assert.Equal(5, GiveawayRules.Count(read));
    }
}
