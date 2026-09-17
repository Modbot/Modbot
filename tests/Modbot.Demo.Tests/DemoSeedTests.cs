using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Live;
using Modbot.TestSupport;

namespace Modbot.Demo.Tests;

/// <summary>
/// The seeded demo, against a real database.
/// </summary>
/// <remarks>
/// <para>
/// The point of these is not that the numbers are large. It is that the demo <em>hangs together</em>
/// (demo mode design §4): every fact points at somebody who exists, every room is in a world that
/// exists, and the analytics pages read back figures that were computed from the facts rather than
/// typed in. A demo whose ban list names people the member list has never heard of is worse than no
/// demo, because a moderator training on it learns something untrue.
/// </para>
/// <para>
/// <strong>The year of history is written once, in one test, and everything about it is asserted
/// there.</strong> It is about eighteen thousand facts through the real fact writer, which is a
/// minute or two; writing it once per assertion would be most of an hour of CI for no more
/// certainty. Everything that only needs the quick half of the seeding gets its own test.
/// </para>
/// <para>
/// One assembly, one container, and this collection runs serially — the demo owns every table, so
/// two of these at once would be two demos in one database.
/// </para>
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class DemoSeedTests
{
    private readonly PostgresFixture _fixture;

    public DemoSeedTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task TheGroupThePeopleAndTheRoomsAreThereBeforeAnyHistoryIs()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await DemoSeedHost.StartAsync(_fixture, ct);

        var plan = await host.Seeder().SeedCoreAsync(ct);

        var settings = await host.Db.GetSettingsAsync(ct);
        Assert.True(settings.DemoData);
        Assert.True(settings.OnboardingComplete);
        Assert.Equal(plan.GroupId, settings.ManagedGroupId);

        Assert.Equal(DemoPlan.PeopleCount, await host.Db.VRChatUsers.CountAsync(ct));
        Assert.Equal(DemoPlan.PeopleCount, await host.Db.GroupMembers.CountAsync(ct));
        Assert.Equal(DemoPlan.StaffCount, await host.Db.Users.CountAsync(ct));
        Assert.Equal(12, await host.Db.VRChatWorlds.CountAsync(ct));
        Assert.True(await host.Db.VRChatInstances.CountAsync(ct) > 100);
        Assert.True(await host.Db.InstanceHeadCounts.AnyAsync(ct));
        Assert.True(await host.Db.CaseFiles.AnyAsync(ct));
        Assert.Equal(6, await host.Db.CalendarEvents.CountAsync(ct));
        Assert.Equal(1, await host.Db.ApiKeys.CountAsync(ct));
        Assert.Equal(1, await host.Db.Webhooks.CountAsync(ct));
        Assert.Equal(14, await host.Db.DiscordChannels.CountAsync(ct));
        Assert.True(await host.Db.DiscordMembers.CountAsync(ct) > 200);

        // The account every visitor is served as has to exist, or /api/auth/me answers 401 and the
        // demo shows a sign-in page it has no way to get past.
        var administrator = await host.Db.Users
            .Include(u => u.Roles)
            .FirstOrDefaultAsync(u => u.Id == DemoMode.AdministratorId, ct);

        Assert.NotNull(administrator);
        Assert.True(administrator.IsVRChatLinked);
        Assert.Contains(administrator.Roles, r => r.RoleId == BuiltInRoles.AdministratorId);

        // Live reads presence reported by a paired client, so the team needs paired clients.
        Assert.Equal(DemoPlan.StaffCount, await host.Db.CompanionDevices.CountAsync(ct));
    }

    [Fact]
    public async Task SeedingIsNotDoneTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await DemoSeedHost.StartAsync(_fixture, ct);

        Assert.False(await host.Seeder().IsSeededAsync(ct));

        await host.Seeder().SeedCoreAsync(ct);

        Assert.True(await host.Seeder().IsSeededAsync(ct));
        Assert.Equal(DemoPlan.PeopleCount, await host.Db.VRChatUsers.CountAsync(ct));
    }

    [Fact]
    public async Task EveryBanHasACaseFileAboutSomebodyWhoExists()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await DemoSeedHost.StartAsync(_fixture, ct);

        await host.Seeder().SeedCoreAsync(ct);

        var known = new HashSet<string>(
            await host.Db.VRChatUsers.Select(u => u.UserId).ToListAsync(ct), StringComparer.Ordinal);

        var staff = await host.Db.Users.Select(u => u.Id).ToListAsync(ct);
        var cases = await host.Db.CaseFiles.AsNoTracking().ToListAsync(ct);

        Assert.NotEmpty(cases);

        foreach (var file in cases)
        {
            Assert.Contains(file.UserId, known);
            Assert.Contains(file.AuthorUserId, staff);
            Assert.NotEqual(string.Empty, file.WrittenReason);

            // The snapshot is stored as written and read straight through by the case file page,
            // so the demo has to write the field names the real capture writes. A snapshot with a
            // shape of its own blanked the whole page: the page read `roleIds` and the demo had
            // written `roles`.
            var profile = JsonNode.Parse(file.ProfileAtBan!)!.AsObject();
            Assert.Equal(file.UserId, profile["userId"]!.GetValue<string>());
            Assert.IsType<JsonArray>(profile["tags"]);
            Assert.NotNull(profile["eighteenPlus"]!["verified"]);
            Assert.NotNull(profile["displayName"]);

            var membership = JsonNode.Parse(file.MembershipAtBan!)!.AsObject();
            Assert.IsType<JsonArray>(membership["roleIds"]);
            Assert.NotNull(membership["joinedAt"]);

            var entry = JsonNode.Parse(file.BanListEntryAtBan!)!.AsObject();
            Assert.NotNull(entry["bannedAt"]);
        }

        var bans = await host.Db.GroupBans.Select(b => b.UserId).ToListAsync(ct);

        Assert.NotEmpty(bans);
        Assert.All(bans, id => Assert.Contains(id, known));
    }

    /// <summary>
    /// The whole demo, written once: is it consistent, do the analytics add up, is the Live page
    /// alive, and does everything end at the present moment.
    /// </summary>
    [Fact]
    public async Task TheWholeDemoHangsTogether()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await DemoSeedHost.StartAsync(_fixture, ct);

        var state = new DemoState();
        Assert.False(state.Busy);

        var plan = await host.Seeder().SeedCoreAsync(ct);
        await host.History().WriteAsync(plan, state, ct);

        // The progress line names the step it is on. The service that drives it clears it at the
        // end; the history writer is not the thing that finishes.
        Assert.NotEqual(string.Empty, state.Step);

        await EveryFactPointsAtSomebodyWhoExistsAsync(host, ct);
        await TheAnalyticsAddUpAsync(host, ct);
        await TheLivePageHasRoomsOpenWithPeopleInThemAsync(host, ct);
        await EverythingTimeBasedEndsNowAsync(host, ct);
    }

    private static async Task EveryFactPointsAtSomebodyWhoExistsAsync(DemoSeedHost host, CancellationToken ct)
    {
        var known = new HashSet<string>(
            await host.Db.VRChatUsers.Select(u => u.UserId).ToListAsync(ct), StringComparer.Ordinal);

        var knownDiscord = new HashSet<string>(
            await host.Db.DiscordMembers.Select(m => m.UserId).ToListAsync(ct), StringComparer.Ordinal);

        var knownWorlds = new HashSet<string>(
            await host.Db.VRChatWorlds.Select(w => w.WorldId).ToListAsync(ct), StringComparer.Ordinal);

        var settings = await host.Db.GetSettingsAsync(ct);

        var facts = await host.Db.Events.AsNoTracking()
            .Select(e => new { e.Type, e.SubjectPlatform, e.SubjectId, e.ActorPlatform, e.ActorId, e.WorldId })
            .ToListAsync(ct);

        Assert.NotEmpty(facts);

        foreach (var fact in facts)
        {
            // The group's own details are the one fact whose subject is the group rather than a
            // person, exactly as the group-info sync writes it.
            if (fact.Type == FactType.GroupInfoChanged)
            {
                Assert.Equal(settings.ManagedGroupId, fact.SubjectId);
                continue;
            }

            if (fact.SubjectPlatform == FactPlatform.VRChat)
                Assert.Contains(fact.SubjectId, known);

            if (fact.SubjectPlatform == FactPlatform.Discord)
                Assert.Contains(fact.SubjectId, knownDiscord);

            if (fact.ActorPlatform == FactPlatform.VRChat && fact.ActorId is { Length: > 0 } actor)
                Assert.Contains(actor, known);

            if (fact.WorldId is { Length: > 0 } world)
                Assert.Contains(world, knownWorlds);
        }
    }

    private static async Task TheAnalyticsAddUpAsync(DemoSeedHost host, CancellationToken ct)
    {
        var joinedTotal = await host.Db.DailyTotals
            .Where(t => t.Metric == DailyTotalMetrics.MembersJoined && t.Dimension == "")
            .SumAsync(t => t.Value, ct);

        var joinFacts = await host.Db.Events.CountAsync(e => e.Type == FactType.MemberJoined, ct);

        // A count metric apportions each fact across the days its window covers, so the sum over
        // the year is the number of facts -- to within rounding on the fractional days.
        Assert.InRange((double)joinedTotal, joinFacts - 1.0, joinFacts + 1.0);

        foreach (var metric in new[]
        {
            DailyTotalMetrics.MembersJoined,
            DailyTotalMetrics.MembersLeft,
            DailyTotalMetrics.BansAdded,
            DailyTotalMetrics.InstancesOpened,
            DailyTotalMetrics.WorldInstances,
            DailyTotalMetrics.WorldVisitors,
            DailyTotalMetrics.ModeratorWarns,
            DailyTotalMetrics.DiscordMessages,
            DailyTotalMetrics.DiscordMembersJoined,
            DailyTotalMetrics.DiscordMembersCount,
        })
        {
            Assert.True(
                await host.Db.DailyTotals.AnyAsync(t => t.Metric == metric && t.Value > 0, ct),
                $"Nothing was computed for {metric}.");
        }

        // Repeat offenders and moderator baselines are the review job's work, from those totals.
        Assert.True(await host.Db.RepeatOffenders.AnyAsync(o => o.Actions > 1, ct));
        Assert.True(await host.Db.ModeratorBaselines.AnyAsync(ct));

        // My Group's member count is read out of the group's own facts and nothing else -- it is a
        // level, and no daily total adds up to it. A demo with none showed an empty chart and a
        // dash where the headcount goes, beside full joins and leaves charts.
        var group = await host.Db.Events.AsNoTracking()
            .Where(e => e.Type == FactType.GroupInfoChanged)
            .OrderBy(e => e.OccurredAt)
            .Select(e => e.Data)
            .ToListAsync(ct);

        // Not every one carries a member count -- a day on which only the online count moved is a
        // real change and a real fact, and the chart simply has no point for it.
        var counts = group
            .Select(d => JsonNode.Parse(d)!.AsObject())
            .Select(o => o["changed"]?["MemberCount"]?["new"] ?? o["baseline"]?["MemberCount"])
            .Where(n => n is not null)
            .Select(n => n!.GetValue<int>())
            .ToList();

        Assert.True(counts.Count > 30, $"Only {counts.Count} of {group.Count} group facts carried a member count.");
        Assert.True(counts[^1] > 100, $"The group ended the year with {counts[^1]} members.");
        Assert.True(counts[^1] > counts[0], "The group never grew.");
    }

    private static async Task TheLivePageHasRoomsOpenWithPeopleInThemAsync(DemoSeedHost host, CancellationToken ct)
    {
        var settings = await host.Db.GetSettingsAsync(ct);

        var open = await host.Db.VRChatInstances.AsNoTracking()
            .Where(i => i.GroupId == settings.ManagedGroupId && i.SeenInGroupList && i.ClosedAt == null)
            .ToListAsync(ct);

        // A handful, not one: the doc promises "a few open right now", and how many the dice leave
        // open at the moment of seeding is anything from none to four.
        Assert.True(
            open.Count >= DemoPlan.RoomsOpenNow,
            $"Only {open.Count} rooms are open; Live is the page the demo is judged on.");

        var people = await new RoomPeopleReader(host.Db).ForRoomsAsync(open, ct);

        Assert.True(
            open.Count(r => people.TryGetValue(r.Id, out var p) && p.Here.Count > 0) >= DemoPlan.RoomsOpenNow,
            "An open room with nobody in it reads as a dead demo.");
    }

    private static async Task EverythingTimeBasedEndsNowAsync(DemoSeedHost host, CancellationToken ct)
    {
        var now = host.Clock.UtcNow;

        var newest = await host.Db.Events.MaxAsync(e => e.OccurredAt, ct);
        var oldest = await host.Db.Events.MinAsync(e => e.OccurredAt, ct);

        Assert.True(newest <= now, "A fact was dated in the future.");
        Assert.True(now - newest < TimeSpan.FromDays(2), "The newest fact is not recent.");
        Assert.True(now - oldest > TimeSpan.FromDays(300), "There is not a year of history.");

        var newestMessage = await host.Db.DiscordMessages.MaxAsync(m => m.SentAt, ct);
        Assert.True(newestMessage <= now);
        Assert.True(now - newestMessage < TimeSpan.FromDays(3));

        // A calendar with nothing ahead of it looks broken.
        Assert.True(await host.Db.CalendarEvents.AnyAsync(e => e.StartsAt > now, ct));
        Assert.True(await host.Db.CalendarEvents.AnyAsync(e => e.EndsAt < now, ct));
    }

    [Fact]
    public async Task AResetEmptiesEverythingAndFillsItInAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await DemoSeedHost.StartAsync(_fixture, ct);

        await host.Seeder().SeedCoreAsync(ct);

        // A handful of facts rather than the year of them: what is being tested is that the wipe
        // empties the fact log, and eighteen thousand rows prove that no better than five do.
        await host.WriteSomeFactsAsync(5, ct);

        Assert.True(await host.Db.Events.CountAsync(ct) > 0);

        await host.Seeder().WipeAsync(ct);

        Assert.Equal(0, await host.Db.Events.CountAsync(ct));
        Assert.Equal(0, await host.Db.VRChatUsers.CountAsync(ct));
        Assert.Equal(0, await host.Db.Users.CountAsync(ct));
        Assert.Equal(0, await host.Db.DiscordMessages.CountAsync(ct));
        Assert.Equal(0, await host.Db.DailyTotals.CountAsync(ct));
        Assert.Equal(0, await host.Db.CaseFiles.CountAsync(ct));
        Assert.Equal(0, await host.Db.VRChatInstances.CountAsync(ct));

        var settings = await host.Db.GetSettingsAsync(ct);
        Assert.False(settings.DemoData);
        Assert.False(settings.OnboardingComplete);

        // The built-in roles are the migrations' rows, not the demo's, and must survive a wipe.
        Assert.Equal(BuiltInRoles.All.Count, await host.Db.Roles.CountAsync(ct));

        await host.Seeder().SeedCoreAsync(ct);

        Assert.Equal(DemoPlan.PeopleCount, await host.Db.VRChatUsers.CountAsync(ct));
        Assert.True((await host.Db.GetSettingsAsync(ct)).DemoData);
    }

    /// <summary>
    /// An AI provider and key an operator set is not made-up data, and a reset must not take it.
    /// </summary>
    [Fact]
    public async Task AResetKeepsTheAiSettings()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await DemoSeedHost.StartAsync(_fixture, ct);

        await host.Seeder().SeedCoreAsync(ct);

        var settings = await host.Db.GetSettingsAsync(ct);
        settings.AiEnabled = true;
        settings.AiProvider = "openrouter";
        settings.AiModel = "a-model";
        settings.AiApiKeyEncrypted = host.Protector.Protect("a-key");
        await host.Db.SaveChangesAsync(ct);

        await host.Seeder().WipeAsync(ct);

        var after = await host.Db.GetSettingsAsync(ct);

        Assert.True(after.AiEnabled);
        Assert.Equal("openrouter", after.AiProvider);
        Assert.Equal("a-model", after.AiModel);
        Assert.Equal("a-key", host.Protector.Unprotect(after.AiApiKeyEncrypted!));
    }
}
