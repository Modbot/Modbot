using Microsoft.EntityFrameworkCore;
using Modbot.AI.Alerts;
using Modbot.AI.Tests.Insights;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.AI.Tests.Alerts;

/// <summary>
/// The "open a second instance" alert: which head counts raise it, the percent needing a known
/// capacity, and once per instance (written, not run -- CI runs them when staging is merged).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class SecondInstanceCheckerTests : AlertTestBase
{
    private const string Group = "grp_test";
    private const string World = "wrld_test";

    public SecondInstanceCheckerTests(PostgresFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData(null, null, 40, null)]
    [InlineData(30, null, 40, 30)]
    [InlineData(null, 80, 40, 32)]
    [InlineData(null, 80, 12, 10)]
    [InlineData(null, 80, null, null)]
    [InlineData(30, 50, 40, 20)]
    [InlineData(30, 100, 40, 30)]
    [InlineData(30, 80, null, 30)]
    public void TheLineIsTheLowerOfWhatApplies_AndAPercentNeedsACapacity(int? people, int? percent, int? capacity, int? line)
        => Assert.Equal(line, SecondInstanceChecker.Threshold(people, percent, capacity));

    [Fact]
    public async Task AnInstanceAtTheNumberRaisesOneAlert_OnceForItsWholeLife()
    {
        var instance = await SeedAsync(headCount: 30, capacity: 40);
        await SetAlertSettingsAsync(s =>
        {
            s.SecondInstanceAtPeople = 30;
            s.DiscordChannelId = "123";
        });

        Assert.Equal(1, await RunSecondAsync());

        var alert = Assert.Single(await AlertsAsync());
        Assert.Equal(AlertWatchers.InstanceSecond, alert.Watcher);
        Assert.Equal(instance, alert.InstanceId);
        Assert.Equal(30m, alert.Now);
        Assert.Equal(30m, alert.Normal);
        Assert.Equal("123", alert.DiscordChannelId);
        Assert.Equal("/live", alert.Link);

        // It empties and fills again, or simply stays full: still the one alert.
        await SetHeadCountAsync(instance, 5);
        Assert.Equal(0, await RunSecondAsync());
        await SetHeadCountAsync(instance, 38);
        Assert.Equal(0, await RunSecondAsync());

        Assert.Single(await AlertsAsync());
    }

    [Fact]
    public async Task BelowTheNumberRaisesNothing()
    {
        await SeedAsync(headCount: 29, capacity: 40);
        await SetAlertSettingsAsync(s => s.SecondInstanceAtPeople = 30);

        Assert.Equal(0, await RunSecondAsync());
        Assert.Empty(await AlertsAsync());
    }

    [Fact]
    public async Task APercentUsesTheWorldsCapacity_AndNeedsOne()
    {
        await SeedAsync(headCount: 32, capacity: 40, world: "wrld_known");
        await SeedAsync(headCount: 90, capacity: null, world: "wrld_unknown", location: "wrld_unknown:2");
        await SetAlertSettingsAsync(s => s.SecondInstanceAtPercent = 80);

        Assert.Equal(1, await RunSecondAsync());

        var alert = Assert.Single(await AlertsAsync());
        Assert.Equal(32m, alert.Now);
        Assert.Equal(32m, alert.Normal);
    }

    [Fact]
    public async Task NothingSetRaisesNothing_AndNeitherDoesAClosedInstance()
    {
        var instance = await SeedAsync(headCount: 80, capacity: 40);

        Assert.Equal(0, await RunSecondAsync());

        await SetAlertSettingsAsync(s => s.SecondInstanceAtPeople = 10);
        await using (var context = NewContext())
        {
            await context.VRChatInstances.Where(i => i.Id == instance)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.ClosedAt, Start), Ct);
        }

        Assert.Equal(0, await RunSecondAsync());
        Assert.Empty(await AlertsAsync());
    }

    private async Task<int> RunSecondAsync()
    {
        Clock.UtcNow = Start;
        await using var context = NewContext();
        return await new SecondInstanceChecker(context, new FactWriter(context, Clock), new EventPartitionMaintainer(context, Clock), Clock)
            .RunAsync(Ct);
    }

    private async Task SetHeadCountAsync(Guid instance, int people)
    {
        await using var context = NewContext();
        await context.VRChatInstances.Where(i => i.Id == instance)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.HeadCount, people), Ct);
    }

    /// <summary>An open group instance of the managed group, in a world with the given capacity.</summary>
    private async Task<Guid> SeedAsync(int headCount, int? capacity, string world = World, string? location = null)
    {
        await using var context = NewContext();

        var settings = await context.GetSettingsAsync(Ct);
        settings.ManagedGroupId = Group;

        if (!await context.VRChatWorlds.AnyAsync(w => w.WorldId == world, Ct))
            context.VRChatWorlds.Add(new VRChatWorld { WorldId = world, Name = "Test world", Capacity = capacity });

        var instance = new VRChatInstance
        {
            Id = Guid.CreateVersion7(),
            Location = location ?? $"{world}:1~group({Group})~groupAccessType(plus)~region(us)",
            WorldId = world,
            VRChatInstanceId = "1",
            GroupId = Group,
            Type = "group",
            GroupAccessType = "plus",
            Region = "us",
            OpenedAt = Start.AddHours(-1),
            LastSeenAt = Start,
            SeenInGroupList = true,
            HeadCount = headCount,
        };

        context.VRChatInstances.Add(instance);
        await context.SaveChangesAsync(Ct);

        return instance.Id;
    }
}
