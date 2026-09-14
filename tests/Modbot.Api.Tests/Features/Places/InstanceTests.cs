using System.Net;
using Modbot.Api.Features.Places;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Places;

[Collection(nameof(PostgresCollection))]
public class InstanceTests
{
    private readonly PostgresFixture _db;

    public InstanceTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task AnUnknownRoom_Is404()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(
            ModbotPermissions.ViewAnalytics | ModbotPermissions.ViewAuditLog, ct);

        var response = await host.GetAsync($"/api/instances/{Guid.NewGuid()}", cookie, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ARoom_CarriesItsWorld_AndWhoWasSeenInIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-4);

        await PlacesFixtures.WorldAsync(host, "wrld_a", "The Black Cat", t, ct);
        var room = await PlacesFixtures.RoomAsync(host, "wrld_a", "39047", t, t.AddHours(2), t.AddHours(2), ct);
        await PlacesFixtures.PersonAsync(host, "usr_a", "Ada", t, ct);

        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_a", t.AddMinutes(5), "wrld_a", "39047"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_a", t.AddMinutes(35), "wrld_a", "39047"), ct);

        var cookie = await host.SignedInAsync(
            ModbotPermissions.ViewAnalytics | ModbotPermissions.ViewAuditLog, ct);

        var view = await host.GetJsonAsync<InstanceView>($"/api/instances/{room.Id}", cookie, ct);

        Assert.Equal("The Black Cat", view.Room.WorldName);
        Assert.Equal("39047", view.Room.VRChatInstanceId);
        Assert.Equal(7, view.Room.PeakPeople);
        Assert.True(view.CanSeeWhoWasThere);

        var seen = Assert.Single(view.People);
        Assert.Equal("usr_a", seen.UserId);
        Assert.Equal("Ada", seen.DisplayName);
        Assert.Equal(30m, seen.MinutesSeen);

        Assert.Equal(2, view.Log.Count);
    }

    /// <summary>
    /// VRChat hands the same room number out again once a room closes. A room must not show the
    /// people or the facts of the evening before it.
    /// </summary>
    [Fact]
    public async Task ARoomReusingANumber_DoesNotShowTheEarlierEvenings()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var monday = host.Clock.UtcNow.AddDays(-7);
        var tonight = host.Clock.UtcNow.AddHours(-2);

        await PlacesFixtures.WorldAsync(host, "wrld_a", "The Black Cat", monday, ct);

        var earlier = await PlacesFixtures.RoomAsync(
            host, "wrld_a", "39047", monday, monday.AddHours(1), monday.AddHours(1), ct);
        var later = await PlacesFixtures.RoomAsync(
            host, "wrld_a", "39047", tonight, tonight.AddMinutes(30), null, ct);

        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_old", monday.AddMinutes(5), "wrld_a", "39047"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_new", tonight.AddMinutes(5), "wrld_a", "39047"), ct);

        var cookie = await host.SignedInAsync(
            ModbotPermissions.ViewAnalytics | ModbotPermissions.ViewAuditLog, ct);

        var first = await host.GetJsonAsync<InstanceView>($"/api/instances/{earlier.Id}", cookie, ct);
        var second = await host.GetJsonAsync<InstanceView>($"/api/instances/{later.Id}", cookie, ct);

        Assert.Equal("usr_old", Assert.Single(first.People).UserId);
        Assert.Equal("usr_new", Assert.Single(second.People).UserId);
    }

    /// <summary>
    /// The room's own shape is not moderation history; who was in it and what was done to them is
    /// (spec 5.9.4). A caller with ViewAnalytics alone gets the room and neither list.
    /// </summary>
    [Fact]
    public async Task WithoutViewAuditLog_TheRoomAnswers_AndWhoWasThereDoesNot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-4);
        await PlacesFixtures.WorldAsync(host, "wrld_a", "The Black Cat", t, ct);
        var room = await PlacesFixtures.RoomAsync(host, "wrld_a", "39047", t, t.AddHours(2), null, ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_a", t.AddMinutes(5), "wrld_a", "39047"), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var view = await host.GetJsonAsync<InstanceView>($"/api/instances/{room.Id}", cookie, ct);

        Assert.Equal("The Black Cat", view.Room.WorldName);
        Assert.False(view.CanSeeWhoWasThere);
        Assert.Empty(view.People);
        Assert.Empty(view.Log);
    }

    [Fact]
    public async Task WithoutViewAnalytics_Is403()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var response = await host.GetAsync($"/api/instances/{Guid.NewGuid()}", cookie, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
