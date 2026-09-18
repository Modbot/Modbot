using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;
using Modbot.VRChat.Users;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// Who the periodic refresh is for (user profile sync design §3.1, narrowed 2026-09-18).
/// </summary>
/// <remarks>
/// <c>vrchat_user</c> holds a row for everyone Modbot has ever seen anywhere, so the two periodic
/// tiers -- "profile is old" and "never refreshed" -- used to queue a stranger who walked through
/// one instance a year ago forever. They now cover current group members and anyone seen inside
/// <see cref="UserProfileSyncOptions.RefreshNonMembersFor"/>. Everybody else is still reachable
/// through the two on-demand tiers, which these tests also pin.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class UserRefreshScopeTests(PostgresFixture fixture) : SyncTestBase(fixture)
{
    /// <summary>Longer ago than the thirty-day window, so a non-member is past it.</summary>
    private static readonly TimeSpan LongGone = TimeSpan.FromDays(200);

    [Fact]
    public async Task AMemberWithAnOldProfileIsStillRefreshed()
    {
        VRChat.Users.Has("usr_member", displayName: "Still here");

        await SeenAsync("usr_member", seen: Now - LongGone, refreshed: Now.AddDays(-10));
        await JoinedTheGroupAsync("usr_member");

        var run = await RunUserProfileAsync();

        Assert.Equal("usr_member", run.UserId);
        Assert.Equal(RefreshReason.ProfileIsOld, run.Reason);
        Assert.True(run.Refreshed);
    }

    [Fact]
    public async Task AMemberWhoseProfileWasNeverFetchedIsStillRefreshed()
    {
        VRChat.Users.Has("usr_member");

        await SeenAsync("usr_member", seen: Now - LongGone, refreshed: null);
        await JoinedTheGroupAsync("usr_member");

        var run = await RunUserProfileAsync();

        Assert.Equal("usr_member", run.UserId);
        Assert.Equal(RefreshReason.NeverRefreshed, run.Reason);
    }

    /// <summary>Somebody outside the group who was here last week is still worth keeping current.</summary>
    [Fact]
    public async Task ANonMemberSeenInsideTheWindowIsStillRefreshed()
    {
        VRChat.Users.Has("usr_visitor");

        await SeenAsync("usr_visitor", seen: Now.AddDays(-7), refreshed: Now.AddDays(-2));

        var run = await RunUserProfileAsync();

        Assert.Equal("usr_visitor", run.UserId);
        Assert.Equal(RefreshReason.ProfileIsOld, run.Reason);
    }

    [Fact]
    public async Task ANonMemberNobodyHasSeenForMonthsIsNotRefreshedForBeingOld()
    {
        VRChat.Users.Has("usr_stranger");

        await SeenAsync("usr_stranger", seen: Now - LongGone, refreshed: Now - LongGone);

        var run = await RunUserProfileAsync();

        Assert.Equal(SyncOutcome.Quiet, run.Outcome);
        Assert.Null(run.UserId);
        Assert.Empty(VRChat.Users.ProfileRequests);
    }

    [Fact]
    public async Task ANonMemberNobodyHasSeenForMonthsIsNotRefreshedForNeverHavingBeenFetched()
    {
        VRChat.Users.Has("usr_stranger");

        await SeenAsync("usr_stranger", seen: Now - LongGone, refreshed: null);

        var run = await RunUserProfileAsync();

        Assert.Equal(SyncOutcome.Quiet, run.Outcome);
        Assert.Null(run.UserId);
        Assert.Empty(VRChat.Users.ProfileRequests);
    }

    /// <summary>A member who left is outside the periodic refresh, like anybody else outside it.</summary>
    [Fact]
    public async Task SomebodyWhoLeftTheGroupLongAgoIsNotRefreshedForBeingOld()
    {
        VRChat.Users.Has("usr_left");

        await SeenAsync("usr_left", seen: Now - LongGone, refreshed: Now - LongGone);
        await JoinedTheGroupAsync("usr_left", leftAt: Now - LongGone);

        var run = await RunUserProfileAsync();

        Assert.Equal(SyncOutcome.Quiet, run.Outcome);
        Assert.Empty(VRChat.Users.ProfileRequests);
    }

    /// <summary>
    /// The on-demand tiers are untouched: a client reporting somebody in an instance fetches them
    /// however long ago Modbot last saw them.
    /// </summary>
    [Fact]
    public async Task ALongGoneNonMemberSeenInAnInstanceIsFetchedAtOnce()
    {
        VRChat.Users.Has("usr_stranger", displayName: "Back again");

        await SeenAsync("usr_stranger", seen: Now - LongGone, refreshed: Now - LongGone);
        await AskedForAsync("usr_stranger", RefreshReason.SeenInInstance);

        var run = await RunUserProfileAsync(housekeeping: false);

        Assert.Equal("usr_stranger", run.UserId);
        Assert.Equal(RefreshReason.SeenInInstance, run.Reason);
        Assert.True(run.Refreshed);
        Assert.Equal("Back again", (await UserRowAsync("usr_stranger"))!.DisplayName);
    }

    /// <summary>And so is a moderator opening them in Modbot.</summary>
    [Fact]
    public async Task ALongGoneNonMemberOpenedInModbotIsFetchedAtOnce()
    {
        VRChat.Users.Has("usr_stranger");

        await SeenAsync("usr_stranger", seen: Now - LongGone, refreshed: Now - LongGone);

        var asked = await AskedForAsync("usr_stranger", RefreshReason.OpenedInModbot);
        Assert.Equal(RefreshRequestOutcome.Queued, asked.Outcome);

        var run = await RunUserProfileAsync(housekeeping: false);

        Assert.Equal("usr_stranger", run.UserId);
        Assert.Equal(RefreshReason.OpenedInModbot, run.Reason);
        Assert.True(run.Refreshed);
    }

    /// <summary>The window is a setting: widen it and the same stranger is back in the periodic tiers.</summary>
    [Fact]
    public async Task TheWindowIsASetting()
    {
        VRChat.Users.Has("usr_stranger");
        ProfileOptions = ProfileOptions with { RefreshNonMembersFor = TimeSpan.FromDays(365) };

        await SeenAsync("usr_stranger", seen: Now - LongGone, refreshed: Now - LongGone);

        var run = await RunUserProfileAsync();

        Assert.Equal("usr_stranger", run.UserId);
        Assert.Equal(RefreshReason.ProfileIsOld, run.Reason);
    }

    private async Task SeenAsync(string userId, DateTimeOffset seen, DateTimeOffset? refreshed)
    {
        await using var context = Database.NewContext();

        context.VRChatUsers.Add(new VRChatUser
        {
            UserId = userId,
            FirstSeenAt = seen,
            LastSeenAt = seen,
            LastRefreshedAt = refreshed,
            DisplayName = refreshed is null ? null : "Someone",
        });

        await context.SaveChangesAsync(Ct);
    }

    private async Task JoinedTheGroupAsync(string userId, DateTimeOffset? leftAt = null)
    {
        await using var context = Database.NewContext();

        context.GroupMembers.Add(new GroupMember
        {
            GroupId = GroupId,
            UserId = userId,
            Roles = "[]",
            JoinedAt = Now.AddDays(-400),
            FirstSeenAt = Now.AddDays(-400),
            LastSeenAt = Now,
            LeftAt = leftAt,
        });

        await context.SaveChangesAsync(Ct);
    }

    private async Task<RefreshRequested> AskedForAsync(string userId, RefreshReason reason)
    {
        await using var context = Database.NewContext();
        return await ProfilesFor(context).RequestRefreshAsync(userId, reason, Ct);
    }
}
