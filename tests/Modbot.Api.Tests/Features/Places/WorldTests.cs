using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Places;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Places;

[Collection(nameof(PostgresCollection))]
public class WorldTests
{
    private readonly PostgresFixture _db;

    public WorldTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task AWorldWithNoNameYet_IsOrdinary_AndSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        await PlacesFixtures.WorldAsync(host, "wrld_a", name: null, host.Clock.UtcNow, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var world = await host.GetJsonAsync<WorldView>("/api/worlds?id=wrld_a", cookie, ct);

        Assert.True(world.Known);
        Assert.Null(world.Name);
        Assert.Null(world.LastReadAt);
        Assert.Equal("wrld_a", world.WorldId);
    }

    /// <summary>
    /// An id nobody has ever stored a row for still answers. The popup is opened from an id on a
    /// screen, and the honest answer is "Modbot has only ever seen the id".
    /// </summary>
    [Fact]
    public async Task AWorldModbotHasNeverStored_AnswersAsUnknown()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var world = await host.GetJsonAsync<WorldView>("/api/worlds?id=wrld_never-seen", cookie, ct);

        Assert.False(world.Known);
        Assert.Null(world.Name);
        Assert.Empty(world.Instances);
        Assert.Equal(0, world.InstancesTotal);
    }

    [Fact]
    public async Task TheInstancesInAWorld_AreListedNewestFirst_AndOnlyThatWorlds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddDays(-1);

        await PlacesFixtures.WorldAsync(host, "wrld_a", "The Black Cat", t, ct);
        await PlacesFixtures.InstanceAsync(host, "wrld_a", "1", t, t.AddHours(2), t.AddHours(2), ct);
        await PlacesFixtures.InstanceAsync(host, "wrld_a", "2", t.AddHours(5), t.AddHours(6), null, ct);
        await PlacesFixtures.InstanceAsync(host, "wrld_b", "3", t.AddHours(9), t.AddHours(9), null, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var world = await host.GetJsonAsync<WorldView>("/api/worlds?id=wrld_a", cookie, ct);

        Assert.Equal("The Black Cat", world.Name);
        Assert.Equal(2, world.InstancesTotal);
        Assert.Equal(1, world.InstancesOpenNow);
        Assert.Equal(["2", "1"], world.Instances.Select(r => r.VRChatInstanceId));

        // The world's name travels onto every instance row, so the popup and the Instances page
        // cannot disagree about what the place is called.
        Assert.All(world.Instances, r => Assert.Equal("The Black Cat", r.WorldName));
    }

    /// <summary>
    /// How many are in an instance is the instance page's head count. The group list's number counts
    /// group members only, so an instance with a friend in it read one short. The list's number
    /// stands in only before the page has been read. The world's capacity rides along for "25/40".
    /// </summary>
    [Fact]
    public async Task AnInstanceRow_CountsEveryoneInIt_NotOnlyGroupMembers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-1);

        await PlacesFixtures.WorldAsync(host, "wrld_a", "The Black Cat", t, ct);
        var counted = await PlacesFixtures.InstanceAsync(host, "wrld_a", "1", t, t.AddMinutes(30), null, ct);
        await PlacesFixtures.InstanceAsync(host, "wrld_a", "2", t.AddMinutes(5), t.AddMinutes(30), null, ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            await db.VRChatInstances.Where(i => i.Id == counted.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.HeadCount, 5), ct);
            await db.VRChatWorlds.Where(w => w.WorldId == "wrld_a")
                .ExecuteUpdateAsync(u => u
                    .SetProperty(w => w.Capacity, 40)
                    .SetProperty(w => w.Platforms, """["android","standalonewindows"]"""), ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var world = await host.GetJsonAsync<WorldView>("/api/worlds?id=wrld_a", cookie, ct);

        // The fixture's group list says 3 for both; only the first has had its page read.
        Assert.Equal(5, world.Instances.Single(r => r.VRChatInstanceId == "1").PeopleNow);
        Assert.Equal(3, world.Instances.Single(r => r.VRChatInstanceId == "2").PeopleNow);
        Assert.All(world.Instances, r => Assert.Equal(40, r.WorldCapacity));
        Assert.All(world.Instances, r => Assert.Equal(["android", "standalonewindows"], r.WorldPlatforms!));
    }

    /// <summary>
    /// The same arithmetic the Worlds page uses, over one world and all of recorded history
    /// rather than over a window.
    /// </summary>
    [Fact]
    public async Task TimeSeen_IsSummedFromPresenceSessions_ForThisWorldOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-6);

        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_a", t, "wrld_a", "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstancePresenceObserved, "usr_b", t.AddMinutes(10), "wrld_a", "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_a", t.AddMinutes(30), "wrld_a", "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_c", t, "wrld_b", "9"), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var world = await host.GetJsonAsync<WorldView>("/api/worlds?id=wrld_a", cookie, ct);

        Assert.Equal(50m, world.Counts.MinutesSeen);
        Assert.Equal(2, world.Counts.Visitors);
        Assert.Equal(2, world.Counts.Arrivals);
        Assert.Equal(t.AddMinutes(30), world.Counts.LastSeenAt);
    }

    [Fact]
    public async Task WithoutAnId_Is400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var response = await host.GetAsync("/api/worlds?id=", cookie, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WithoutViewAnalytics_Is403()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var response = await host.GetAsync("/api/worlds?id=wrld_a", cookie, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnUnauthenticatedCaller_Gets401()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var response = await host.Client.GetAsync("/api/worlds?id=wrld_a", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
