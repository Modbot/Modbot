using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Giveaways;
using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Giveaways;

/// <summary>
/// Lists design §3–§5: the rules saved lists added, each against the data it reads; a list naming
/// the same people as a giveaway with the same rules; and a list in a giveaway standing in for its
/// own rules.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class SavedListCheckerTests(PostgresFixture fixture) : GiveawayTestBase(fixture)
{
    private const string Alice = "usr_alice";
    private const string Bob = "usr_bob";
    private const string Carol = "usr_carol";

    private static GiveawayRule Rule(
        string kind, decimal? amount = null, int? withinDays = null, string? id = null, DateOnly? date = null)
        => new() { Kind = kind, Amount = amount, WithinDays = withinDays, Id = id, Date = date };

    private static GiveawayRule AllOf(params GiveawayRule[] rules) => new() { Rules = rules };

    private static string Key(string vrchat) => $"vrchat:{vrchat}";

    private async Task<GiveawayPeople> PeopleAsync(GiveawayRule rule)
    {
        await using var context = Database.NewContext();
        return await NewChecker(context).PeopleAsync(rule, Ct);
    }

    private async Task<Guid> AddListAsync(GiveawayRule rules, string name = "Regulars", bool deleted = false)
    {
        await using var context = Database.NewContext();

        var list = new SavedList
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Rules = GiveawayRules.Store(rules),
            CreatedAt = Now,
            UpdatedAt = Now,
            DeletedAt = deleted ? Now : null,
        };

        context.SavedLists.Add(list);
        await context.SaveChangesAsync(Ct);
        return list.Id;
    }

    private async Task ActedOnAsync(string vrchat, string type, int daysAgo)
    {
        await using var context = Database.NewContext();
        var at = Now.AddDays(-daysAgo);
        await new EventPartitionMaintainer(context, Clock).EnsureForAsync(at, Ct);

        await new FactWriter(context, Clock).WriteAsync(
            new FactRecord
            {
                Type = type,
                OccurredAt = at,
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = vrchat,
                Source = FactSource.AuditLog,
            },
            Ct);
    }

    // ── Membership ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NewThisMonthIsWhoJoinedTheGroupInTheLastNDays()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 10);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 100);

        Assert.Equal([Key(Alice)], await WhoMatchesAsync(Rule(GiveawayRuleKinds.GroupJoinedWithinDays, 30)));
    }

    [Fact]
    public async Task JoinedBeforeAndSinceSplitTheGroupOnOneDay()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 100);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 10);

        var day = DateOnly.FromDateTime(Now.AddDays(-30).UtcDateTime);

        Assert.Equal([Key(Alice)], await WhoMatchesAsync(Rule(GiveawayRuleKinds.GroupJoinedBefore, date: day)));
        Assert.Equal([Key(Bob)], await WhoMatchesAsync(Rule(GiveawayRuleKinds.GroupJoinedSince, date: day)));
    }

    [Fact]
    public async Task FirstSeenCountsFromTheFirstTimeModbotSawThem()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 100);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 100);

        await using (var context = Database.NewContext())
        {
            var bob = await context.VRChatUsers.SingleAsync(u => u.UserId == Bob, Ct);
            bob.FirstSeenAt = Now.AddDays(-200);
            await context.SaveChangesAsync(Ct);
        }

        Assert.Equal([Key(Alice)], await WhoMatchesAsync(Rule(GiveawayRuleKinds.FirstSeenWithinDays, 30)));
    }

    // ── Presence ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DaysSeenCountsDifferentDaysAndIsApproximate()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 100);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 100);

        await SeenAsync(Alice, hours: 1, daysAgo: 1);
        await SeenAsync(Alice, hours: 1, daysAgo: 3);
        await SeenAsync(Alice, hours: 1, daysAgo: 5);
        await SeenAsync(Bob, hours: 5, daysAgo: 2);

        var people = await PeopleAsync(Rule(GiveawayRuleKinds.DaysSeen, 3, withinDays: 30));

        Assert.Equal([Key(Alice)], people.People.Select(p => p.Person.Key));
        Assert.True(people.FromPolledData);
    }

    /// <summary>Lapsed is seen once and not lately. Somebody never seen has not lapsed.</summary>
    [Fact]
    public async Task LapsedIsSeenBeforeButNotLately()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 100);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 100);
        await AddPersonAsync(vrchat: Carol, inGroupDays: 100);

        await SeenAsync(Alice, hours: 2, daysAgo: 40);
        await SeenAsync(Bob, hours: 2, daysAgo: 2);

        Assert.Equal([Key(Alice)], await WhoMatchesAsync(Rule(GiveawayRuleKinds.NotSeenWithinDays, 30)));
    }

    // ── Moderation ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AModerationCountCountsOneKindOverItsWindow()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 100);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 100);
        await AddPersonAsync(vrchat: Carol, inGroupDays: 100);

        await ActedOnAsync(Alice, FactType.JoinRequestRejected, daysAgo: 5);
        await ActedOnAsync(Alice, FactType.JoinRequestBlocked, daysAgo: 6);
        await ActedOnAsync(Bob, FactType.JoinRequestRejected, daysAgo: 5);
        await ActedOnAsync(Bob, FactType.JoinRequestRejected, daysAgo: 200);
        await ActedOnAsync(Carol, FactType.MemberBanned, daysAgo: 5);
        await ActedOnAsync(Carol, FactType.MemberBanned, daysAgo: 6);

        Assert.Equal(
            [Key(Alice), Key(Bob)],
            await WhoMatchesAsync(Rule(GiveawayRuleKinds.ModerationCount, 2, id: ModerationKinds.Rejection)));

        Assert.Equal(
            [Key(Alice)],
            await WhoMatchesAsync(Rule(GiveawayRuleKinds.ModerationCount, 2, withinDays: 30, id: ModerationKinds.Rejection)));

        Assert.Equal(
            [Key(Carol)],
            await WhoMatchesAsync(Rule(GiveawayRuleKinds.ModerationCount, 2, id: ModerationKinds.Ban)));
    }

    // ── Retention ────────────────────────────────────────────────────────────────────────

    /// <summary>M7 §6: the quiet correctness failure, for the rules that read kept facts.</summary>
    [Fact]
    public async Task RulesReachingPastTheKeptFactsAreRefusedInWords()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 100);
        await RetentionAsync(moderationDays: 90, presenceDays: 90);

        var counted = await PeopleAsync(Rule(GiveawayRuleKinds.ModerationCount, 1, id: ModerationKinds.Ban));
        Assert.StartsWith("Modbot cannot answer “banned once or more”. Moderation history is kept for", counted.Unanswerable);

        var lapsed = await PeopleAsync(Rule(GiveawayRuleKinds.NotSeenWithinDays, 30));
        Assert.StartsWith("Modbot cannot answer", lapsed.Unanswerable);
        Assert.Contains("Presence history", lapsed.Unanswerable);

        var daysSeen = await PeopleAsync(Rule(GiveawayRuleKinds.DaysSeen, 3, withinDays: 30));
        Assert.Null(daysSeen.Unanswerable);
    }

    // ── Lists ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Giveaways design §2.6: a list and a giveaway with the same rules must name the same people.
    /// </summary>
    [Fact]
    public async Task AListNamesTheSamePeopleAsAGiveawayPreviewOfTheSameRules()
    {
        await AddPersonAsync(vrchat: Alice, name: "Alice", inGroupDays: 100);
        await AddPersonAsync(vrchat: Bob, name: "Bob", inGroupDays: 10);
        await AddPersonAsync(vrchat: Carol, name: "Carol", inGroupDays: 100);
        await SeenAsync(Alice, hours: 12, daysAgo: 2);
        await SeenAsync(Bob, hours: 12, daysAgo: 2);
        await SeenAsync(Carol, hours: 1, daysAgo: 2);

        var rules = AllOf(
            Rule(GiveawayRuleKinds.GroupMemberDays, 30),
            Rule(GiveawayRuleKinds.InstanceHours, 10, withinDays: 30));

        var people = await PeopleAsync(rules);

        await using var context = Database.NewContext();
        var preview = await NewChecker(context).PreviewAsync(
            rules, GiveawayExclusions.None, GiveawayWeights.Uniform, null, page: 1000, Ct);

        Assert.Equal(preview.InDraw, people.People.Count);
        Assert.Equal(preview.Total, people.Considered);
        Assert.Equal(
            preview.People.Where(p => p.KeptOut.Length == 0).Select(p => p.Key).Order(StringComparer.Ordinal),
            people.People.Select(p => p.Person.Key).Order(StringComparer.Ordinal));
        Assert.Equal([Key(Alice)], people.People.Select(p => p.Person.Key));
    }

    [Fact]
    public async Task APeopleListIsSortedByName()
    {
        await AddPersonAsync(vrchat: Alice, name: "zed", inGroupDays: 100);
        await AddPersonAsync(vrchat: Bob, name: "Amy", inGroupDays: 100);

        var people = await PeopleAsync(Rule(GiveawayRuleKinds.InGroup));

        Assert.Equal(["Amy", "zed"], people.People.Select(p => p.Person.Name!));
    }

    [Fact]
    public async Task AListInAGiveawayStandsInForItsOwnRules()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 100);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 10);

        var list = await AddListAsync(AllOf(Rule(GiveawayRuleKinds.GroupMemberDays, 30)));

        Assert.Equal(
            await WhoMatchesAsync(AllOf(Rule(GiveawayRuleKinds.GroupMemberDays, 30))),
            await WhoMatchesAsync(AllOf(Rule(GiveawayRuleKinds.InList, id: list.ToString("D")))));

        Assert.Equal(
            [Key(Bob)],
            await WhoMatchesAsync(new GiveawayRule
            {
                Kind = GiveawayRuleKinds.NoneOf,
                Rules = [Rule(GiveawayRuleKinds.InList, id: list.ToString("D"))],
            }));
    }

    /// <summary>A list that is gone lets nobody through. It must never quietly mean "everybody".</summary>
    [Fact]
    public async Task ADeletedListLetsNobodyThrough()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 100);

        var list = await AddListAsync(AllOf(), deleted: true);

        Assert.Empty(await WhoMatchesAsync(AllOf(Rule(GiveawayRuleKinds.InList, id: list.ToString("D")))));
    }

    /// <summary>
    /// Lists design §4.2: the draw keeps the rules with the list written out, so changing the list
    /// afterwards does not change what the draw says it was drawn from.
    /// </summary>
    [Fact]
    public async Task ADrawKeepsTheListsRulesWrittenOut()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 100);

        var list = await AddListAsync(AllOf(Rule(GiveawayRuleKinds.GroupMemberDays, 30)));
        var giveaway = await AddGiveawayAsync(rules: AllOf(Rule(GiveawayRuleKinds.InList, id: list.ToString("D"))));

        await using var context = Database.NewContext();
        var tracked = await context.Giveaways.SingleAsync(g => g.Id == giveaway.Id, Ct);
        var result = await NewDrawer(context).DrawAsync(tracked, null, Ct);

        Assert.Null(result.Problem);

        var kept = GiveawayRules.ReadStored(result.Draw!.Rules);
        Assert.Empty(GiveawayRules.ListsIn(kept));
        Assert.Equal(GiveawayRuleKinds.GroupMemberDays, Assert.Single(kept.Rules).Kind);
    }
}
