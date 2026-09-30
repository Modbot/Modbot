using System.Net;
using Microsoft.EntityFrameworkCore;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// Another group's instance name and group name for the World tab: asked of VRChat once, kept
/// whatever VRChat said, never asked for an instance whose location says outsiders cannot join, and
/// never retried after a 429.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class OtherInstanceNamesTests(PostgresFixture fixture) : SyncTestBase(fixture)
{
    private const string OtherGroup = "grp_cats";
    private const string Open = $"wrld_a:16354~group({OtherGroup})~groupAccessType(public)~region(eu)";
    private const string Public = "wrld_a:22222~region(us)";
    private const string MembersOnly = $"wrld_a:30000~group({OtherGroup})~groupAccessType(members)~region(eu)";
    private const string InviteOnly = "wrld_a:69955~private(usr_f)~region(use)";

    private async Task<OtherNameOutcome> ReadAsync(OtherNameKind kind, string id)
    {
        await using var context = Database.NewContext();
        return await new OtherNameReader(Gate, context, Clock).ReadAsync(new OtherNameRequest(kind, id), Ct);
    }

    [Fact]
    public async Task AnInstancesName_IsAskedOnce_AndKept()
    {
        VRChat.Instances.Page(Open, nUsers: 12, userCount: 12, displayName: "  Game night ");

        Assert.Equal(OtherNameOutcome.Saved, await ReadAsync(OtherNameKind.Instance, Open));
        Assert.Equal(OtherNameOutcome.AlreadyKnown, await ReadAsync(OtherNameKind.Instance, Open));

        Assert.Equal(Open, Assert.Single(VRChat.Instances.Requests));

        await using var db = Database.NewContext();
        var kept = await db.OtherInstanceNames.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((Open, "Game night", false, Now), (kept.Location, kept.Name, kept.Refused, kept.AskedAt));
    }

    /// <summary>A name that is only the number is no name, as for the group's own instances.</summary>
    [Fact]
    public async Task NoName_IsKeptAsAsked_AndNotAskedAgain()
    {
        VRChat.Instances.Page(Public, nUsers: 3, userCount: 3, displayName: "22222");

        Assert.Equal(OtherNameOutcome.Saved, await ReadAsync(OtherNameKind.Instance, Public));
        Assert.Equal(OtherNameOutcome.AlreadyKnown, await ReadAsync(OtherNameKind.Instance, Public));

        Assert.Single(VRChat.Instances.Requests);

        await using var db = Database.NewContext();
        var kept = await db.OtherInstanceNames.AsNoTracking().SingleAsync(Ct);
        Assert.Null(kept.Name);
        Assert.False(kept.Refused);
    }

    [Theory]
    [InlineData(MembersOnly)]
    [InlineData(InviteOnly)]
    [InlineData($"wrld_a:3~group({OtherGroup})~groupAccessType(plus)~region(us)")]
    [InlineData("wrld_a:4~friends(usr_5)~region(use)")]
    [InlineData("wrld_a:5~hidden(usr_5)~region(eu)")]
    public async Task AnInstanceClosedToOutsiders_IsNeverAsked(string location)
    {
        Assert.Equal(OtherNameOutcome.NotAsked, await ReadAsync(OtherNameKind.Instance, location));

        Assert.Empty(VRChat.Instances.Requests);

        await using var db = Database.NewContext();
        Assert.Empty(await db.OtherInstanceNames.AsNoTracking().ToListAsync(Ct));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ARefusal_IsKeptAsRefused_AndNotAskedAgain(HttpStatusCode status)
    {
        VRChat.Instances.Status(Open, status);

        Assert.Equal(OtherNameOutcome.Saved, await ReadAsync(OtherNameKind.Instance, Open));
        Assert.Equal(OtherNameOutcome.AlreadyKnown, await ReadAsync(OtherNameKind.Instance, Open));

        Assert.Single(VRChat.Instances.Requests);

        await using var db = Database.NewContext();
        var kept = await db.OtherInstanceNames.AsNoTracking().SingleAsync(Ct);
        Assert.True(kept.Refused);
        Assert.Null(kept.Name);
    }

    /// <summary>A 429 is not an answer: nothing is kept, and nothing reaches VRChat while the bucket is cold.</summary>
    [Fact]
    public async Task A429_KeepsNothing_AndIsNotRetried()
    {
        VRChat.Instances.Status(Open, HttpStatusCode.TooManyRequests);

        Assert.Equal(OtherNameOutcome.Paused, await ReadAsync(OtherNameKind.Instance, Open));

        VRChat.Instances.Page(Open, nUsers: 12, userCount: 12, displayName: "Game night");
        Assert.Equal(OtherNameOutcome.Paused, await ReadAsync(OtherNameKind.Instance, Open));

        Assert.Single(VRChat.Instances.Requests);

        await using var db = Database.NewContext();
        Assert.Empty(await db.OtherInstanceNames.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task AGroupsName_IsAskedOnce_AndKept()
    {
        var group = GroupInfoSnapshotTests.Group();
        group.Name = "Cats Club";
        VRChat.Groups.Group = group;

        Assert.Equal(OtherNameOutcome.Saved, await ReadAsync(OtherNameKind.Group, OtherGroup));
        Assert.Equal(OtherNameOutcome.AlreadyKnown, await ReadAsync(OtherNameKind.Group, OtherGroup));

        Assert.Equal(1, VRChat.Groups.GroupRequests);

        await using var db = Database.NewContext();
        var kept = await db.OtherGroupNames.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((OtherGroup, "Cats Club", false), (kept.GroupId, kept.Name, kept.Refused));
    }

    [Fact]
    public async Task AGroupRefusal_IsKeptAsRefused()
    {
        VRChat.Groups.GroupStatus = HttpStatusCode.Forbidden;

        Assert.Equal(OtherNameOutcome.Saved, await ReadAsync(OtherNameKind.Group, OtherGroup));
        Assert.Equal(OtherNameOutcome.AlreadyKnown, await ReadAsync(OtherNameKind.Group, OtherGroup));

        Assert.Equal(1, VRChat.Groups.GroupRequests);

        await using var db = Database.NewContext();
        var kept = await db.OtherGroupNames.AsNoTracking().SingleAsync(Ct);
        Assert.True(kept.Refused);
        Assert.Null(kept.Name);
    }
}

/// <summary>
/// The queue between the World tab and the one service that asks: each name waits once, however many
/// popups offer it, and a stopped bucket lets its names go.
/// </summary>
public class OtherNameQueueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly OtherNameRequest Instance = new(OtherNameKind.Instance, "wrld_a:16354~region(eu)");
    private static readonly OtherNameRequest Group = new(OtherNameKind.Group, "grp_cats");

    [Fact]
    public async Task ANameOfferedTwice_WaitsOnce()
    {
        var queue = new OtherNameQueue();

        Assert.True(queue.Offer(Instance));
        Assert.True(queue.Offer(Instance));
        Assert.Equal(1, queue.Count);

        var taken = await queue.NextAsync(Ct);
        Assert.Equal(Instance, taken);

        // Still being read: offering it again neither queues it nor says it is not coming.
        Assert.True(queue.Offer(Instance));
        Assert.True(queue.IsWaiting(Instance));
        Assert.Equal(0, queue.Count);

        queue.Done(taken);
        Assert.False(queue.IsWaiting(Instance));
    }

    [Fact]
    public async Task DroppingOneKind_LeavesTheOther()
    {
        var queue = new OtherNameQueue();
        queue.Offer(Instance);
        queue.Offer(Group);

        Assert.Equal(1, queue.DropWaiting(OtherNameKind.Instance));
        Assert.False(queue.IsWaiting(Instance));

        Assert.Equal(Group, await queue.NextAsync(Ct));
    }

    [Fact]
    public void AFullQueue_TurnsNewNamesAway()
    {
        var queue = new OtherNameQueue();

        for (var i = 0; i < OtherNameQueue.MostWaiting; i++)
            Assert.True(queue.Offer(new OtherNameRequest(OtherNameKind.Instance, $"wrld_a:{i}")));

        Assert.False(queue.Offer(Group));
        Assert.False(queue.IsWaiting(Group));
    }
}
