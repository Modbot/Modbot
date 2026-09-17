using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Data;

/// <summary>
/// Recording instances and worlds several times in one pass, before anything is saved.
/// </summary>
/// <remarks>
/// The group instance poll records every open instance, and the world each one is in, and saves once
/// at the end. A world or instance added earlier in that pass is not in the database yet. Looking for it
/// with a query alone found nothing and added it again, and EF Core refused to track the second
/// copy -- failing the whole poll every ten seconds for as long as an instance sat in a new world.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class PlaceStoreTests
{
    private static readonly DateTimeOffset Evening = new(2026, 9, 14, 21, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _db;

    public PlaceStoreTests(PostgresFixture db) => _db = db;

    private static string NewWorld() => $"wrld_{Guid.NewGuid()}";

    private static string Location(string world, string number) =>
        $"{world}:{number}~group(grp_0a17232e-6ad4-4889-8e1e-6e0c5fa815fd)~groupAccessType(plus)~region(us)";

    [Fact]
    public async Task ANewWorldNotedTwiceBeforeSavingIsOneWorld()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = NewWorld();

        await using (var context = _db.NewContext())
        {
            var places = new PlaceStore(context, new FakeClock(Evening));

            await places.NoteWorldSeenAsync(world, Evening, ct);
            await places.NoteWorldSeenAsync(world, Evening.AddSeconds(1), ct);

            await context.SaveChangesAsync(ct);
        }

        await using var read = _db.NewContext();
        var row = Assert.Single(await read.VRChatWorlds.Where(w => w.WorldId == world).ToListAsync(ct));
        Assert.Equal(Evening, row.FirstSeenAt);
        Assert.Equal(Evening.AddSeconds(1), row.LastSeenAt);
    }

    /// <summary>The crash as it happened: an instance in a brand-new world, then the world recorded again.</summary>
    [Fact]
    public async Task AnInstanceInANewWorldThenTheWorldAgainSavesCleanly()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = NewWorld();

        await using (var context = _db.NewContext())
        {
            var places = new PlaceStore(context, new FakeClock(Evening));

            var instance = await places.RecordSightingAsync(Location(world, "68681"), Evening, userCount: 3, fromGroupList: true, ct: ct);
            Assert.NotNull(instance);

            await places.NoteWorldSeenAsync(world, Evening, ct);

            await context.SaveChangesAsync(ct);
        }

        await using var read = _db.NewContext();
        Assert.Single(await read.VRChatWorlds.Where(w => w.WorldId == world).ToListAsync(ct));
        Assert.Single(await read.VRChatInstances.Where(i => i.WorldId == world).ToListAsync(ct));
    }

    [Fact]
    public async Task TwoInstancesInTheSameNewWorldInOnePassAreTwoInstancesAndOneWorld()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = NewWorld();

        await using (var context = _db.NewContext())
        {
            var places = new PlaceStore(context, new FakeClock(Evening));

            await places.RecordSightingAsync(Location(world, "11111"), Evening, fromGroupList: true, ct: ct);
            await places.RecordSightingAsync(Location(world, "22222"), Evening, fromGroupList: true, ct: ct);

            await context.SaveChangesAsync(ct);
        }

        await using var read = _db.NewContext();
        Assert.Single(await read.VRChatWorlds.Where(w => w.WorldId == world).ToListAsync(ct));
        Assert.Equal(2, await read.VRChatInstances.CountAsync(i => i.WorldId == world, ct));
    }

    /// <summary>
    /// The same location twice before saving is one instance. A query alone could not see the instance the
    /// first sighting added, and would have opened a second one.
    /// </summary>
    [Fact]
    public async Task TheSameInstanceSeenTwiceBeforeSavingIsOneInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = NewWorld();
        var location = Location(world, "68681");

        await using (var context = _db.NewContext())
        {
            var places = new PlaceStore(context, new FakeClock(Evening));

            var first = await places.RecordSightingAsync(location, Evening, userCount: 2, fromGroupList: true, ct: ct);
            var second = await places.RecordSightingAsync(location, Evening.AddSeconds(10), userCount: 5, fromGroupList: true, ct: ct);

            Assert.Same(first, second);

            await context.SaveChangesAsync(ct);
        }

        await using var read = _db.NewContext();
        var instance = Assert.Single(await read.VRChatInstances.Where(i => i.Location == location).ToListAsync(ct));
        Assert.Equal(5, instance.PeakUserCount);
        Assert.Equal(Evening.AddSeconds(10), instance.LastSeenAt);
    }
}
