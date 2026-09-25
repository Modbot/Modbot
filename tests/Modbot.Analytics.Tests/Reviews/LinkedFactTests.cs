using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Reviews;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Reviews;

/// <summary>
/// Spec 5.3.2: one decision that leaves several facts behind is counted once, and is still
/// readable as every fact it left.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class LinkedFactTests : ReviewTestBase
{
    public LinkedFactTests(PostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ABanAndTheInstanceKickItCaused_CountAsOneAction()
    {
        await WriteAsync(
            Fact(FactType.MemberBanned, Start.AddDays(-1), subjectId: "usr_p", actorId: "alice"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-1).AddSeconds(1), subjectId: "usr_p", actorId: "alice",
                worldId: "wrld_a", instanceId: "1"));

        await RunAsync();

        var row = await OffenderAsync("usr_p");
        Assert.NotNull(row);

        // One decision. The ban is what counts, and the kick that carried it out is not a second
        // mark against the person or a second action by the moderator.
        Assert.Equal(1, row.Actions);
        Assert.Equal(1, row.Bans);
        Assert.Equal(0, row.InstanceKicks);
        Assert.Equal(RepeatOffenderStatus.Once, row.Status);

        var links = await LinksAsync();
        var link = Assert.Single(links);

        var ban = Assert.Single(await FactsAsync(FactType.MemberBanned));
        var kick = Assert.Single(await FactsAsync(FactType.GroupInstanceKick));

        Assert.Equal(kick.Id, link.FactId);
        Assert.Equal(ban.Id, link.MainFactId);
    }

    [Fact]
    public async Task TheKickArrivingFirst_FromAnotherSource_StillLinksToTheBan()
    {
        // The moderator's client reports the kick; VRChat's audit log reports the ban a poll
        // later. Different sources, out of order, and the same decision.
        await WriteAsync(Fact(
            FactType.GroupInstanceKick,
            Start.AddDays(-1).AddSeconds(2),
            subjectId: "usr_p",
            actorId: "alice",
            source: FactSource.Companion,
            worldId: "wrld_a",
            instanceId: "1"));

        Clock.Advance(TimeSpan.FromMinutes(5));

        await WriteAsync(Fact(
            FactType.MemberBanned,
            Start.AddDays(-1),
            subjectId: "usr_p",
            actorId: "alice",
            source: FactSource.AuditLog));

        await RunAsync();

        var ban = Assert.Single(await FactsAsync(FactType.MemberBanned));
        var kick = Assert.Single(await FactsAsync(FactType.GroupInstanceKick));

        var link = Assert.Single(await LinksAsync());
        Assert.Equal(kick.Id, link.FactId);
        Assert.Equal(ban.Id, link.MainFactId);

        Assert.Equal(1, (await OffenderAsync("usr_p"))!.Actions);
    }

    [Fact]
    public async Task TwoSeparateDecisions_MinutesApart_CountTwice()
    {
        var first = Start.AddDays(-1);

        await WriteAsync(
            Fact(FactType.MemberBanned, first, subjectId: "usr_p", actorId: "alice"),
            Fact(FactType.GroupInstanceKick, first.AddSeconds(1), subjectId: "usr_p", actorId: "alice",
                worldId: "wrld_a", instanceId: "1"),
            Fact(FactType.MemberUnbanned, first.AddMinutes(5), subjectId: "usr_p", actorId: "alice"),
            Fact(FactType.MemberBanned, first.AddMinutes(10), subjectId: "usr_p", actorId: "alice"),
            Fact(FactType.GroupInstanceKick, first.AddMinutes(10).AddSeconds(1), subjectId: "usr_p", actorId: "alice",
                worldId: "wrld_a", instanceId: "1"));

        await RunAsync();

        var row = await OffenderAsync("usr_p");
        Assert.NotNull(row);

        // Two bans, each with its own kick: two decisions, two actions -- not one, and not four.
        Assert.Equal(2, row.Actions);
        Assert.Equal(2, row.Bans);
        Assert.Equal(0, row.InstanceKicks);

        var links = await LinksAsync();
        Assert.Equal(2, links.Count);
        Assert.Equal(2, links.Select(l => l.MainFactId).Distinct().Count());
    }

    [Fact]
    public async Task AKickOnItsOwn_IsNotLinkedAndStillCounts()
    {
        await WriteAsync(
            Fact(FactType.GroupInstanceKick, Start.AddDays(-1), subjectId: "usr_p", actorId: "alice"),
            Fact(FactType.MemberBanned, Start.AddDays(-1).AddMinutes(30), subjectId: "usr_other", actorId: "alice"));

        await RunAsync();

        Assert.Empty(await LinksAsync());

        var row = await OffenderAsync("usr_p");
        Assert.NotNull(row);
        Assert.Equal(1, row.Actions);
        Assert.Equal(1, row.InstanceKicks);
    }

    [Fact]
    public async Task AFactWhoseTimeIsOnlyAWindow_IsNeverLinked()
    {
        // A sync diff knows only that something happened between two polls (spec 5.3). "At the
        // same moment as" is not a question that time can answer, so it is not asked.
        await WriteAsync(
            Fact(FactType.MemberBanned, Start.AddDays(-1), Start.AddDays(-1).AddMinutes(5),
                subjectId: "usr_p", source: FactSource.SyncDiff),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-1).AddSeconds(1), subjectId: "usr_p", actorId: "alice"));

        await RunAsync();

        Assert.Empty(await LinksAsync());
    }

    [Fact]
    public async Task TwoModeratorsActingAtOnce_AreNotOneDecision()
    {
        await WriteAsync(
            Fact(FactType.MemberBanned, Start.AddDays(-1), subjectId: "usr_p", actorId: "alice"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-1).AddSeconds(1), subjectId: "usr_p", actorId: "bob"));

        await RunAsync();

        Assert.Empty(await LinksAsync());
        Assert.Equal(2, (await OffenderAsync("usr_p"))!.Actions);
    }

    [Fact]
    public async Task ARebuild_FindsTheLinksInHistoryItHasNeverRead()
    {
        // What every deployment upgrading to this looks like: the facts are already in the log and
        // the link table is empty, and the first run reads the whole log rather than only what
        // arrives next.
        await WriteAsync(
            Fact(FactType.MemberBanned, Start.AddDays(-30), subjectId: "usr_p", actorId: "alice"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-30).AddSeconds(2), subjectId: "usr_p", actorId: "alice"));

        await using (var context = Database.NewContext())
        {
            await context.LinkedFacts.ExecuteDeleteAsync(Ct);
            var state = await context.ReviewRunState.FirstOrDefaultAsync(s => s.Id == 1, Ct);
            if (state is not null)
            {
                state.LinkVersion = 0;
                await context.SaveChangesAsync(Ct);
            }
        }

        await RunAsync();

        Assert.Single(await LinksAsync());
        Assert.Equal(1, (await OffenderAsync("usr_p"))!.Actions);

        await using var after = Database.NewContext();
        var run = await after.ReviewRunState.AsNoTracking().FirstAsync(s => s.Id == 1, Ct);
        Assert.Equal(FactLinker.Version, run.LinkVersion);
    }

    [Fact]
    public async Task ExcludingAKindOfAction_ChangesTheStandings()
    {
        await WriteAsync(
            Fact(FactType.GroupInstanceKick, Start.AddDays(-1), subjectId: "usr_p", actorId: "alice"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-2), subjectId: "usr_p", actorId: "bob"),
            Fact(FactType.GroupInstanceKick, Start.AddDays(-3), subjectId: "usr_p", actorId: "carol"),
            Fact(FactType.MemberBanned, Start.AddDays(-4), subjectId: "usr_p", actorId: "carol"));

        await RunAsync();
        Assert.Equal(RepeatOffenderStatus.Repeat, (await OffenderAsync("usr_p"))!.Status);

        // The group does not think clearing somebody out of an instance is a strike.
        await SetThresholdsAsync(ReviewThresholds.Default with
        {
            RepeatOffenderTypes = [FactType.MemberBanned, FactType.MemberKicked],
        });

        await RunAsync(rebuild: true);

        var row = await OffenderAsync("usr_p");
        Assert.NotNull(row);

        Assert.Equal(1, row.Actions);
        Assert.Equal(RepeatOffenderStatus.Once, row.Status);

        // The kicks happened and are still on the row; they simply do not count towards the rule.
        Assert.Equal(3, row.InstanceKicks);
    }

    [Fact]
    public void TheDefaultKindsThatCount_AreTheOnesThatAlwaysCounted()
    {
        Assert.Null(ReviewThresholds.Default.RepeatOffenderTypes);
        Assert.Equal(ActionsOnPeople.Types, ReviewThresholds.Default.CountedTypes);

        // A stored document written before the setting existed reads the same way.
        Assert.Equal(ActionsOnPeople.Types, ReviewThresholds.Read("{\"repeatOffenderActionsIn30Days\":4}").CountedTypes);

        // And a hand-edited row that names nothing, or nothing Modbot knows, falls back rather
        // than freezing every status at "once".
        Assert.Equal(ActionsOnPeople.Types, (ReviewThresholds.Default with { RepeatOffenderTypes = [] }).CountedTypes);
        Assert.Equal(ActionsOnPeople.Types, (ReviewThresholds.Default with { RepeatOffenderTypes = ["nonsense"] }).CountedTypes);
    }

    [Fact]
    public void EveryPair_HasAMainThatIsNeverAFollower()
    {
        // A decision is two facts deep, never a chain. Anything else would let one link drag a
        // third fact in behind it.
        foreach (var pair in LinkedActions.Pairs)
        {
            Assert.False(LinkedActions.IsFollower(pair.Main), $"{pair.Main} is both a main and a follower.");
            Assert.False(LinkedActions.IsMain(pair.Follower), $"{pair.Follower} is both a main and a follower.");
        }
    }

    private async Task<List<LinkedFact>> LinksAsync()
    {
        await using var context = Database.NewContext();
        return await context.LinkedFacts.AsNoTracking().OrderBy(l => l.FactId).ToListAsync(Ct);
    }
}
