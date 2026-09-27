using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Places;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Places;

/// <summary>
/// How an instance compared with the other instances in its world: read from the world's page as it
/// was saved every two minutes, bounded to the instance's own open stretch, and ranked against the
/// list's numbers.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class InstanceWorldTests
{
    private readonly PostgresFixture _db;

    public InstanceWorldTests(PostgresFixture db) => _db = db;

    private static async Task ReadingsAsync(ReadSurfaceTestHost host, CancellationToken ct, params WorldHeadCount[] readings)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        db.WorldHeadCounts.AddRange(readings);
        await db.SaveChangesAsync(ct);
    }

    private static WorldHeadCount Read(string worldId, DateTimeOffset at, int? occupants, string instances) => new()
    {
        WorldId = worldId,
        CountedAt = at,
        Occupants = occupants,
        Instances = instances,
    };

    [Fact]
    public async Task AnUnknownInstance_Is404()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var response = await host.GetAsync($"/api/instances/{Guid.NewGuid()}/world", cookie, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WithoutViewAnalytics_Is403()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var response = await host.GetAsync($"/api/instances/{Guid.NewGuid()}/world", cookie, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Every instance from before the world was being read. Nothing to compare, and nothing made up.</summary>
    [Fact]
    public async Task AnInstanceWithNoReadings_ComesBackEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-4);
        var instance = await PlacesFixtures.InstanceAsync(host, "wrld_a", "39047", t, t.AddHours(2), t.AddHours(2), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var view = await host.GetJsonAsync<InstanceWorldView>($"/api/instances/{instance.Id}/world", cookie, ct);

        Assert.Empty(view.Readings);
        Assert.Empty(view.Others);
        Assert.Null(view.AtPeak);
        Assert.Equal(0m, view.BusiestMinutes);
    }

    [Fact]
    public async Task EachRead_RanksTheInstance_AgainstTheWorldsList()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-4);
        var instance = await PlacesFixtures.InstanceAsync(host, "wrld_a", "39047", t, t.AddHours(1), t.AddHours(1), ct);

        await ReadingsAsync(host, ct,
            Read("wrld_a", t.AddMinutes(2), 100, """[["39047~group(grp_1)~groupAccessType(public)~region(us)",10],["500~region(eu)",20],["600",5]]"""),
            Read("wrld_a", t.AddMinutes(4), 120, """[["39047~group(grp_1)~groupAccessType(public)~region(us)",30],["500~region(eu)",25],["600",5]]"""),
            Read("wrld_a", t.AddMinutes(6), 90, """[["39047~group(grp_1)~groupAccessType(public)~region(us)",18],["500~region(eu)",26]]"""));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var view = await host.GetJsonAsync<InstanceWorldView>($"/api/instances/{instance.Id}/world", cookie, ct);

        Assert.Equal([(2, 3), (1, 3), (2, 2)], view.Readings.Select(r => (r.Rank!.Value, r.Of)));
        Assert.All(view.Readings, r => Assert.True(r.Listed));
        Assert.Equal([10, 30, 18], view.Readings.Select(r => r.People!.Value));

        var peak = Assert.IsType<WorldReading>(view.AtPeak);
        Assert.Equal((30, 1, 3, (int?)120), (peak.People!.Value, peak.Rank!.Value, peak.Of, peak.Occupants));

        // The busiest other first; this instance is never one of the others.
        Assert.Equal(["500~region(eu)", "600"], view.Others.Select(o => o.InstanceId));
        Assert.Equal(2, view.OthersTotal);

        var busiest = view.Others[0];
        Assert.Equal(("500", 26, "eu"), (busiest.Number, busiest.Peak, busiest.Region));
        Assert.Equal([20, 25, 26], busiest.Readings.Select(r => r.People));
        Assert.Equal((t.AddMinutes(2), t.AddMinutes(6)), (busiest.FirstSeenAt, busiest.LastSeenAt));

        // First only at the second read, which speaks for the two minutes to the third.
        Assert.Equal(2m, view.BusiestMinutes);
    }

    /// <summary>
    /// When the world's list leaves the instance out, its own head count at that moment stands in, and
    /// the read says it was not listed. An unsure count stays unsure.
    /// </summary>
    [Fact]
    public async Task AnInstanceTheListLeavesOut_IsRankedByItsOwnHeadCount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-4);
        var instance = await PlacesFixtures.InstanceAsync(host, "wrld_a", "39047", t, t.AddHours(1), t.AddHours(1), ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.InstanceHeadCounts.AddRange(
                new InstanceHeadCount { InstanceId = instance.Id, CountedAt = t.AddMinutes(1), HeadCount = 8, UserCount = 8, Source = HeadCounts.FromPage },
                new InstanceHeadCount { InstanceId = instance.Id, CountedAt = t.AddMinutes(3), HeadCount = 40, Source = HeadCounts.FromPage });
            await db.SaveChangesAsync(ct);
        }

        await ReadingsAsync(host, ct,
            Read("wrld_a", t.AddMinutes(2), 50, """[["500",12]]"""),
            Read("wrld_a", t.AddMinutes(4), 60, """[["500",12]]"""));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var view = await host.GetJsonAsync<InstanceWorldView>($"/api/instances/{instance.Id}/world", cookie, ct);

        Assert.Equal(
            [(8, false, false, 2, 2), (40, true, false, 1, 2)],
            view.Readings.Select(r => (r.People!.Value, r.Unsure, r.Listed, r.Rank!.Value, r.Of)));
    }

    /// <summary>
    /// Only this world, and only while this instance was open. VRChat reissues numbers, so a read from
    /// another evening under the same number must not count.
    /// </summary>
    [Fact]
    public async Task ReadsOutsideTheInstancesOwnStretch_OrWorld_DoNotCount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-4);
        var instance = await PlacesFixtures.InstanceAsync(host, "wrld_a", "39047", t, t.AddHours(1), t.AddHours(1), ct);

        await ReadingsAsync(host, ct,
            Read("wrld_a", t.AddMinutes(-2), 1, """[["39047",1]]"""),
            Read("wrld_a", t.AddMinutes(30), 2, """[["39047",2]]"""),
            Read("wrld_b", t.AddMinutes(30), 3, """[["39047",3]]"""),
            Read("wrld_a", t.AddHours(1).AddMinutes(2), 4, """[["39047",4]]"""));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var view = await host.GetJsonAsync<InstanceWorldView>($"/api/instances/{instance.Id}/world", cookie, ct);

        var only = Assert.Single(view.Readings);
        Assert.Equal((t.AddMinutes(30), 2), (only.At, only.People!.Value));
    }

    /// <summary>
    /// Another instance Modbot has a row for can be opened: same world, same number, open while the
    /// list carried it. The group's own instance in the same world is the usual case.
    /// </summary>
    [Fact]
    public async Task AnotherInstanceModbotKnows_CarriesItsIdAndName()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-4);
        var instance = await PlacesFixtures.InstanceAsync(host, "wrld_a", "39047", t, t.AddHours(1), t.AddHours(1), ct);
        var sister = await PlacesFixtures.InstanceAsync(host, "wrld_a", "500", t, t.AddHours(1), t.AddHours(1), ct, name: "Late show");

        // An earlier evening under the same number is not the one the list meant.
        await PlacesFixtures.InstanceAsync(host, "wrld_a", "600", t.AddDays(-3), t.AddDays(-3).AddHours(1), t.AddDays(-3).AddHours(1), ct);

        await ReadingsAsync(host, ct,
            Read("wrld_a", t.AddMinutes(10), 40, """[["39047",10],["500~group(grp_1)~groupAccessType(plus)",8],["600",3]]"""));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var view = await host.GetJsonAsync<InstanceWorldView>($"/api/instances/{instance.Id}/world", cookie, ct);

        var known = view.Others.Single(o => o.Number == "500");
        Assert.Equal((sister.Id, "Late show", "grp_1", "plus"), (known.ModbotInstanceId!.Value, known.Name, known.GroupId, known.GroupAccessType));

        Assert.Null(view.Others.Single(o => o.Number == "600").ModbotInstanceId);
    }

    [Fact]
    public async Task OnlyTheBusiestOthersAreShown_ButAllAreCounted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-4);
        var instance = await PlacesFixtures.InstanceAsync(host, "wrld_a", "39047", t, t.AddHours(1), t.AddHours(1), ct);

        var entries = string.Join(",", Enumerable.Range(1, 12).Select(n => $"[\"{n}\",{n}]"));
        await ReadingsAsync(host, ct, Read("wrld_a", t.AddMinutes(10), 100, $"[[\"39047\",4],{entries}]"));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var view = await host.GetJsonAsync<InstanceWorldView>($"/api/instances/{instance.Id}/world", cookie, ct);

        Assert.Equal(12, view.OthersTotal);
        Assert.Equal(InstanceWorldView.OthersShown, view.Others.Count);
        Assert.Equal(Enumerable.Range(5, 8).Reverse().Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)), view.Others.Select(o => o.Number!));
        Assert.Equal((9, 13), (view.Readings[0].Rank!.Value, view.Readings[0].Of));
    }

    /// <summary>A read speaks for at most five minutes, so a gap in the reads is never counted as busiest.</summary>
    [Fact]
    public void BusiestFor_StopsAtAGapInTheReads()
    {
        var t = new DateTimeOffset(2026, 9, 27, 20, 0, 0, TimeSpan.Zero);

        WorldReading At(int minute, int rank, int of = 3) =>
            new(t.AddMinutes(minute), null, null, null, 10, false, true, rank, of);

        var readings = new[] { At(0, 1), At(2, 1), At(30, 2), At(32, 1), At(34, 1, of: 1) };

        // 0→2 counts 2; 2→30 counts at most 5; 30 is second; 32→34 counts 2; alone at 34 counts nothing.
        Assert.Equal(9m, InstanceWorldQuery.BusiestMinutes(readings, t.AddMinutes(40)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void AListThatCannotBeRead_HasNoEntries(string? json)
        => Assert.Empty(InstanceWorldQuery.Entries(json));

    /// <summary>An entry of any shape but an id and a whole number is skipped, and so is an id seen twice.</summary>
    [Fact]
    public void OddEntries_AreSkipped_NotGuessedAt()
    {
        var entries = InstanceWorldQuery.Entries("""[["1~region(us)",4],["2"],[3,4],["4","5"],["5",-1],["6",2.5],"7",["1~region(us)",9],["8",0]]""");

        Assert.Equal([("1~region(us)", 4), ("8", 0)], entries);
    }

    [Theory]
    [InlineData("16354~group(grp_1)~region(eu)", "16354")]
    [InlineData("16354", "16354")]
    [InlineData("~region(eu)", null)]
    public void TheNumber_IsEverythingBeforeTheFirstTilde(string instanceId, string? number)
        => Assert.Equal(number, InstanceWorldQuery.NumberOf(instanceId));
}
