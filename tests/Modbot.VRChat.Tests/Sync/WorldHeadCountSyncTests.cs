using System.Net;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// The world head count read: each world the group has an instance open in is read once a pass,
/// the page's counts and its <c>instances</c> list are kept as VRChat sent them, and nothing else is
/// read.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class WorldHeadCountSyncTests(PostgresFixture fixture) : SyncTestBase(fixture)
{
    private const string World = "wrld_4432";
    private const string OtherWorld = "wrld_9001";

    private const string Instance = $"{World}:68681~group({GroupId})~groupAccessType(public)~region(us)";
    private const string SecondInstance = $"{World}:12345~group({GroupId})~groupAccessType(plus)~region(eu)";
    private const string ElsewhereInstance = $"{OtherWorld}:555~group({GroupId})~groupAccessType(members)~region(us)";

    private const string Busy =
        """{"id":"wrld_4432","occupants":410,"publicOccupants":380,"privateOccupants":30,"instances":[["68681~group(grp_test)~groupAccessType(public)~region(us)",53],["70001~region(eu)",14]]}""";

    private async Task ListedAsync(params string[] locations)
    {
        VRChat.Groups.Instances.Clear();
        foreach (var location in locations)
            VRChat.Groups.Instances.Add(FakeGroups.Listed(location, memberCount: 1));

        await RunGroupInstancesAsync();
    }

    private async Task<List<WorldHeadCount>> ReadingsAsync()
    {
        await using var db = Database.NewContext();
        return await db.WorldHeadCounts.AsNoTracking().OrderBy(r => r.Id).ToListAsync(Ct);
    }

    [Fact]
    public async Task NoOpenInstance_ReadsNoWorld()
    {
        var run = await RunWorldHeadCountsAsync();

        Assert.Equal(SyncOutcome.Quiet, run.Outcome);
        Assert.Empty(VRChat.Worlds.Requests);
        Assert.Empty(await ReadingsAsync());
    }

    [Fact]
    public async Task AWorldWithAnOpenInstance_IsReadAndKeptAsSent()
    {
        await ListedAsync(Instance);
        VRChat.Worlds.Page(World, Busy);

        var run = await RunWorldHeadCountsAsync();

        Assert.Equal(SyncOutcome.Produced, run.Outcome);
        Assert.Equal(World, Assert.Single(VRChat.Worlds.Requests));

        var reading = Assert.Single(await ReadingsAsync());
        Assert.Equal(World, reading.WorldId);
        Assert.Equal(Now, reading.CountedAt);
        Assert.Equal((410, 380, 30), (reading.Occupants, reading.PublicOccupants, reading.PrivateOccupants));

        // The list is kept whole, qualifiers and all. jsonb may space it differently, so it is
        // compared as JSON rather than as text.
        using var kept = System.Text.Json.JsonDocument.Parse(reading.Instances!);
        using var sent = System.Text.Json.JsonDocument.Parse(Busy);
        Assert.True(System.Text.Json.JsonElement.DeepEquals(sent.RootElement.GetProperty("instances"), kept.RootElement));
    }

    [Fact]
    public async Task TwoInstancesInOneWorld_ReadTheWorldOnce()
    {
        await ListedAsync(Instance, SecondInstance, ElsewhereInstance);
        VRChat.Worlds.Page(World, Busy).Page(OtherWorld, """{"occupants":3,"instances":[]}""");

        var run = await RunWorldHeadCountsAsync();

        Assert.Equal(2, run.Read);
        Assert.Equal([OtherWorld, World], VRChat.Worlds.Requests.Order(StringComparer.Ordinal));
        Assert.Equal(2, (await ReadingsAsync()).Count);
    }

    [Fact]
    public async Task AClosedInstance_StopsItsWorldBeingRead()
    {
        await ListedAsync(Instance);
        VRChat.Worlds.Page(World, Busy);
        await RunWorldHeadCountsAsync();

        await ListedAsync();
        VRChat.Worlds.Requests.Clear();

        var run = await RunWorldHeadCountsAsync();

        Assert.Equal(SyncOutcome.Quiet, run.Outcome);
        Assert.Empty(VRChat.Worlds.Requests);
    }

    /// <summary>A body that does not say a number keeps it null, never nought: an empty world reads 0.</summary>
    [Fact]
    public async Task MissingNumbers_AreNull_NotNought()
    {
        await ListedAsync(Instance);
        VRChat.Worlds.Page(World, """{"id":"wrld_4432","occupants":null}""");

        await RunWorldHeadCountsAsync();

        var reading = Assert.Single(await ReadingsAsync());
        Assert.Null(reading.Occupants);
        Assert.Null(reading.PublicOccupants);
        Assert.Null(reading.PrivateOccupants);
        Assert.Null(reading.Instances);
    }

    [Fact]
    public async Task AFailedRead_KeepsNothing_AndTheNextWorldIsStillRead()
    {
        await ListedAsync(Instance, ElsewhereInstance);
        VRChat.Worlds.Status(OtherWorld, HttpStatusCode.InternalServerError).Page(World, Busy);

        var run = await RunWorldHeadCountsAsync();

        Assert.Equal((1, 1), (run.Read, run.Failed));
        Assert.Equal(World, Assert.Single(await ReadingsAsync()).WorldId);
    }

    /// <summary>
    /// A 429 is a cold stop: never retried, the pass ends there, and nothing is asked while the
    /// bucket is cold (spec 4.3.1). Worlds are read in id order, so the first one is the one refused.
    /// </summary>
    [Fact]
    public async Task A429_StopsThePass_AndIsNotRetried()
    {
        await ListedAsync(Instance, ElsewhereInstance);
        VRChat.Worlds.Status(World, HttpStatusCode.TooManyRequests).Page(OtherWorld, Busy);

        var run = await RunWorldHeadCountsAsync();

        Assert.Equal(SyncOutcome.RateLimited, run.Outcome);
        Assert.Equal(World, Assert.Single(VRChat.Worlds.Requests));
        Assert.Empty(await ReadingsAsync());

        VRChat.Worlds.Page(World, Busy);
        Clock.Advance(WorldHeadCountSyncService.Interval);

        Assert.Equal(SyncOutcome.RateLimited, (await RunWorldHeadCountsAsync()).Outcome);
        Assert.Single(VRChat.Worlds.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void ABodyThatCannotBeRead_GivesNothing(string body)
        => Assert.Null(WorldHeadCountSync.ReadBody(body));
}
