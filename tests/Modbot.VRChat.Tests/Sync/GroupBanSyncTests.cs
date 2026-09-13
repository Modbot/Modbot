using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// The ban sweep: the same shape as the member sweep, and the one interaction between the two
/// that matters -- a member who vanished because they were banned did not leave.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class GroupBanSyncTests(PostgresFixture fixture) : SyncTestBase(fixture)
{
    [Fact]
    public async Task TheFirstSweepRecordsEveryBanAndOneSnapshot()
    {
        for (var i = 0; i < 120; i++)
            VRChat.Groups.Bans.Add(Ban($"usr_{i:000}", Now.AddDays(-i - 1)));

        var finished = await SweepBansAsync();

        Assert.True(finished.SweepComplete);
        Assert.True(finished.FirstSweep);
        Assert.Equal([0, 95, 120], VRChat.Groups.BanQueries.Select(q => q.Offset));

        await using var context = Database.NewContext();
        Assert.Equal(120, await context.GroupBans.CountAsync(b => b.GroupId == GroupId && b.LiftedAt == null, Ct));

        var fact = Assert.Single(await FactsAsync());
        Assert.Equal(FactType.BansSnapshot, fact.Type);
        Assert.Equal(120, Payload(fact)["banCount"]!.GetValue<int>());
        Assert.Empty(await FactsOfTypeAsync(FactType.MemberBanned));

        var row = await BanRowAsync("usr_005");
        Assert.Equal(Now.AddDays(-6), row!.BannedAt);
        Assert.Contains("bannedAt", row.Raw);
    }

    [Fact]
    public async Task EveryBannedPersonIsRecordedAsASightingDatedAtTheBan()
    {
        VRChat.Groups.Bans.Add(Ban("usr_a", Now.AddDays(-9)));

        await SweepBansAsync();

        var user = await UserRowAsync("usr_a");
        Assert.NotNull(user);
        Assert.Equal(Now.AddDays(-9), user.LastSeenAt);
    }

    [Fact]
    public async Task ANewBanOnALaterSweepIsRecordedDatedWhenVRChatSaysItWasIssued()
    {
        VRChat.Groups.Bans.Add(Ban("usr_a"));
        await SweepBansAsync();

        RestBans();
        var bannedAt = Clock.UtcNow.AddMinutes(-3);
        VRChat.Groups.Bans.Add(Ban("usr_b", bannedAt));

        var second = await SweepBansAsync();

        Assert.Equal(1, second.FactsWritten);

        var ban = Assert.Single(await FactsOfTypeAsync(FactType.MemberBanned));
        Assert.Equal("usr_b", ban.SubjectId);
        Assert.Equal(bannedAt, ban.OccurredAt);
        Assert.Null(ban.OccurredBefore);
        Assert.Equal(FactSource.SyncDiff, ban.Source);
        Assert.Null(ban.ActorId);
    }

    [Fact]
    public async Task ABanTheAuditLogAlreadyRecordedIsNotRecordedAgain()
    {
        VRChat.Groups.Bans.Add(Ban("usr_a"));
        await SweepBansAsync();

        RestBans();
        var bannedAt = Clock.UtcNow.AddMinutes(-3);

        VRChat.Groups.Add(Entry("gaud_ban", bannedAt, GroupAuditLogEvents.UserBan, target: "usr_b"));
        await RunAuditLogAsync();

        VRChat.Groups.Bans.Add(Ban("usr_b", bannedAt));
        var noticed = await SweepBansAsync();

        // Noticed in the same instant the audit log last polled, so it waits for the next poll.
        Assert.Equal(1, noticed.FactsWaiting);

        RestBans();
        await RunAuditLogAsync();
        var second = await SweepBansAsync();

        Assert.Equal(0, second.FactsWritten);
        Assert.Equal(1, second.FactsDeduplicated);

        var ban = Assert.Single(await FactsOfTypeAsync(FactType.MemberBanned));
        Assert.Equal(FactSource.AuditLog, ban.Source);
    }

    [Fact]
    public async Task ABanMissingFromAFullSweepIsMarkedLiftedAndTheUnbanRecordedWithAWindow()
    {
        VRChat.Groups.Bans.Add(Ban("usr_a"));
        VRChat.Groups.Bans.Add(Ban("usr_b"));
        await SweepBansAsync();
        var lastSeen = (await BanRowAsync("usr_b"))!.LastSeenAt;

        RestBans();
        VRChat.Groups.Bans.RemoveAll(b => b.UserId == "usr_b");
        var second = await SweepBansAsync();

        Assert.Equal(1, second.MarkedGone);
        Assert.Equal(Clock.UtcNow, (await BanRowAsync("usr_b"))!.LiftedAt);
        Assert.Empty(await FactsOfTypeAsync(FactType.MemberUnbanned));
        var noticedAt = Clock.UtcNow;

        RestBans();
        var third = await SweepBansAsync();

        Assert.Equal(1, third.FactsWritten);

        var unban = Assert.Single(await FactsOfTypeAsync(FactType.MemberUnbanned));
        Assert.Equal(lastSeen, unban.OccurredAt);
        Assert.Equal(noticedAt, unban.OccurredBefore);
    }

    [Fact]
    public async Task ABanSkippedForOneSweepIsNotAnUnban()
    {
        VRChat.Groups.Bans.Add(Ban("usr_a"));
        VRChat.Groups.Bans.Add(Ban("usr_b"));
        await SweepBansAsync();

        RestBans();
        var skipped = VRChat.Groups.Bans.Single(b => b.UserId == "usr_b");
        VRChat.Groups.Bans.Remove(skipped);
        await SweepBansAsync();

        RestBans();
        VRChat.Groups.Bans.Add(skipped);
        await SweepBansAsync();

        var row = await BanRowAsync("usr_b");
        Assert.Null(row!.LiftedAt);
        Assert.Null(row.WaitingFacts);
        Assert.Empty(await FactsOfTypeAsync(FactType.MemberUnbanned));
    }

    /// <summary>
    /// The one place the two sweeps meet. Somebody who dropped off the member list because they
    /// were banned did not leave; the ban is the fact that should exist, and it is the ban list
    /// -- not only the audit log -- that says so.
    /// </summary>
    [Fact]
    public async Task AMemberWhoVanishedBecauseTheyWereBannedIsNotRecordedAsLeaving()
    {
        VRChat.Groups.Members.Add(Member("usr_a"));
        VRChat.Groups.Members.Add(Member("usr_b"));
        await SweepMembersAsync();
        await SweepBansAsync();

        RestMembers();
        RestBans();

        var bannedAt = Clock.UtcNow.AddMinutes(-1);
        VRChat.Groups.Members.RemoveAll(m => m.UserId == "usr_b");
        VRChat.Groups.Bans.Add(Ban("usr_b", bannedAt));

        await SweepMembersAsync();
        await SweepBansAsync();

        RestMembers();
        var third = await SweepMembersAsync();

        Assert.Equal(1, third.FactsDeduplicated);
        Assert.Empty(await FactsOfTypeAsync(FactType.MemberLeft));

        var ban = Assert.Single(await FactsOfTypeAsync(FactType.MemberBanned));
        Assert.Equal(bannedAt, ban.OccurredAt);
        Assert.NotNull((await MemberRowAsync("usr_b"))!.LeftAt);
    }

    [Fact]
    public async Task ARateLimitedPageColdStopsTheBanSweepAndNotTheMemberSweep()
    {
        VRChat.Groups.Bans.Add(Ban("usr_a"));
        VRChat.Groups.Members.Add(Member("usr_m"));
        VRChat.Groups.BansStatus = HttpStatusCode.TooManyRequests;

        var limited = await RunBanSweepAsync();

        Assert.Equal(SyncOutcome.RateLimited, limited.Outcome);
        Assert.Equal(0, (await SettingsAsync()).BanSweepOffset);

        VRChat.Groups.BansStatus = HttpStatusCode.OK;
        var requests = VRChat.Groups.BanRequests;

        Assert.Equal(SyncOutcome.RateLimited, (await RunBanSweepAsync()).Outcome);
        Assert.Equal(requests, VRChat.Groups.BanRequests);

        Assert.True((await SweepMembersAsync()).SweepComplete);
    }

    /// <summary>A 403 -- the bot's role lacks the permission -- is a failure with VRChat's reason, not a rate limit.</summary>
    [Fact]
    public async Task AForbiddenPageIsAFailureNotAColdStop()
    {
        VRChat.Groups.BansStatus = HttpStatusCode.Forbidden;

        var pass = await RunBanSweepAsync();

        Assert.Equal(SyncOutcome.Failed, pass.Outcome);

        VRChat.Groups.BansStatus = HttpStatusCode.OK;
        VRChat.Groups.Bans.Add(Ban("usr_a"));

        Assert.True((await SweepBansAsync()).SweepComplete);
    }

    private static JsonObject Payload(ModbotEvent fact) => JsonNode.Parse(fact.Data)!.AsObject();
}
