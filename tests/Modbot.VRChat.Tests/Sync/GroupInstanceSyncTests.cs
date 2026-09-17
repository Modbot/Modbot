using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;
using Modbot.VRChat.Tests.Fakes;
using VRChat.API.Model;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// The group's own instance list, with the world VRChat attaches to each instance.
/// </summary>
/// <remarks>
/// The other place tests list instances with no world attached, so the step that names a world from the
/// list never ran in them. That step crashed on the first instance in any world Modbot had not saved
/// yet, and because the poll saves once at the end, it crashed again every ten seconds.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class GroupInstanceSyncTests(PostgresFixture fixture) : SyncTestBase(fixture)
{
    private static string Location(string world, string number) =>
        $"{world}:{number}~group({GroupId})~groupAccessType(plus)~region(us)";

    /// <summary>A listed instance with its world attached, as the real list returns it.</summary>
    private static GroupInstance InWorld(string location, string worldId, string worldName, int members = 2)
    {
        var instance = FakeGroups.Listed(location, members);

        var world = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));
        world.Id = worldId;
        world.Name = worldName;
        world.UpdatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        instance.World = world;
        return instance;
    }

    [Fact]
    public async Task AnInstanceInAWorldModbotHasNeverSeen_IsRecorded_AndTheWorldIsNamedFromTheList()
    {
        const string worldId = "wrld_never-seen-before";
        VRChat.Groups.Instances.Add(InWorld(Location(worldId, "68681"), worldId, "The Black Cat"));

        var run = await RunGroupInstancesAsync();

        Assert.Equal(SyncOutcome.Produced, run.Outcome);
        Assert.Equal(1, run.Opened);

        await using var db = Database.NewContext();
        var world = await db.VRChatWorlds.AsNoTracking().SingleAsync(w => w.WorldId == worldId, Ct);
        Assert.Equal("The Black Cat", world.Name);
        Assert.NotNull(world.LastRefreshedAt);
        Assert.Single(await db.VRChatInstances.AsNoTracking().Where(i => i.WorldId == worldId).ToListAsync(Ct));
    }

    [Fact]
    public async Task TwoInstancesInTheSameNewWorldInOnePoll_AreTwoInstancesAndOneWorld()
    {
        const string worldId = "wrld_two-instances-one-world";
        VRChat.Groups.Instances.Add(InWorld(Location(worldId, "11111"), worldId, "Popcorn Palace"));
        VRChat.Groups.Instances.Add(InWorld(Location(worldId, "22222"), worldId, "Popcorn Palace", members: 5));

        var run = await RunGroupInstancesAsync();

        Assert.Equal(SyncOutcome.Produced, run.Outcome);
        Assert.Equal(2, run.Opened);

        await using var db = Database.NewContext();
        var world = await db.VRChatWorlds.AsNoTracking().SingleAsync(w => w.WorldId == worldId, Ct);
        Assert.Equal("Popcorn Palace", world.Name);
        Assert.Equal(2, await db.VRChatInstances.CountAsync(i => i.WorldId == worldId, Ct));
    }

    [Fact]
    public async Task TheNextPollAfterANewWorld_IsQuietAndAddsNothing()
    {
        const string worldId = "wrld_polled-twice";
        VRChat.Groups.Instances.Add(InWorld(Location(worldId, "68681"), worldId, "VRChat Home"));

        await RunGroupInstancesAsync();
        Clock.Advance(TimeSpan.FromSeconds(10));
        var second = await RunGroupInstancesAsync();

        Assert.Equal(0, second.Opened);

        await using var db = Database.NewContext();
        Assert.Single(await db.VRChatWorlds.AsNoTracking().Where(w => w.WorldId == worldId).ToListAsync(Ct));
        Assert.Single(await db.VRChatInstances.AsNoTracking().Where(i => i.WorldId == worldId).ToListAsync(Ct));
    }
}
