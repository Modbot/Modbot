using System.Net;
using Modbot.Api.Features.Places;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Places;

[Collection(nameof(PostgresCollection))]
public class PersonMetricsTests
{
    private readonly PostgresFixture _db;

    public PersonMetricsTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task SomebodyNeverSeen_ReadsAsNothing_RatherThanAsNeverThere()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, ct);
        var metrics = await host.GetJsonAsync<PersonMetrics>(
            "/api/vrchat-users/metrics?id=usr_nobody", cookie, ct);

        Assert.False(metrics.Known);
        Assert.Equal(0m, metrics.Counts.MinutesSeen);
        Assert.Empty(metrics.RecentRooms);
    }

    [Fact]
    public async Task TimeSeen_WorldsAndRooms_ComeFromTheirOwnSessions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-8);

        // Half an hour in one world, a quarter in another, and somebody else in the same room --
        // whose time must not land on this person's total.
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_a", t, "wrld_a", "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_a", t.AddMinutes(30), "wrld_a", "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_b", t, "wrld_a", "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_b", t.AddMinutes(30), "wrld_a", "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_a", t.AddHours(2), "wrld_b", "7"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_a", t.AddHours(2).AddMinutes(15), "wrld_b", "7"), ct);

        await PlacesFixtures.WorldAsync(host, "wrld_a", "The Black Cat", t, ct);
        await PlacesFixtures.RoomAsync(host, "wrld_a", "1", t, t.AddMinutes(30), t.AddMinutes(30), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, ct);
        var metrics = await host.GetJsonAsync<PersonMetrics>(
            "/api/vrchat-users/metrics?id=usr_a", cookie, ct);

        Assert.True(metrics.Known);
        Assert.Equal(45m, metrics.Counts.MinutesSeen);
        Assert.Equal(2, metrics.Counts.Worlds);
        Assert.Equal(2, metrics.Counts.Rooms);
        Assert.Equal(2, metrics.Counts.Arrivals);
        Assert.Equal(t, metrics.Counts.FirstSeenAt);

        // Only the room there is a stored row for. The other one was never in the group's list,
        // so Modbot has presence facts about it and no room of its own to link to.
        var room = Assert.Single(metrics.RecentRooms);
        Assert.Equal("The Black Cat", room.WorldName);
    }

    [Fact]
    public async Task WithoutViewProfile_Is403()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var response = await host.GetAsync("/api/vrchat-users/metrics?id=usr_a", cookie, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
