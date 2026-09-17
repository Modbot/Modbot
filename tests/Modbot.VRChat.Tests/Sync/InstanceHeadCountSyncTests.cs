using System.Net;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// An instance's head count: the instance's own page when it can be read and believed, the group list's
/// number when it cannot, and one change-log row each time the number moves.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class InstanceHeadCountSyncTests(PostgresFixture fixture) : SyncTestBase(fixture)
{
    private const string Instance = $"wrld_4432:68681~group({GroupId})~groupAccessType(plus)~region(us)";

    /// <summary>The group's list carries the instance with this many members, and has been polled.</summary>
    private async Task ListedAsync(int memberCount = 2)
    {
        VRChat.Groups.Instances.Clear();
        VRChat.Groups.Instances.Add(FakeGroups.Listed(Instance, memberCount));
        await RunGroupInstancesAsync();
    }

    private async Task<VRChatInstance> InstanceAsync()
    {
        await using var db = Database.NewContext();
        return await db.VRChatInstances.AsNoTracking().SingleAsync(i => i.Location == Instance, Ct);
    }

    private async Task<List<InstanceHeadCount>> ChangesAsync()
    {
        await using var db = Database.NewContext();
        return await db.InstanceHeadCounts.AsNoTracking().OrderBy(c => c.Id).ToListAsync(Ct);
    }

    [Fact]
    public async Task TheListsCountStandsInUntilTheInstancesPageIsRead()
    {
        await ListedAsync(memberCount: 2);

        var instance = await InstanceAsync();
        Assert.Equal(2, instance.HeadCount);
        Assert.Equal(HeadCounts.FromList, instance.HeadCountSource);

        var change = Assert.Single(await ChangesAsync());
        Assert.Equal(2, change.HeadCount);
        Assert.Equal(HeadCounts.FromList, change.Source);
        Assert.Equal(instance.Id, change.InstanceId);
    }

    [Fact]
    public async Task TheInstancesOwnPage_SetsTheHeadCount_WithUserCountKeptBeside()
    {
        await ListedAsync(memberCount: 2);
        VRChat.Instances.Page(Instance, nUsers: 3, userCount: 2);

        var run = await RunInstanceHeadCountsAsync();

        Assert.Equal(SyncOutcome.Produced, run.Outcome);
        Assert.Equal(Instance, Assert.Single(VRChat.Instances.Requests));

        var instance = await InstanceAsync();
        Assert.Equal(3, instance.HeadCount);
        Assert.Equal(HeadCounts.FromPage, instance.HeadCountSource);
        Assert.Equal(2, instance.PageUserCount);
        Assert.Equal(Now, instance.PageReadAt);

        var changes = await ChangesAsync();
        Assert.Equal(2, changes.Count);
        Assert.Equal((3, 2, 2, HeadCounts.FromPage), (changes[1].HeadCount, changes[1].UserCount, changes[1].MemberCount, changes[1].Source));
    }

    [Fact]
    public async Task AnInstanceThatReadsInactive_KeepsTheListsCount()
    {
        // The endpoint answers 200 for an instance that never existed and says active: false. A zero in
        // that body is not a head count.
        await ListedAsync(memberCount: 2);
        VRChat.Instances.Page(Instance, nUsers: 0, userCount: 0, active: false);

        await RunInstanceHeadCountsAsync();

        var instance = await InstanceAsync();
        Assert.Equal(2, instance.HeadCount);
        Assert.Equal(HeadCounts.FromList, instance.HeadCountSource);
        Assert.Null(instance.PageReadAt);
        Assert.Single(await ChangesAsync());
    }

    [Fact]
    public async Task A429ColdStops_IsNotRetried_AndTheListsCountStands()
    {
        await ListedAsync(memberCount: 2);
        VRChat.Instances.Status(Instance, HttpStatusCode.TooManyRequests);

        var limited = await RunInstanceHeadCountsAsync();

        Assert.Equal(SyncOutcome.RateLimited, limited.Outcome);
        Assert.Single(VRChat.Instances.Requests);

        // Whatever the page would say now is irrelevant: nothing should reach it while the bucket
        // is cold, however often the loop comes round.
        VRChat.Instances.Page(Instance, nUsers: 9, userCount: 9);
        Clock.Advance(InstanceHeadCountSync.ReadEvery);

        Assert.Equal(SyncOutcome.RateLimited, (await RunInstanceHeadCountsAsync()).Outcome);
        Assert.Single(VRChat.Instances.Requests);

        Assert.Equal(2, (await InstanceAsync()).HeadCount);
    }

    [Fact]
    public async Task AFailedRead_FallsBackToTheListsCount()
    {
        await ListedAsync(memberCount: 2);
        VRChat.Instances.Page(Instance, nUsers: 3, userCount: 2);
        await RunInstanceHeadCountsAsync();

        VRChat.Instances.Status(Instance, HttpStatusCode.InternalServerError);
        Clock.Advance(InstanceHeadCountSync.ReadEvery);
        await RunInstanceHeadCountsAsync();

        var instance = await InstanceAsync();
        Assert.Equal(2, instance.HeadCount);
        Assert.Equal(HeadCounts.FromList, instance.HeadCountSource);
    }

    [Fact]
    public async Task AFreshPageRead_WinsOverTheList_UntilItGoesStale()
    {
        await ListedAsync(memberCount: 2);
        VRChat.Instances.Page(Instance, nUsers: 3, userCount: 2);
        await RunInstanceHeadCountsAsync();

        Clock.Advance(TimeSpan.FromSeconds(10));
        await ListedAsync(memberCount: 2);
        Assert.Equal(3, (await InstanceAsync()).HeadCount);

        Clock.Advance(HeadCounts.PageReadGoesStaleAfter);
        await ListedAsync(memberCount: 2);

        var instance = await InstanceAsync();
        Assert.Equal(2, instance.HeadCount);
        Assert.Equal(HeadCounts.FromList, instance.HeadCountSource);
    }

    [Fact]
    public async Task EachInstanceIsReadAboutEveryThirtySeconds()
    {
        await ListedAsync();
        VRChat.Instances.Page(Instance, nUsers: 3, userCount: 2);

        await RunInstanceHeadCountsAsync();
        Assert.Equal(SyncOutcome.Quiet, (await RunInstanceHeadCountsAsync()).Outcome);
        Assert.Single(VRChat.Instances.Requests);

        Clock.Advance(InstanceHeadCountSync.ReadEvery);
        await RunInstanceHeadCountsAsync();
        Assert.Equal(2, VRChat.Instances.Requests.Count);
    }

    [Fact]
    public async Task OneRowPerChange_NotPerRead()
    {
        await ListedAsync(memberCount: 2);

        VRChat.Instances.Page(Instance, nUsers: 3, userCount: 2);
        await RunInstanceHeadCountsAsync();

        Clock.Advance(InstanceHeadCountSync.ReadEvery);
        await RunInstanceHeadCountsAsync();

        Clock.Advance(InstanceHeadCountSync.ReadEvery);
        VRChat.Instances.Page(Instance, nUsers: 5, userCount: 3);
        await RunInstanceHeadCountsAsync();

        Assert.Equal([2, 3, 5], (await ChangesAsync()).Select(c => c.HeadCount));
        Assert.Equal(5, (await InstanceAsync()).PeakUserCount);
    }

    [Fact]
    public async Task OnlyInstancesTheGroupsListCarriesNowAreRead()
    {
        await using (var db = Database.NewContext())
        {
            // Seen by a moderator's client, never by the group's list.
            db.VRChatInstances.Add(new VRChatInstance
            {
                Id = Guid.NewGuid(),
                Location = $"wrld_4432:11111~group({GroupId})",
                WorldId = "wrld_4432",
                VRChatInstanceId = "11111",
                GroupId = GroupId,
                OpenedAt = Now,
                LastSeenAt = Now,
            });

            await db.SaveChangesAsync(Ct);
        }

        await ListedAsync();
        VRChat.Instances.Page(Instance, nUsers: 3, userCount: 2);
        await RunInstanceHeadCountsAsync();

        // The list stops carrying the instance: it has closed, and its page is not asked about again.
        VRChat.Groups.Instances.Clear();
        await RunGroupInstancesAsync();
        Clock.Advance(InstanceHeadCountSync.ReadEvery);

        Assert.Equal(SyncOutcome.Quiet, (await RunInstanceHeadCountsAsync()).Outcome);
        Assert.Equal([Instance], VRChat.Instances.Requests);
    }
}
