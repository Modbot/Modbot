using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Sync;
using Modbot.VRChat.Users;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// The profile producer: discovery from the fact log, one fetch per pass in the queue's order,
/// a fact per change, and a 18+ flag that only a moderator can clear.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class UserProfileSyncTests(PostgresFixture fixture) : SyncTestBase(fixture)
{
    // ── Discovery ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryoneTheFactLogMentionsGetsARow()
    {
        await WriteFactAsync(FactType.MemberBanned, "usr_banned", actor: "usr_mod", at: Now.AddMinutes(-1));

        var run = await RunUserProfileAsync();

        Assert.Equal(2, run.Discovered);

        var banned = await UserRowAsync("usr_banned");
        var moderator = await UserRowAsync("usr_mod");

        Assert.NotNull(banned);
        Assert.NotNull(moderator);
        Assert.Equal(Now.AddMinutes(-1), banned.LastSeenAt);
    }

    /// <summary>The cursor moves, so a pass reads only what was written since the last one.</summary>
    [Fact]
    public async Task DiscoveryIsIncremental()
    {
        await WriteFactAsync(FactType.MemberJoined, "usr_a", at: Now.AddMinutes(-2));
        await RunUserProfileAsync();

        var cursor = (await SettingsAsync()).UserProfileEventsReadThrough;
        Assert.True(cursor > 0);

        await WriteFactAsync(FactType.MemberJoined, "usr_b", at: Now.AddMinutes(-1));
        var second = await RunUserProfileAsync(housekeeping: false);

        Assert.Equal(1, second.Discovered);
        Assert.True((await SettingsAsync()).UserProfileEventsReadThrough > cursor);

        // And a pass with nothing new reads nothing new.
        Assert.Equal(0, (await RunUserProfileAsync(housekeeping: false)).Discovered);
    }

    [Fact]
    public async Task ASecondSightingMovesLastSeenForwardAndLeavesFirstSeenAlone()
    {
        await WriteFactAsync(FactType.MemberJoined, "usr_a", at: Now.AddHours(-2));
        await RunUserProfileAsync();

        await WriteFactAsync(FactType.RoleGranted, "usr_a", at: Now.AddMinutes(-1));
        await RunUserProfileAsync();

        var row = await UserRowAsync("usr_a");

        Assert.Equal(Now.AddHours(-2), row!.FirstSeenAt);
        Assert.Equal(Now.AddMinutes(-1), row.LastSeenAt);
    }

    // ── Refreshing ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheFirstRefreshRecordsTheProfileAndWritesAFirstSeenFact()
    {
        VRChat.Users.Has("usr_a", displayName: "Trinity", pronouns: "she/her", bio: "hi", tags: ["system_trust_veteran"]);
        await WriteFactAsync(FactType.MemberJoined, "usr_a", at: Now.AddMinutes(-1));

        var run = await RunUserProfileAsync();

        Assert.Equal(SyncOutcome.Produced, run.Outcome);
        Assert.True(run.FirstSeen);
        Assert.Equal("usr_a", run.UserId);
        Assert.Equal(RefreshReason.SeenInFactLog, run.Reason);

        var row = await UserRowAsync("usr_a");
        Assert.Equal("Trinity", row!.DisplayName);
        Assert.Equal("she/her", row.Pronouns);
        Assert.Equal(Now, row.LastRefreshedAt);
        Assert.Contains("system_trust_veteran", row.Tags);

        // The raw copy is there for later questions, minus the secrets.
        Assert.Contains("\"displayName\"", row.RawProfile);
        Assert.DoesNotContain("nonce", row.RawProfile);
        Assert.DoesNotContain("private note", row.RawProfile);

        var fact = Assert.Single(await FactsAsync(), f => f.Type == FactType.UserProfileFirstSeen);
        Assert.Equal("usr_a", fact.SubjectId);
        Assert.Null(fact.ActorId);
        Assert.Equal("Trinity", Payload(fact)["baseline"]!["displayName"]!.GetValue<string>());
    }

    [Fact]
    public async Task ARefreshThatFindsNothingChangedWritesNothing()
    {
        VRChat.Users.Has("usr_a");
        await WriteFactAsync(FactType.MemberJoined, "usr_a", at: Now.AddMinutes(-1));
        await RunUserProfileAsync();

        // Old enough to be refreshed again for age.
        Clock.Advance(TimeSpan.FromHours(7));
        var second = await RunUserProfileAsync();

        Assert.Equal(SyncOutcome.Quiet, second.Outcome);
        Assert.True(second.Refreshed);
        Assert.Equal(RefreshReason.ProfileIsOld, second.Reason);
        Assert.Single(await FactsAsync(), f => f.Type.StartsWith("vrchat.user.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AChangedProfileWritesOneFactCarryingOnlyTheDiff()
    {
        VRChat.Users.Has("usr_a", displayName: "Before", bio: "same");
        await WriteFactAsync(FactType.MemberJoined, "usr_a", at: Now.AddMinutes(-1));
        await RunUserProfileAsync();

        var firstRefresh = Clock.UtcNow;
        Clock.Advance(TimeSpan.FromHours(7));
        VRChat.Users.Has("usr_a", displayName: "After", bio: "same");

        var run = await RunUserProfileAsync();

        Assert.Equal(["displayName"], run.Changed);

        var fact = Assert.Single(await FactsAsync(), f => f.Type == FactType.UserProfileChanged);
        var changed = Payload(fact)["changed"]!.AsObject();

        Assert.Equal(["displayName"], changed.Select(c => c.Key));
        Assert.Equal("Before", changed["displayName"]!["old"]!.GetValue<string>());
        Assert.Equal("After", changed["displayName"]!["new"]!.GetValue<string>());

        // A refresh knows only that the change happened since the last one (spec 5.3).
        Assert.Equal(firstRefresh, fact.OccurredAt);
        Assert.Equal(Clock.UtcNow, fact.OccurredBefore);
        Assert.Equal(FactSource.SyncDiff, fact.Source);
    }

    [Fact]
    public async Task APassWithNobodyWaitingSpendsNoRequest()
    {
        var run = await RunUserProfileAsync();

        Assert.Equal(SyncOutcome.Quiet, run.Outcome);
        Assert.False(run.Refreshed);
        Assert.Equal(0, VRChat.Users.RequestCount);
        Assert.Equal(Clock.UtcNow, (await SettingsAsync()).UserProfilePolledAt);
    }

    // ── The sticky 18+ flag ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SeeingEighteenPlusSetsTheFlagAndWritesAFact()
    {
        VRChat.Users.Has("usr_a", ageVerificationStatus: "18+", ageVerified: true);
        await WriteFactAsync(FactType.MemberJoined, "usr_a", at: Now.AddMinutes(-1));

        var run = await RunUserProfileAsync();

        Assert.True(run.AgeVerifiedObserved);

        var row = await UserRowAsync("usr_a");
        Assert.True(row!.Is18PlusVerified);
        Assert.Equal(Now, row.Is18PlusVerifiedAt);
        Assert.Equal(AgeVerificationSource.VRChat, row.Is18PlusVerifiedSource);
        Assert.Equal("18+", row.AgeVerificationStatus);

        var fact = Assert.Single(await FactsAsync(), f => f.Type == FactType.UserAgeVerified);
        Assert.Equal("usr_a", fact.SubjectId);
    }

    /// <summary>
    /// The maintainer's rule. A user who showed 18+ yesterday and hides it today is still 18+;
    /// the profile's status column follows VRChat, the flag does not.
    /// </summary>
    [Fact]
    public async Task HidingTheVerificationLaterDoesNotClearTheFlag()
    {
        VRChat.Users.Has("usr_a", ageVerificationStatus: "18+");
        await WriteFactAsync(FactType.MemberJoined, "usr_a", at: Now.AddMinutes(-1));
        await RunUserProfileAsync();

        var setAt = Clock.UtcNow;
        Clock.Advance(TimeSpan.FromHours(7));
        VRChat.Users.Has("usr_a", ageVerificationStatus: "hidden");

        var run = await RunUserProfileAsync();

        Assert.Equal(["ageVerificationStatus"], run.Changed);
        Assert.False(run.AgeVerifiedObserved);

        var row = await UserRowAsync("usr_a");
        Assert.Equal("hidden", row!.AgeVerificationStatus);
        Assert.True(row.Is18PlusVerified);
        Assert.Equal(setAt, row.Is18PlusVerifiedAt);

        Assert.Single(await FactsAsync(), f => f.Type == FactType.UserAgeVerified);
    }

    [Fact]
    public async Task OnlyAModeratorCanClearTheFlag_AndThatIsRecordedWithWhoDidIt()
    {
        VRChat.Users.Has("usr_a", ageVerificationStatus: "18+");
        await WriteFactAsync(FactType.MemberJoined, "usr_a", at: Now.AddMinutes(-1));
        await RunUserProfileAsync();

        var moderator = Guid.NewGuid();
        Clock.Advance(TimeSpan.FromMinutes(5));

        AgeFlagChange change;
        await using (var context = Database.NewContext())
            change = await ProfilesFor(context).SetAgeFlagAsync("usr_a", verified: false, moderator, "Wrong person", Ct);

        Assert.Equal(AgeFlagChange.Cleared, change);

        var row = await UserRowAsync("usr_a");
        Assert.False(row!.Is18PlusVerified);
        Assert.Equal(AgeVerificationSource.Manual, row.Is18PlusVerifiedSource);
        Assert.Equal(moderator, row.Is18PlusVerifiedByUserId);

        var fact = Assert.Single(await FactsAsync(), f => f.Type == FactType.UserAgeFlagCleared);
        Assert.Equal(FactPlatform.Modbot, fact.ActorPlatform);
        Assert.Equal(moderator.ToString(), fact.ActorId);
        Assert.Equal(FactSource.Manual, fact.Source);
        Assert.Equal("Wrong person", Payload(fact)["reason"]!.GetValue<string>());

        // A sync that still sees "hidden" leaves the moderator's decision alone...
        Clock.Advance(TimeSpan.FromHours(7));
        VRChat.Users.Has("usr_a", ageVerificationStatus: "hidden");
        await RunUserProfileAsync();

        Assert.False((await UserRowAsync("usr_a"))!.Is18PlusVerified);

        // ...and a sync that sees 18+ again is a new observation, and sets it again.
        Clock.Advance(TimeSpan.FromHours(7));
        VRChat.Users.Has("usr_a", ageVerificationStatus: "18+");
        var reobserved = await RunUserProfileAsync();

        Assert.True(reobserved.AgeVerifiedObserved);
        Assert.True((await UserRowAsync("usr_a"))!.Is18PlusVerified);
        Assert.Equal(2, (await FactsAsync()).Count(f => f.Type == FactType.UserAgeVerified));
    }

    [Fact]
    public async Task AModeratorCanSetTheFlagByHand_OnSomebodyModbotHasNeverFetched()
    {
        var moderator = Guid.NewGuid();

        AgeFlagChange change;
        await using (var context = Database.NewContext())
            change = await ProfilesFor(context).SetAgeFlagAsync("usr_new", verified: true, moderator, null, Ct);

        Assert.Equal(AgeFlagChange.Set, change);

        var row = await UserRowAsync("usr_new");
        Assert.True(row!.Is18PlusVerified);
        Assert.Equal(AgeVerificationSource.Manual, row.Is18PlusVerifiedSource);
        Assert.Null(row.LastRefreshedAt);

        Assert.Single(await FactsAsync(), f => f.Type == FactType.UserAgeFlagSet);

        // Setting it again is not a second decision.
        await using (var context = Database.NewContext())
            Assert.Equal(AgeFlagChange.Unchanged, await ProfilesFor(context).SetAgeFlagAsync("usr_new", true, moderator, null, Ct));
    }

    // ── Order ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The tiers, end to end: presence first, then a moderator's request, then the log, then age, then the backlog.</summary>
    [Fact]
    public async Task TheQueueIsSpentInTheDesignsOrder()
    {
        foreach (var id in new[] { "usr_here", "usr_opened", "usr_seen", "usr_old", "usr_never" })
            VRChat.Users.Has(id);

        // Two people already known and refreshed long ago; one never refreshed and not recent.
        await SeedRowAsync("usr_old", lastSeen: Now.AddDays(-3), lastRefreshed: Now.AddDays(-1));
        await SeedRowAsync("usr_never", lastSeen: Now.AddDays(-3), lastRefreshed: null);
        await SeedRowAsync("usr_opened", lastSeen: Now.AddDays(-3), lastRefreshed: Now.AddDays(-1));

        // A presence report and an audit-log entry, both fresh.
        await WriteFactAsync(FactType.InstancePresenceObserved, "usr_here", at: Now.AddMinutes(-2), source: FactSource.Client);
        await WriteFactAsync(FactType.MemberJoined, "usr_seen", at: Now.AddMinutes(-1));

        // A moderator opens one of the old ones.
        await using (var context = Database.NewContext())
        {
            var asked = await ProfilesFor(context).RequestRefreshAsync("usr_opened", RefreshReason.OpenedInModbot, Ct);
            Assert.Equal(RefreshRequestOutcome.Queued, asked.Outcome);
        }

        var order = new List<string?>();
        for (var i = 0; i < 5; i++)
            order.Add((await RunUserProfileAsync()).UserId);

        Assert.Equal(["usr_here", "usr_opened", "usr_seen", "usr_old", "usr_never"], order);
        Assert.False((await RunUserProfileAsync()).Refreshed);
    }

    [Fact]
    public async Task AFreshProfileAnswersAModeratorWithoutARequest()
    {
        VRChat.Users.Has("usr_a");
        await WriteFactAsync(FactType.MemberJoined, "usr_a", at: Now.AddMinutes(-1));
        await RunUserProfileAsync();

        Clock.Advance(TimeSpan.FromSeconds(5));

        RefreshRequested asked;
        await using (var context = Database.NewContext())
            asked = await ProfilesFor(context).RequestRefreshAsync("usr_a", RefreshReason.OpenedInModbot, Ct);

        Assert.Equal(RefreshRequestOutcome.FreshEnough, asked.Outcome);
        Assert.Equal(Now, asked.LastRefreshedAt);
        Assert.Equal(0, Queue.Count);
    }

    /// <summary>A sighting from a month of catch-up history is a row, not a rush.</summary>
    [Fact]
    public async Task AnOldSightingIsRecordedButNotQueuedAsRecent()
    {
        VRChat.Users.Has("usr_a");
        await WriteFactAsync(FactType.MemberBanned, "usr_a", at: Now.AddDays(-20));

        var run = await RunUserProfileAsync(housekeeping: false);

        Assert.Equal(1, run.Discovered);
        Assert.False(run.Refreshed);
        Assert.NotNull(await UserRowAsync("usr_a"));

        // The top-up finds them in the backlog.
        var later = await RunUserProfileAsync(housekeeping: true);

        Assert.Equal("usr_a", later.UserId);
        Assert.Equal(RefreshReason.NeverRefreshed, later.Reason);
    }

    /// <summary>Somebody refreshed by one sighting is not refreshed again by an entry queued for an earlier one.</summary>
    [Fact]
    public async Task AnEntryAnsweredByALaterRefreshIsDroppedUnspent()
    {
        VRChat.Users.Has("usr_a");
        await SeedRowAsync("usr_a", lastSeen: Now.AddMinutes(-1), lastRefreshed: null);

        await RunUserProfileAsync();
        Assert.Equal(1, VRChat.Users.RequestCount);

        // A stale entry for the same sighting, as an overlap re-read would produce.
        Queue.Offer(
            new RefreshRequest("usr_a", RefreshReason.SeenInFactLog, Now.AddMinutes(-1), Clock.UtcNow),
            null, Clock.UtcNow, ProfileOptions);

        var run = await RunUserProfileAsync(housekeeping: false);

        Assert.Equal(1, run.Dropped);
        Assert.False(run.Refreshed);
        Assert.Equal(1, VRChat.Users.RequestCount);
    }

    // ── Failures ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADeletedAccountIsMarkedNotDeleted_AndNotAskedAboutAgainSoon()
    {
        await WriteFactAsync(FactType.MemberBanned, "usr_gone", at: Now.AddMinutes(-1));

        var run = await RunUserProfileAsync();

        Assert.True(run.NotFound);
        Assert.True(run.Refreshed);

        var row = await UserRowAsync("usr_gone");
        Assert.NotNull(row);
        Assert.Equal(Now, row.NotFoundAt);
        Assert.Null(row.LastRefreshedAt);
        Assert.Contains("no account with this id", row.RefreshError);

        Assert.Single(await FactsAsync(), f => f.Type == FactType.UserProfileNotFound);

        // Tomorrow: still gone, still not asked.
        Clock.Advance(TimeSpan.FromDays(1));
        var next = await RunUserProfileAsync();

        Assert.False(next.Refreshed);
        Assert.Equal(1, VRChat.Users.RequestCount);

        // A week on, one more question -- and one fact, not two.
        Clock.Advance(TimeSpan.FromDays(7));
        await RunUserProfileAsync();

        Assert.Equal(2, VRChat.Users.RequestCount);
        Assert.Single(await FactsAsync(), f => f.Type == FactType.UserProfileNotFound);
    }

    /// <summary>
    /// Spec 4.2.5: a 429 on the users lane cold-stops that lane, is never retried, and halves the
    /// global bucket too -- while the group endpoints keep running.
    /// </summary>
    [Fact]
    public async Task ALimitOnTheUsersLaneStopsProfilesHalvesTheGlobalBudgetAndLeavesTheAuditLogRunning()
    {
        VRChat.Users.Has("usr_a");
        await WriteFactAsync(FactType.MemberJoined, "usr_a", at: Now.AddMinutes(-1));
        VRChat.Groups.Add(Entry("gaud_1", Now.AddMinutes(-1)));

        VRChat.Users.Status = HttpStatusCode.TooManyRequests;
        var run = await RunUserProfileAsync();

        Assert.Equal(SyncOutcome.RateLimited, run.Outcome);
        Assert.True((await Health(VRChatEndpointClass.UsersRead)).IsColdStopped);
        Assert.True((await Health(VRChatEndpointClass.Global)).BudgetMultiplier < 1.0);
        Assert.False((await Health(VRChatEndpointClass.Global)).IsColdStopped);

        // The request went back on the queue rather than being lost.
        Assert.Equal(1, Queue.Count);

        // Nothing is retried: the gate refuses before the wire.
        VRChat.Users.Status = HttpStatusCode.OK;
        var again = await RunUserProfileAsync();

        Assert.Equal(SyncOutcome.RateLimited, again.Outcome);
        Assert.Equal(1, VRChat.Users.RequestCount);

        // The audit log is untouched.
        var audit = await RunAuditLogAsync();
        Assert.Equal(SyncOutcome.Produced, audit.Outcome);
    }

    [Fact]
    public async Task APerUserErrorIsWrittenOnTheRowAndTheLaneMovesOn()
    {
        VRChat.Users.Has("usr_a");
        await WriteFactAsync(FactType.MemberJoined, "usr_a", at: Now.AddMinutes(-1));

        VRChat.Users.Status = HttpStatusCode.InternalServerError;
        var run = await RunUserProfileAsync();

        Assert.Equal(SyncOutcome.Quiet, run.Outcome);
        Assert.True(run.Refreshed);

        var row = await UserRowAsync("usr_a");
        Assert.NotNull(row!.RefreshError);
        Assert.Equal(Now, row.RefreshErrorAt);
        Assert.Null(row.LastRefreshedAt);
    }

    [Fact]
    public async Task AnUnconfiguredDeploymentIssuesNothingAtAll()
    {
        await using (var context = Database.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.ManagedGroupId = null;
            await context.SaveChangesAsync(Ct);
        }

        var run = await RunUserProfileAsync();

        Assert.Equal(SyncOutcome.NotConfigured, run.Outcome);
        Assert.Equal(0, VRChat.Users.RequestCount);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private async Task WriteFactAsync(
        string type,
        string subject,
        DateTimeOffset at,
        string? actor = null,
        FactSource source = FactSource.AuditLog)
    {
        await using var context = Database.NewContext();

        var fact = new FactRecord
        {
            Type = type,
            OccurredAt = at,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = subject,
            ActorPlatform = actor is null ? null : FactPlatform.VRChat,
            ActorId = actor,
            Source = source,
            Data = new JsonObject(),
        };

        await new EventPartitionMaintainer(context, Clock).EnsureForAsync(at, Ct);
        await new FactWriter(context, Clock).WriteAsync(fact, Ct);
    }

    private async Task SeedRowAsync(string userId, DateTimeOffset lastSeen, DateTimeOffset? lastRefreshed)
    {
        await using var context = Database.NewContext();

        context.VRChatUsers.Add(new VRChatUser
        {
            UserId = userId,
            FirstSeenAt = lastSeen,
            LastSeenAt = lastSeen,
            LastRefreshedAt = lastRefreshed,
            DisplayName = lastRefreshed is null ? null : "Someone",
        });

        await context.SaveChangesAsync(Ct);
    }

    private async Task<RateLimitBucketHealth> Health(string endpointClass)
    {
        var buckets = await Limiter.Limiter.DescribeAsync(Ct);
        return buckets.Single(b => b.Name == endpointClass);
    }

    private static JsonObject Payload(ModbotEvent fact) => JsonNode.Parse(fact.Data)!.AsObject();
}
