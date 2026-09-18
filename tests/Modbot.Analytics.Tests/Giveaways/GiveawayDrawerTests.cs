using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Giveaways;
using Modbot.Analytics.Retention;
using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Giveaways;

/// <summary>
/// Giveaways design §5: the entrant list is frozen and kept, the seed keeps the promise made
/// before it, the rules are checked again at the draw, and drawing again makes a new draw rather
/// than replacing the old one.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class GiveawayDrawerTests(PostgresFixture fixture) : GiveawayTestBase(fixture)
{
    private const string Alice = "usr_alice";
    private const string Bob = "usr_bob";
    private const string AliceDiscord = "1000000000000001";
    private const string BobDiscord = "1000000000000002";

    private async Task<GiveawayDrawResult> DrawAsync(Guid giveawayId, Guid? by = null)
    {
        await using var context = Database.NewContext();
        var giveaway = await context.Giveaways.SingleAsync(g => g.Id == giveawayId, Ct);
        return await NewDrawer(context).DrawAsync(giveaway, by, Ct);
    }

    private async Task<Giveaway> ReadAsync(Guid giveawayId)
    {
        await using var context = Database.NewContext();
        return await context.Giveaways.AsNoTracking().SingleAsync(g => g.Id == giveawayId, Ct);
    }

    // ── The snapshot ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheWholeEntrantListIsWrittenDownWithEveryWeight()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 10, name: "Alice");
        await AddPersonAsync(vrchat: Bob, inGroupDays: 10, name: "Bob");
        await SeenAsync(Alice, hours: 12, daysAgo: 2);
        await SeenAsync(Bob, hours: 3, daysAgo: 2);

        var giveaway = await AddGiveawayAsync(weighting: GiveawayWeights.InstanceHours);
        var result = await DrawAsync(giveaway.Id);

        Assert.Null(result.Problem);
        Assert.NotNull(result.Draw);

        var entrants = await EntrantsAsync(result.Draw.Id);

        Assert.Equal(2, entrants.Count);
        Assert.Equal([0, 1], entrants.Select(e => e.Position));
        Assert.Equal(12, entrants.Single(e => e.VRChatUserId == Alice).Weight);
        Assert.Equal(3, entrants.Single(e => e.VRChatUserId == Bob).Weight);
        Assert.Equal("Alice", entrants.Single(e => e.VRChatUserId == Alice).Name);
        Assert.Equal(15, result.Draw.TotalWeight);
    }

    [Fact]
    public async Task AnybodyKeptOutIsInTheListWithTheirWeightAtNought()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 10);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 10, banned: true);

        var giveaway = await AddGiveawayAsync(exclusions: new GiveawayExclusions { BannedMembers = true });
        var result = await DrawAsync(giveaway.Id);

        var entrants = await EntrantsAsync(result.Draw!.Id);

        Assert.Equal(2, result.Draw.EntrantCount);
        Assert.Equal(1, result.Draw.InDrawCount);

        var bob = entrants.Single(e => e.VRChatUserId == Bob);
        Assert.Equal(GiveawayKeptOut.Banned, bob.KeptOut);
        Assert.Equal(0, bob.Weight);
        Assert.Null(bob.WinnerRank);
    }

    /// <summary>
    /// §5.1: without the list the seed proves nothing. So the list has to be the list the draw
    /// actually ran on -- reproducing it from the stored rows must give the stored winner.
    /// </summary>
    [Fact]
    public async Task TheStoredSnapshotAndTheStoredSeedGiveTheStoredWinner()
    {
        for (var i = 0; i < 30; i++)
            await AddPersonAsync(vrchat: $"usr_{i:D3}", inGroupDays: 10);

        var giveaway = await AddGiveawayAsync(winners: 3);
        var result = await DrawAsync(giveaway.Id);

        var draw = result.Draw!;
        var entrants = await EntrantsAsync(draw.Id);

        var again = GiveawayDraws.Draw(
            [.. entrants.Select(e => new GiveawayTicket(e.Position, e.Key, e.Weight))],
            draw.WinnerCount,
            draw.Seed);

        Assert.Equal(
            entrants.Where(e => e.WinnerRank is not null).OrderBy(e => e.WinnerRank).Select(e => e.Key),
            again.Select(w => w.Key));
    }

    // ── The seed ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheRevealedSeedIsTheOnePromisedBeforeTheDraw()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 10);

        var giveaway = await AddGiveawayAsync();
        var promised = giveaway.SeedPromise;

        var result = await DrawAsync(giveaway.Id);

        Assert.Equal(promised, result.Draw!.SeedPromise);
        Assert.True(GiveawayDraws.Keeps(result.Draw.SeedPromise, result.Draw.Seed));
    }

    /// <summary>
    /// §5.2: the promise for the next draw stands before anybody has decided to make one, which is
    /// the only order in which a promise means anything.
    /// </summary>
    [Fact]
    public async Task AFreshPromiseIsStandingTheMomentTheOldSeedIsRevealed()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 10);

        var giveaway = await AddGiveawayAsync();
        var result = await DrawAsync(giveaway.Id);

        var after = await ReadAsync(giveaway.Id);

        Assert.NotEqual(result.Draw!.SeedPromise, after.SeedPromise);
        Assert.NotEmpty(after.SeedPromise);
        Assert.NotEmpty(after.SeedEncrypted);
        Assert.True(GiveawayDraws.Keeps(after.SeedPromise, Protector.Unprotect(after.SeedEncrypted)!));
    }

    [Fact]
    public async Task ASeedModbotCannotReadStopsTheDrawRatherThanChangingIt()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 10);

        var giveaway = await AddGiveawayAsync();

        await using (var context = Database.NewContext())
        {
            var row = await context.Giveaways.SingleAsync(g => g.Id == giveaway.Id, Ct);
            row.SeedEncrypted = "not-something-the-protector-wrote";
            await context.SaveChangesAsync(Ct);
        }

        var result = await DrawAsync(giveaway.Id);

        Assert.Equal("Modbot cannot read the seed it promised for this giveaway.", result.Problem);
        Assert.Null(result.Draw);
    }

    // ── Drawing again (§5.3) ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DrawingAgainMakesANewDrawAndLeavesTheOldOneAlone()
    {
        for (var i = 0; i < 20; i++)
            await AddPersonAsync(vrchat: $"usr_{i:D3}", inGroupDays: 10);

        var giveaway = await AddGiveawayAsync();

        var first = await DrawAsync(giveaway.Id);
        var second = await DrawAsync(giveaway.Id);

        Assert.Equal(1, first.Draw!.Number);
        Assert.Equal(2, second.Draw!.Number);
        Assert.NotEqual(first.Draw.Id, second.Draw.Id);
        Assert.NotEqual(first.Draw.Seed, second.Draw.Seed);

        await using var context = Database.NewContext();

        // Both draws, both entrant lists and both winners survive.
        Assert.Equal(2, await context.GiveawayDraws.CountAsync(d => d.GiveawayId == giveaway.Id, Ct));
        Assert.Equal(20, (await EntrantsAsync(first.Draw.Id)).Count);
        Assert.Equal(20, (await EntrantsAsync(second.Draw.Id)).Count);

        var stored = await context.GiveawayDraws.AsNoTracking().SingleAsync(d => d.Id == first.Draw.Id, Ct);
        Assert.Equal(first.Draw.Seed, stored.Seed);
    }

    [Fact]
    public async Task DrawingAgainUsesThePromiseThatWasStandingAfterTheFirstDraw()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 10);

        var giveaway = await AddGiveawayAsync();

        await DrawAsync(giveaway.Id);
        var between = await ReadAsync(giveaway.Id);
        var second = await DrawAsync(giveaway.Id);

        Assert.Equal(between.SeedPromise, second.Draw!.SeedPromise);
    }

    // ── The rules are checked again (§4.2) ───────────────────────────────────────────────

    /// <summary>
    /// Somebody can qualify on Monday and not on Friday, and the draw is the moment that matters.
    /// They stay in the list, marked, rather than disappearing from it.
    /// </summary>
    [Fact]
    public async Task SomebodyWhoQualifiedWhenTheyEnteredAndNoLongerDoesIsShownAsSuch()
    {
        await AddPersonAsync(vrchat: Alice, discord: AliceDiscord, inGroupDays: 100, inDiscordDays: 100, link: true);
        await AddPersonAsync(vrchat: Bob, discord: BobDiscord, inGroupDays: 100, inDiscordDays: 100, link: true);

        var rule = new GiveawayRule
        {
            Kind = GiveawayRuleKinds.NoneOf,
            Rules = [new GiveawayRule { Kind = GiveawayRuleKinds.GroupRole, Id = "grol_banned" }],
        };

        var giveaway = await AddGiveawayAsync(rules: rule, entryWay: GiveawayEntryWays.React);

        // Both reacted, and both qualified at the time.
        await ReactedAsync(giveaway.Id, AliceDiscord);
        await ReactedAsync(giveaway.Id, BobDiscord);

        // Bob picks up the role between reacting and the draw.
        await using (var context = Database.NewContext())
        {
            var member = await context.GroupMembers.SingleAsync(m => m.UserId == Bob, Ct);
            member.Roles = """["grol_banned"]""";
            await context.SaveChangesAsync(Ct);
        }

        var result = await DrawAsync(giveaway.Id);
        var entrants = await EntrantsAsync(result.Draw!.Id);

        Assert.Equal(2, entrants.Count);

        var bob = entrants.Single(e => e.VRChatUserId == Bob);
        Assert.Equal(GiveawayKeptOut.Rules, bob.KeptOut);
        Assert.Equal(0, bob.Weight);

        Assert.Equal(Alice, result.Winners.Single().VRChatUserId);
    }

    [Fact]
    public async Task SomebodyWhoWithdrewIsInTheListAndCannotWin()
    {
        await AddPersonAsync(vrchat: Alice, discord: AliceDiscord, inGroupDays: 10, inDiscordDays: 10, link: true);
        await AddPersonAsync(vrchat: Bob, discord: BobDiscord, inGroupDays: 10, inDiscordDays: 10, link: true);

        var giveaway = await AddGiveawayAsync(entryWay: GiveawayEntryWays.React);
        await ReactedAsync(giveaway.Id, AliceDiscord);
        await ReactedAsync(giveaway.Id, BobDiscord, withdrawn: true);

        var result = await DrawAsync(giveaway.Id);
        var entrants = await EntrantsAsync(result.Draw!.Id);

        Assert.Equal(2, entrants.Count);
        Assert.Equal(GiveawayKeptOut.Withdrawn, entrants.Single(e => e.VRChatUserId == Bob).KeptOut);
        Assert.Equal(Alice, result.Winners.Single().VRChatUserId);
    }

    [Fact]
    public async Task OnlyThePeopleWhoReactedAreEvenConsidered()
    {
        await AddPersonAsync(vrchat: Alice, discord: AliceDiscord, inGroupDays: 10, inDiscordDays: 10, link: true);
        await AddPersonAsync(vrchat: Bob, inGroupDays: 10);

        var giveaway = await AddGiveawayAsync(entryWay: GiveawayEntryWays.React);
        await ReactedAsync(giveaway.Id, AliceDiscord);

        var result = await DrawAsync(giveaway.Id);

        Assert.Equal(1, result.Draw!.EntrantCount);
    }

    // ── Refusals ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AGiveawayNobodyIsInIsNotDrawn()
    {
        var giveaway = await AddGiveawayAsync();
        var result = await DrawAsync(giveaway.Id);

        Assert.Equal("Nobody is in this giveaway.", result.Problem);
        Assert.Null(result.Draw);
    }

    [Fact]
    public async Task ADraftIsNotDrawn()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 10);

        var giveaway = await AddGiveawayAsync(state: GiveawayStates.Draft);
        var result = await DrawAsync(giveaway.Id);

        Assert.Equal("That giveaway is not open.", result.Problem);
    }

    /// <summary>
    /// The retention refusal reaches the draw and not only the preview: a draw on a partial answer
    /// would be a result nobody could defend.
    /// </summary>
    [Fact]
    public async Task ARuleReachingPastTheSurvivingFactsStopsTheDraw()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 400);
        await SeenAsync(Alice, hours: 50, daysAgo: 2);
        await RetentionAsync(moderationDays: 0, presenceDays: 90);

        var giveaway = await AddGiveawayAsync(
            rules: new GiveawayRule { Kind = GiveawayRuleKinds.InstanceHours, Amount = 10, WithinDays = 365 });

        var result = await DrawAsync(giveaway.Id);

        Assert.NotNull(result.Problem);
        Assert.Contains("kept for 90 days", result.Problem, StringComparison.Ordinal);
        Assert.Null(result.Draw);
    }

    // ── The fact ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheDrawIsAFactCarryingEverythingNeededToCheckIt()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 10, name: "Alice");

        var giveaway = await AddGiveawayAsync();
        var result = await DrawAsync(giveaway.Id);

        await using var context = Database.NewContext();
        var fact = await context.Events.AsNoTracking()
            .SingleAsync(e => e.Type == FactType.GiveawayDrawn && e.SubjectId == giveaway.Id.ToString(), Ct);

        Assert.Contains(result.Draw!.Seed, fact.Data, StringComparison.Ordinal);
        Assert.Contains(result.Draw.SeedPromise, fact.Data, StringComparison.Ordinal);
        Assert.Contains("\"winners\"", fact.Data, StringComparison.Ordinal);
        Assert.Contains("Alice", fact.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADrawnGiveawaySaysSo()
    {
        await AddPersonAsync(vrchat: Alice, inGroupDays: 10);

        var giveaway = await AddGiveawayAsync();
        await DrawAsync(giveaway.Id);

        var after = await ReadAsync(giveaway.Id);

        Assert.Equal(GiveawayStates.Drawn, after.State);
        Assert.Equal(1, after.DrawCount);
    }

    // ── Purging (§6.3) ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A purged person must never reappear as an entrant, and a past draw must still be
    /// checkable. Both, at once, is the whole of the requirement.
    /// </summary>
    [Fact]
    public async Task APurgedPersonLeavesTheirWeightBehindAndNothingElse()
    {
        await AddPersonAsync(vrchat: Alice, discord: AliceDiscord, inGroupDays: 10, inDiscordDays: 10, link: true, name: "Alice");
        await AddPersonAsync(vrchat: Bob, discord: BobDiscord, inGroupDays: 10, inDiscordDays: 10, link: true, name: "Bob");

        var giveaway = await AddGiveawayAsync(entryWay: GiveawayEntryWays.React);
        await ReactedAsync(giveaway.Id, AliceDiscord);
        await ReactedAsync(giveaway.Id, BobDiscord);

        var result = await DrawAsync(giveaway.Id);
        var before = await EntrantsAsync(result.Draw!.Id);
        var aliceWeight = before.Single(e => e.VRChatUserId == Alice).Weight;

        await using (var context = Database.NewContext())
        {
            var purger = new UserPurger(
                context,
                Clock,
                new DailyTotalsJob(context, Clock),
                new FactWriter(context, Clock),
                new EventPartitionMaintainer(context, Clock));

            var purge = await purger.PurgeAsync(FactPlatform.Discord, AliceDiscord, Ct);

            Assert.Equal(1, purge.GiveawayEntriesDeleted);
            Assert.Equal(1, purge.GiveawayEntrantsBlanked);
        }

        // The standing entry is gone, so she cannot be drawn again.
        await using (var context = Database.NewContext())
        {
            Assert.False(await context.GiveawayEntries.AnyAsync(e => e.DiscordUserId == AliceDiscord, Ct));
        }

        // The old snapshot keeps its arithmetic and loses the person.
        var after = await EntrantsAsync(result.Draw.Id);
        var purged = after.Single(e => e.Purged);

        Assert.Equal(aliceWeight, purged.Weight);
        Assert.Null(purged.Name);
        Assert.Null(purged.VRChatUserId);
        Assert.Null(purged.DiscordUserId);
        Assert.Equal(string.Empty, purged.Key);

        // Bob is untouched, and the list is still the same length and in the same order.
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before.Select(e => e.Position), after.Select(e => e.Position));
        Assert.Equal(before.Select(e => e.Weight), after.Select(e => e.Weight));
        Assert.Equal("Bob", after.Single(e => e.VRChatUserId == Bob).Name);
    }

    [Fact]
    public async Task APurgedPersonIsNotAnEntrantInANewDraw()
    {
        await AddPersonAsync(vrchat: Alice, discord: AliceDiscord, inGroupDays: 10, inDiscordDays: 10, link: true);
        await AddPersonAsync(vrchat: Bob, discord: BobDiscord, inGroupDays: 10, inDiscordDays: 10, link: true);

        var giveaway = await AddGiveawayAsync(entryWay: GiveawayEntryWays.React);
        await ReactedAsync(giveaway.Id, AliceDiscord);
        await ReactedAsync(giveaway.Id, BobDiscord);

        await using (var context = Database.NewContext())
        {
            await new UserPurger(
                context,
                Clock,
                new DailyTotalsJob(context, Clock),
                new FactWriter(context, Clock),
                new EventPartitionMaintainer(context, Clock))
                .PurgeAsync(FactPlatform.Discord, AliceDiscord, Ct);
        }

        var result = await DrawAsync(giveaway.Id);
        var entrants = await EntrantsAsync(result.Draw!.Id);

        Assert.Equal(1, result.Draw.EntrantCount);
        Assert.Equal(Bob, Assert.Single(entrants).VRChatUserId);
    }
}
