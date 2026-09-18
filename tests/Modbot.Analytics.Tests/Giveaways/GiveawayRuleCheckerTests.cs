using Modbot.Analytics.Giveaways;
using Modbot.Core.Giveaways;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Giveaways;

/// <summary>
/// Giveaways design §2.2: each rule against the data it reads, and the combining words over them.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class GiveawayRuleCheckerTests(PostgresFixture fixture) : GiveawayTestBase(fixture)
{
    private const string Alice = "usr_alice";
    private const string Bob = "usr_bob";
    private const string AliceDiscord = "1000000000000001";
    private const string BobDiscord = "1000000000000002";

    private static GiveawayRule Rule(string kind, decimal? amount = null, int? withinDays = null, string? id = null)
        => new() { Kind = kind, Amount = amount, WithinDays = withinDays, Id = id };

    private static GiveawayRule Combined(string kind, params GiveawayRule[] rules)
        => new() { Kind = kind, Rules = rules };

    // ── Membership ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InTheGroupNowFindsMembersAndNotPeopleWhoLeft()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 100);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 100, leftGroup: true);

        Assert.Equal([$"vrchat:{Alice}"], await WhoMatchesAsync(Rule(GiveawayRuleKinds.InGroup)));
    }

    [Fact]
    public async Task GroupTenureCountsFromTheJoinDateVRChatStates()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 100);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 10);

        Assert.Equal(
            [$"vrchat:{Alice}"],
            await WhoMatchesAsync(Rule(GiveawayRuleKinds.GroupMemberDays, 30)));
    }

    [Fact]
    public async Task DiscordTenureCountsFromTheStoredJoinDate()
    {
        await AddPersonAsync(discord: AliceDiscord, inDiscordDays: 100);
        await AddPersonAsync(discord: BobDiscord, inDiscordDays: 5);

        Assert.Equal(
            [$"discord:{AliceDiscord}"],
            await WhoMatchesAsync(Rule(GiveawayRuleKinds.DiscordMemberDays, 30)));
    }

    [Fact]
    public async Task AVRChatAccountsAgeComesFromTheJoinDateModbotStores()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 1, accountDays: 900);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 1, accountDays: 10);

        Assert.Equal(
            [$"vrchat:{Alice}"],
            await WhoMatchesAsync(Rule(GiveawayRuleKinds.VRChatAccountDays, 365)));
    }

    /// <summary>
    /// An account whose profile Modbot has never read has no join date, and the honest answer is
    /// "no", not "probably old enough".
    /// </summary>
    [Fact]
    public async Task AnAccountWithNoKnownJoinDateDoesNotPass()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 1);

        Assert.Empty(await WhoMatchesAsync(Rule(GiveawayRuleKinds.VRChatAccountDays, 1)));
    }

    [Fact]
    public async Task RolesAreMatchedOnBothSides()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 10, groupRoles: ["grol_regulars"]);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 10, groupRoles: ["grol_other"]);
        await AddPersonAsync(discord: AliceDiscord, inDiscordDays: 10, discordRoles: ["777"]);

        Assert.Equal(
            [$"vrchat:{Alice}"],
            await WhoMatchesAsync(Rule(GiveawayRuleKinds.GroupRole, id: "grol_regulars")));

        Assert.Equal(
            [$"discord:{AliceDiscord}"],
            await WhoMatchesAsync(Rule(GiveawayRuleKinds.DiscordRole, id: "777")));
    }

    [Fact]
    public async Task LinkedAccountsFindsOnlyTheLinkedPair()
    {
        await AddPersonAsync(vrchat: Alice, discord: AliceDiscord, inGroupDays: 10, inDiscordDays: 10, link: true);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 10);

        Assert.Equal(
            [$"vrchat:{Alice}"],
            await WhoMatchesAsync(Rule(GiveawayRuleKinds.LinkedAccounts)));
    }

    // ── Presence ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HoursInOurInstancesAddsUpEverySession()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 10);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 10);

        await SeenAsync(Alice, hours: 4, daysAgo: 3, instance: "1");
        await SeenAsync(Alice, hours: 4, daysAgo: 2, instance: "2");
        await SeenAsync(Bob, hours: 4, daysAgo: 2, instance: "2");

        Assert.Equal(
            [$"vrchat:{Alice}"],
            await WhoMatchesAsync(Rule(GiveawayRuleKinds.InstanceHours, 6)));
    }

    /// <summary>
    /// The total and the longest single stay are different questions, and the spec says to offer
    /// both because they describe different people.
    /// </summary>
    [Fact]
    public async Task HoursInOneSingleInstanceIsNotTheTotal()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 10);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 10);

        // Alice: eight hours over two evenings. Bob: six hours in one.
        await SeenAsync(Alice, hours: 4, daysAgo: 3, instance: "1");
        await SeenAsync(Alice, hours: 4, daysAgo: 2, instance: "2");
        await SeenAsync(Bob, hours: 6, daysAgo: 2, instance: "3");

        Assert.Equal(
            [$"vrchat:{Alice}", $"vrchat:{Bob}"],
            await WhoMatchesAsync(Rule(GiveawayRuleKinds.InstanceHours, 6)));

        Assert.Equal(
            [$"vrchat:{Bob}"],
            await WhoMatchesAsync(Rule(GiveawayRuleKinds.OneInstanceHours, 6)));
    }

    [Fact]
    public async Task AWindowLeavesOutWhatHappenedBeforeIt()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 400);

        await SeenAsync(Alice, hours: 20, daysAgo: 200, instance: "1");
        await SeenAsync(Alice, hours: 2, daysAgo: 3, instance: "2");

        Assert.Equal(
            [$"vrchat:{Alice}"],
            await WhoMatchesAsync(Rule(GiveawayRuleKinds.InstanceHours, 10)));

        Assert.Empty(await WhoMatchesAsync(Rule(GiveawayRuleKinds.InstanceHours, 10, withinDays: 30)));
    }

    [Fact]
    public async Task SeenInTheLastSoManyDaysUsesTheLastTimeTheyWereSeen()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 400);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 400);

        await SeenAsync(Alice, hours: 1, daysAgo: 5);
        await SeenAsync(Bob, hours: 1, daysAgo: 200);

        Assert.Equal(
            [$"vrchat:{Alice}"],
            await WhoMatchesAsync(Rule(GiveawayRuleKinds.SeenWithinDays, 30)));
    }

    /// <summary>
    /// Nobody has been seen at all, which is a different thing from being seen a long time ago and
    /// must not read as "seen today".
    /// </summary>
    [Fact]
    public async Task SomebodyNeverSeenAtAllIsNotSeenRecently()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 10);

        Assert.Empty(await WhoMatchesAsync(Rule(GiveawayRuleKinds.SeenWithinDays, 3650)));
    }

    // ── Discord daily totals ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task VoiceHoursAreSummedFromTheDailyTotalInMinutes()
    {
        await AddPersonAsync(discord: AliceDiscord, inDiscordDays: 10);
        await AddPersonAsync(discord: BobDiscord, inDiscordDays: 10);

        await VoiceMinutesAsync(AliceDiscord, 400, daysAgo: 3);
        await VoiceMinutesAsync(AliceDiscord, 400, daysAgo: 2);
        await VoiceMinutesAsync(BobDiscord, 30, daysAgo: 2);

        // Alice: 800 minutes, which is 13 hours and a third.
        Assert.Equal(
            [$"discord:{AliceDiscord}"],
            await WhoMatchesAsync(Rule(GiveawayRuleKinds.VoiceHours, 13)));
    }

    [Fact]
    public async Task MessagesAreSummedFromTheDailyTotal()
    {
        await AddPersonAsync(discord: AliceDiscord, inDiscordDays: 10);
        await AddPersonAsync(discord: BobDiscord, inDiscordDays: 10);

        await MessagesAsync(AliceDiscord, 60, daysAgo: 3);
        await MessagesAsync(AliceDiscord, 60, daysAgo: 2);
        await MessagesAsync(BobDiscord, 5, daysAgo: 2);

        Assert.Equal(
            [$"discord:{AliceDiscord}"],
            await WhoMatchesAsync(Rule(GiveawayRuleKinds.Messages, 100)));
    }

    // ── Trouble ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoTroubleLeavesOutAnybodyWithABanOnEitherSide()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 10);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 10);
        await AddPersonAsync(discord: BobDiscord, inDiscordDays: 10);

        await TroubleAsync(Bob, Core.Data.Entities.FactPlatform.VRChat, daysAgo: 5);
        await TroubleAsync(BobDiscord, Core.Data.Entities.FactPlatform.Discord, daysAgo: 5);

        Assert.Equal(
            [$"vrchat:{Alice}"],
            await WhoMatchesAsync(Rule(GiveawayRuleKinds.NoTrouble)));
    }

    [Fact]
    public async Task NoTroubleInAWindowForgetsOlderTrouble()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 400);
        await TroubleAsync(Alice, Core.Data.Entities.FactPlatform.VRChat, daysAgo: 300);

        Assert.Empty(await WhoMatchesAsync(Rule(GiveawayRuleKinds.NoTrouble)));

        Assert.Equal(
            [$"vrchat:{Alice}"],
            await WhoMatchesAsync(Rule(GiveawayRuleKinds.NoTrouble, withinDays: 30)));
    }

    // ── Combining ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AllOfNeedsEveryRuleInside()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 100, accountDays: 900);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 100, accountDays: 10);

        var rule = Combined(
            GiveawayRuleKinds.AllOf,
            Rule(GiveawayRuleKinds.GroupMemberDays, 30),
            Rule(GiveawayRuleKinds.VRChatAccountDays, 365));

        Assert.Equal([$"vrchat:{Alice}"], await WhoMatchesAsync(rule));
    }

    [Fact]
    public async Task AnyOfNeedsOneRuleInside()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 100, accountDays: 10);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 1, accountDays: 900);

        var rule = Combined(
            GiveawayRuleKinds.AnyOf,
            Rule(GiveawayRuleKinds.GroupMemberDays, 30),
            Rule(GiveawayRuleKinds.VRChatAccountDays, 365));

        Assert.Equal([$"vrchat:{Alice}", $"vrchat:{Bob}"], await WhoMatchesAsync(rule));
    }

    [Fact]
    public async Task NoneOfIsHowARuleIsNegated()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 10, groupRoles: ["grol_staff"]);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 10);

        var rule = Combined(GiveawayRuleKinds.NoneOf, Rule(GiveawayRuleKinds.GroupRole, id: "grol_staff"));

        Assert.Equal([$"vrchat:{Bob}"], await WhoMatchesAsync(rule));
    }

    [Fact]
    public async Task GroupsNestOneLevelDown()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 100, groupRoles: ["grol_regulars"]);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 100, accountDays: 900);
        await AddPersonAsync(vrchat: "usr_carol", inGroupDays: 100);

        var rule = Combined(
            GiveawayRuleKinds.AllOf,
            Rule(GiveawayRuleKinds.GroupMemberDays, 30),
            Combined(
                GiveawayRuleKinds.AnyOf,
                Rule(GiveawayRuleKinds.GroupRole, id: "grol_regulars"),
                Rule(GiveawayRuleKinds.VRChatAccountDays, 365)));

        Assert.Equal([$"vrchat:{Alice}", $"vrchat:{Bob}"], await WhoMatchesAsync(rule));
    }

    // ── Saying why ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SomebodyWhoDoesNotPassIsToldWhichRuleTheyFailed()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 3);

        await using var context = Database.NewContext();
        var match = await NewChecker(context).SnapshotAsync(
            Rule(GiveawayRuleKinds.GroupMemberDays, 30),
            GiveawayExclusions.None,
            GiveawayWeights.Uniform,
            null,
            entrants: null,
            Ct);

        var alice = Assert.Single(match.People);
        Assert.Equal(GiveawayKeptOut.Rules, alice.KeptOut);
        Assert.Equal("in the group for 30 days or more", alice.Because);
    }

    /// <summary>
    /// §6.1: a rule answered from polled presence says so, and a measurement within a tenth of the
    /// threshold is a close call rather than a settled answer.
    /// </summary>
    [Fact]
    public async Task APresenceFigureNearTheThresholdIsACloseCall()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 10);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 10);

        await SeenAsync(Alice, hours: 10.2, daysAgo: 2, instance: "1");
        await SeenAsync(Bob, hours: 40, daysAgo: 2, instance: "2");

        await using var context = Database.NewContext();
        var match = await NewChecker(context).SnapshotAsync(
            Rule(GiveawayRuleKinds.InstanceHours, 10),
            GiveawayExclusions.None,
            GiveawayWeights.Uniform,
            null,
            entrants: null,
            Ct);

        Assert.True(match.FromPolledData);
        Assert.Equal(1, match.CloseCalls);

        var alice = match.People.Single(p => p.VRChatUserId == Alice);
        Assert.True(alice.CloseCall);
        Assert.True(alice.FromPolledData);

        Assert.False(match.People.Single(p => p.VRChatUserId == Bob).CloseCall);
    }

    /// <summary>
    /// Voice and messages come from daily totals, which are exact counts of what was recorded, so
    /// nothing about them is approximate and nothing should be marked as such.
    /// </summary>
    [Fact]
    public async Task ADiscordFigureIsNeverMarkedApproximate()
    {
        await AddPersonAsync(discord: AliceDiscord, inDiscordDays: 10);
        await VoiceMinutesAsync(AliceDiscord, 601, daysAgo: 2);

        await using var context = Database.NewContext();
        var match = await NewChecker(context).SnapshotAsync(
            Rule(GiveawayRuleKinds.VoiceHours, 10),
            GiveawayExclusions.None,
            GiveawayWeights.Uniform,
            null,
            entrants: null,
            Ct);

        Assert.False(match.FromPolledData);
        Assert.Equal(0, match.CloseCalls);
    }

    // ── Exclusions ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnExcludedPersonIsInTheListWithTheirReason()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 10, banned: true);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 10);

        await using var context = Database.NewContext();
        var match = await NewChecker(context).SnapshotAsync(
            GiveawayRule.Everyone,
            new GiveawayExclusions { BannedMembers = true },
            GiveawayWeights.Uniform,
            null,
            entrants: null,
            Ct);

        Assert.Equal(2, match.Total);
        Assert.Equal(1, match.InDraw);

        var alice = match.People.Single(p => p.VRChatUserId == Alice);
        Assert.Equal(GiveawayKeptOut.Banned, alice.KeptOut);
        Assert.Equal(0, alice.Weight);
    }

    /// <summary>
    /// An exclusion has to beat the rules in the reason it gives: "staff" and "does not pass the
    /// rules" are different answers and the first is the one a person deserves.
    /// </summary>
    [Fact]
    public async Task AnExclusionBeatsTheRulesInTheReasonGiven()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 1, banned: true);

        await using var context = Database.NewContext();
        var match = await NewChecker(context).SnapshotAsync(
            Rule(GiveawayRuleKinds.GroupMemberDays, 365),
            new GiveawayExclusions { BannedMembers = true },
            GiveawayWeights.Uniform,
            null,
            entrants: null,
            Ct);

        Assert.Equal(GiveawayKeptOut.Banned, Assert.Single(match.People).KeptOut);
    }

    // ── Weighting ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WeightsAreCountedFromTheNumberAndHeldDownByTheCap()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 10);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 10);
        await AddPersonAsync(vrchat: "usr_carol", inGroupDays: 10);

        await SeenAsync(Alice, hours: 400, daysAgo: 2, instance: "1");
        await SeenAsync(Bob, hours: 5, daysAgo: 2, instance: "2");
        // Carol has never been seen, so her measured number is nought.

        await using var context = Database.NewContext();
        var match = await NewChecker(context).SnapshotAsync(
            GiveawayRule.Everyone,
            GiveawayExclusions.None,
            GiveawayWeights.InstanceHours,
            weightCap: 20,
            entrants: null,
            Ct);

        Assert.Equal(20, match.People.Single(p => p.VRChatUserId == Alice).Weight);
        Assert.Equal(5, match.People.Single(p => p.VRChatUserId == Bob).Weight);

        // The floor of one: qualifying with no hours recorded is still being in the draw.
        Assert.Equal(1, match.People.Single(p => p.VRChatUserId == "usr_carol").Weight);

        Assert.Equal(26, match.TotalWeight);
    }

    // ── Retention (§6.2) ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// M7 §6's most likely quiet correctness failure, and the test it asks for by name. The window
    /// reaches past the surviving facts, so there is no answer -- not a smaller one.
    /// </summary>
    [Fact]
    public async Task ARuleReachingPastTheSurvivingFactsIsRefusedRatherThanAnsweredPartly()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 400);
        await SeenAsync(Alice, hours: 50, daysAgo: 2);

        await RetentionAsync(moderationDays: 0, presenceDays: 90);

        await using var context = Database.NewContext();
        var match = await NewChecker(context).PreviewAsync(
            Rule(GiveawayRuleKinds.InstanceHours, 10, withinDays: 365),
            GiveawayExclusions.None,
            GiveawayWeights.Uniform,
            null,
            ct: Ct);

        Assert.NotNull(match.Unanswerable);
        Assert.Contains("kept for 90 days", match.Unanswerable, StringComparison.Ordinal);
        Assert.Contains("365", match.Unanswerable, StringComparison.Ordinal);

        // And nothing that looks like an answer came back with it.
        Assert.Empty(match.People);
        Assert.Equal(0, match.InDraw);
    }

    [Fact]
    public async Task AnAllTimePresenceRuleIsRefusedWhenPresenceIsBeingPruned()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 400);
        await RetentionAsync(moderationDays: 0, presenceDays: 90);

        await using var context = Database.NewContext();
        var match = await NewChecker(context).PreviewAsync(
            Rule(GiveawayRuleKinds.InstanceHours, 10),
            GiveawayExclusions.None,
            GiveawayWeights.Uniform,
            null,
            ct: Ct);

        Assert.NotNull(match.Unanswerable);
        Assert.Contains("all of it", match.Unanswerable, StringComparison.Ordinal);
    }

    /// <summary>
    /// The default configuration is the one where this cannot happen, which is exactly why the
    /// configured case above is tested deliberately (M7 §6).
    /// </summary>
    [Fact]
    public async Task NothingIsRefusedWhenNothingIsBeingPruned()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 400);
        await SeenAsync(Alice, hours: 50, daysAgo: 2);

        await using var context = Database.NewContext();
        var match = await NewChecker(context).PreviewAsync(
            Rule(GiveawayRuleKinds.InstanceHours, 10, withinDays: 3650),
            GiveawayExclusions.None,
            GiveawayWeights.Uniform,
            null,
            ct: Ct);

        Assert.Null(match.Unanswerable);
        Assert.Equal(1, match.InDraw);
    }

    /// <summary>
    /// Daily totals outlive the facts they were computed from, so a question answered from one is
    /// answerable however short the retention window is. §5's "prefer daily totals" earning out.
    /// </summary>
    [Fact]
    public async Task ADailyTotalRuleIsNeverRefusedForRetention()
    {
        await AddPersonAsync(discord: AliceDiscord, inDiscordDays: 400);
        await VoiceMinutesAsync(AliceDiscord, 6000, daysAgo: 300);

        await RetentionAsync(moderationDays: 30, presenceDays: 30);

        await using var context = Database.NewContext();
        var match = await NewChecker(context).PreviewAsync(
            Rule(GiveawayRuleKinds.VoiceHours, 10, withinDays: 3650),
            GiveawayExclusions.None,
            GiveawayWeights.Uniform,
            null,
            ct: Ct);

        Assert.Null(match.Unanswerable);
        Assert.Equal(1, match.InDraw);
    }

    [Fact]
    public async Task AModerationRulePastItsWindowIsRefusedToo()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 400);
        await RetentionAsync(moderationDays: 365, presenceDays: 0);

        await using var context = Database.NewContext();
        var match = await NewChecker(context).PreviewAsync(
            Rule(GiveawayRuleKinds.NoTrouble, withinDays: 1000),
            GiveawayExclusions.None,
            GiveawayWeights.Uniform,
            null,
            ct: Ct);

        Assert.NotNull(match.Unanswerable);
        Assert.Contains("Moderation history", match.Unanswerable, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWeightCountedFromPresenceIsRefusedWhenPresenceIsBeingPruned()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 10);
        await RetentionAsync(moderationDays: 0, presenceDays: 90);

        await using var context = Database.NewContext();
        var match = await NewChecker(context).PreviewAsync(
            GiveawayRule.Everyone,
            GiveawayExclusions.None,
            GiveawayWeights.InstanceHours,
            null,
            ct: Ct);

        Assert.NotNull(match.Unanswerable);
        Assert.Contains("Hours in our instances", match.Unanswerable, StringComparison.Ordinal);
    }

    // ── The count and the page (§2.4) ────────────────────────────────────────────────────

    [Fact]
    public async Task ThePreviewCountsEverybodyAndReturnsOnlyAPage()
    {
        for (var i = 0; i < 30; i++)
            await AddPersonAsync(vrchat: $"usr_{i:D3}", inGroupDays: 10);

        await using var context = Database.NewContext();
        var match = await NewChecker(context).PreviewAsync(
            GiveawayRule.Everyone,
            GiveawayExclusions.None,
            GiveawayWeights.Uniform,
            null,
            page: 5,
            Ct);

        Assert.Equal(30, match.Total);
        Assert.Equal(30, match.InDraw);
        Assert.Equal(5, match.People.Count);
    }

    // ── Who is even considered (§2.5) ────────────────────────────────────────────────────

    [Fact]
    public async Task ALinkedPersonIsOneEntrantAndNotTwo()
    {
        await AddPersonAsync(
            vrchat: Alice, discord: AliceDiscord, inGroupDays: 10, inDiscordDays: 10, link: true);

        var who = await WhoMatchesAsync(GiveawayRule.Everyone);

        Assert.Equal([$"vrchat:{Alice}"], who);
    }

    /// <summary>
    /// A rule needing VRChat data simply fails for somebody Modbot has no VRChat account for, and
    /// it fails visibly rather than by leaving them out of the list.
    /// </summary>
    [Fact]
    public async Task ARuleNeedingVRChatDataFailsVisiblyForSomebodyWithNoVRChatAccount()
    {
        await AddPersonAsync(discord: AliceDiscord, inDiscordDays: 10);

        await using var context = Database.NewContext();
        var match = await NewChecker(context).SnapshotAsync(
            Rule(GiveawayRuleKinds.InstanceHours, 1),
            GiveawayExclusions.None,
            GiveawayWeights.Uniform,
            null,
            entrants: null,
            Ct);

        var only = Assert.Single(match.People);
        Assert.Equal(GiveawayKeptOut.Rules, only.KeptOut);
        Assert.Equal("1 hours or more in our instances", only.Because);
    }
}
