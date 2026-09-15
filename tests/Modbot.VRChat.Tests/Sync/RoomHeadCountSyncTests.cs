using System.Net;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// A room's head count: the room's own page when it can be read and believed, the group list's
/// number when it cannot, and one change-log row each time the number moves.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class RoomHeadCountSyncTests(PostgresFixture fixture) : SyncTestBase(fixture)
{
    private const string Room = $"wrld_4432:68681~group({GroupId})~groupAccessType(plus)~region(us)";

    /// <summary>The group's list carries the room with this many members, and has been polled.</summary>
    private async Task ListedAsync(int memberCount = 2)
    {
        VRChat.Groups.Instances.Clear();
        VRChat.Groups.Instances.Add(FakeGroups.Listed(Room, memberCount));
        await RunGroupInstancesAsync();
    }

    private async Task<VRChatInstance> RoomAsync()
    {
        await using var db = Database.NewContext();
        return await db.VRChatInstances.AsNoTracking().SingleAsync(i => i.Location == Room, Ct);
    }

    private async Task<List<InstanceHeadCount>> ChangesAsync()
    {
        await using var db = Database.NewContext();
        return await db.InstanceHeadCounts.AsNoTracking().OrderBy(c => c.Id).ToListAsync(Ct);
    }

    [Fact]
    public async Task TheListsCountStandsInUntilTheRoomsPageIsRead()
    {
        await ListedAsync(memberCount: 2);

        var room = await RoomAsync();
        Assert.Equal(2, room.HeadCount);
        Assert.Equal(HeadCounts.FromList, room.HeadCountSource);

        var change = Assert.Single(await ChangesAsync());
        Assert.Equal(2, change.HeadCount);
        Assert.Equal(HeadCounts.FromList, change.Source);
        Assert.Equal(room.Id, change.InstanceId);
    }

    [Fact]
    public async Task TheRoomsOwnPage_SetsTheHeadCount_WithUserCountKeptBeside()
    {
        await ListedAsync(memberCount: 2);
        VRChat.Instances.Page(Room, nUsers: 3, userCount: 2);

        var run = await RunRoomHeadCountsAsync();

        Assert.Equal(SyncOutcome.Produced, run.Outcome);
        Assert.Equal(Room, Assert.Single(VRChat.Instances.Requests));

        var room = await RoomAsync();
        Assert.Equal(3, room.HeadCount);
        Assert.Equal(HeadCounts.FromRoom, room.HeadCountSource);
        Assert.Equal(2, room.PageUserCount);
        Assert.Equal(Now, room.PageReadAt);

        var changes = await ChangesAsync();
        Assert.Equal(2, changes.Count);
        Assert.Equal((3, 2, 2, HeadCounts.FromRoom), (changes[1].HeadCount, changes[1].UserCount, changes[1].MemberCount, changes[1].Source));
    }

    [Fact]
    public async Task ARoomThatReadsInactive_KeepsTheListsCount()
    {
        // The endpoint answers 200 for a room that never existed and says active: false. A zero in
        // that body is not a head count.
        await ListedAsync(memberCount: 2);
        VRChat.Instances.Page(Room, nUsers: 0, userCount: 0, active: false);

        await RunRoomHeadCountsAsync();

        var room = await RoomAsync();
        Assert.Equal(2, room.HeadCount);
        Assert.Equal(HeadCounts.FromList, room.HeadCountSource);
        Assert.Null(room.PageReadAt);
        Assert.Single(await ChangesAsync());
    }

    [Fact]
    public async Task A429ColdStops_IsNotRetried_AndTheListsCountStands()
    {
        await ListedAsync(memberCount: 2);
        VRChat.Instances.Status(Room, HttpStatusCode.TooManyRequests);

        var limited = await RunRoomHeadCountsAsync();

        Assert.Equal(SyncOutcome.RateLimited, limited.Outcome);
        Assert.Single(VRChat.Instances.Requests);

        // Whatever the page would say now is irrelevant: nothing should reach it while the bucket
        // is cold, however often the loop comes round.
        VRChat.Instances.Page(Room, nUsers: 9, userCount: 9);
        Clock.Advance(RoomHeadCountSync.ReadEvery);

        Assert.Equal(SyncOutcome.RateLimited, (await RunRoomHeadCountsAsync()).Outcome);
        Assert.Single(VRChat.Instances.Requests);

        Assert.Equal(2, (await RoomAsync()).HeadCount);
    }

    [Fact]
    public async Task AFailedRead_FallsBackToTheListsCount()
    {
        await ListedAsync(memberCount: 2);
        VRChat.Instances.Page(Room, nUsers: 3, userCount: 2);
        await RunRoomHeadCountsAsync();

        VRChat.Instances.Status(Room, HttpStatusCode.InternalServerError);
        Clock.Advance(RoomHeadCountSync.ReadEvery);
        await RunRoomHeadCountsAsync();

        var room = await RoomAsync();
        Assert.Equal(2, room.HeadCount);
        Assert.Equal(HeadCounts.FromList, room.HeadCountSource);
    }

    [Fact]
    public async Task AFreshPageRead_WinsOverTheList_UntilItGoesStale()
    {
        await ListedAsync(memberCount: 2);
        VRChat.Instances.Page(Room, nUsers: 3, userCount: 2);
        await RunRoomHeadCountsAsync();

        Clock.Advance(TimeSpan.FromSeconds(10));
        await ListedAsync(memberCount: 2);
        Assert.Equal(3, (await RoomAsync()).HeadCount);

        Clock.Advance(HeadCounts.RoomReadGoesStaleAfter);
        await ListedAsync(memberCount: 2);

        var room = await RoomAsync();
        Assert.Equal(2, room.HeadCount);
        Assert.Equal(HeadCounts.FromList, room.HeadCountSource);
    }

    [Fact]
    public async Task EachRoomIsReadAboutEveryThirtySeconds()
    {
        await ListedAsync();
        VRChat.Instances.Page(Room, nUsers: 3, userCount: 2);

        await RunRoomHeadCountsAsync();
        Assert.Equal(SyncOutcome.Quiet, (await RunRoomHeadCountsAsync()).Outcome);
        Assert.Single(VRChat.Instances.Requests);

        Clock.Advance(RoomHeadCountSync.ReadEvery);
        await RunRoomHeadCountsAsync();
        Assert.Equal(2, VRChat.Instances.Requests.Count);
    }

    [Fact]
    public async Task OneRowPerChange_NotPerRead()
    {
        await ListedAsync(memberCount: 2);

        VRChat.Instances.Page(Room, nUsers: 3, userCount: 2);
        await RunRoomHeadCountsAsync();

        Clock.Advance(RoomHeadCountSync.ReadEvery);
        await RunRoomHeadCountsAsync();

        Clock.Advance(RoomHeadCountSync.ReadEvery);
        VRChat.Instances.Page(Room, nUsers: 5, userCount: 3);
        await RunRoomHeadCountsAsync();

        Assert.Equal([2, 3, 5], (await ChangesAsync()).Select(c => c.HeadCount));
        Assert.Equal(5, (await RoomAsync()).PeakUserCount);
    }

    [Fact]
    public async Task OnlyRoomsTheGroupsListCarriesNowAreRead()
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
        VRChat.Instances.Page(Room, nUsers: 3, userCount: 2);
        await RunRoomHeadCountsAsync();

        // The list stops carrying the room: it has closed, and its page is not asked about again.
        VRChat.Groups.Instances.Clear();
        await RunGroupInstancesAsync();
        Clock.Advance(RoomHeadCountSync.ReadEvery);

        Assert.Equal(SyncOutcome.Quiet, (await RunRoomHeadCountsAsync()).Outcome);
        Assert.Equal([Room], VRChat.Instances.Requests);
    }
}
