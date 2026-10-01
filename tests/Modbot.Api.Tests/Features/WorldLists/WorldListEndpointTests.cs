using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.WorldLists;

/// <summary>
/// World lists design: lists under the calendar's permissions, an event's date picked at once when it
/// is scheduled, Pick again before the date opens, and Next game matched to the people in the event's
/// instance -- or to nobody in particular when that count is unknown.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class WorldListEndpointTests(PostgresFixture db)
{
    private const string GroupId = "grp_world_lists";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const ModbotPermissions Manager = ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar;

    private async Task<ApiTestHost> StartAsync()
    {
        await using (var context = db.NewContext())
        {
            await context.CalendarEvents.ExecuteDeleteAsync(Ct);
            await context.WorldLists.ExecuteDeleteAsync(Ct);
            await context.VRChatInstances.Where(i => i.GroupId == GroupId).ExecuteDeleteAsync(Ct);
        }

        return await ApiTestHost.StartAsync(db);
    }

    private static object ListBody(string name, params (string Id, int? Min, int? Max)[] worlds) => new
    {
        name,
        worlds = worlds.Select(w => new { worldId = w.Id, minPlayers = w.Min, maxPlayers = w.Max }).ToArray(),
    };

    private static async Task<Guid> CreateListAsync(ApiTestHost host, string cookie, object body)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/world-lists", body, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("id").GetGuid();
    }

    /// <summary>An event that picks from <paramref name="listId"/>, starting <paramref name="startsIn"/> from now.</summary>
    private static object EventBody(ApiTestHost host, Guid listId, TimeSpan startsIn, int openMinutesBefore = 10)
    {
        var start = host.Clock.UtcNow + startsIn;

        return new
        {
            title = "Game night",
            description = "Games",
            startsAt = start.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            endsAt = start.AddHours(2).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            timeZone = "UTC",
            repeat = "weekly",
            repeatDays = Array.Empty<string>(),
            worldId = (string?)null,
            worldListId = listId,
            accessType = "members",
            region = "us",
            category = "gaming",
            languages = Array.Empty<string>(),
            platforms = Array.Empty<string>(),
            tags = Array.Empty<string>(),
            visibility = "group",
            notifyMembers = false,
            publishToVRChat = false,
            publishToDiscord = false,
            postToChannel = false,
            autoOpen = true,
            openMinutesBefore,
            draft = false,
        };
    }

    private static async Task<JsonElement> CreateEventAsync(ApiTestHost host, string cookie, object body)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", body, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.BodyOf(response, Ct);
    }

    /// <summary>The managed group's instance of the event's world, open now, with this many people in it.</summary>
    private async Task AddInstanceAsync(ApiTestHost host, string worldId, int? people)
    {
        await using var context = db.NewContext();

        var settings = await context.GetSettingsAsync(Ct);
        settings.ManagedGroupId = GroupId;

        var now = host.Clock.UtcNow;
        context.VRChatInstances.Add(new VRChatInstance
        {
            Id = Guid.CreateVersion7(),
            Location = $"{worldId}:12345~group({GroupId})",
            WorldId = worldId,
            VRChatInstanceId = "12345",
            GroupId = GroupId,
            OpenedAt = now,
            LastSeenAt = now,
            HeadCount = people,
            SeenInGroupList = true,
        });

        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task SeeingListsNeedsSeeCalendar_AndMakingOneNeedsManageCalendar()
    {
        await using var host = await StartAsync();
        var (_, nobody) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, "/api/world-lists", null, nobody, Ct)).StatusCode);

        var seen = await host.SendJsonAsync(HttpMethod.Get, "/api/world-lists", null, viewer, Ct);
        Assert.Equal(HttpStatusCode.OK, seen.StatusCode);
        Assert.False((await ApiTestHost.BodyOf(seen, Ct)).GetProperty("canManage").GetBoolean());

        var body = ListBody("Game night", ("wrld_prop", 4, 12));
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Post, "/api/world-lists", body, viewer, Ct)).StatusCode);

        var id = await CreateListAsync(host, manager, body);
        Assert.Single(await host.FactsAsync(FactType.WorldListCreated, id.ToString(), Ct));

        var lists = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/world-lists", null, viewer, Ct), Ct);
        var world = Assert.Single(Assert.Single(lists.GetProperty("lists").EnumerateArray()).GetProperty("worlds").EnumerateArray());
        Assert.Equal(4, world.GetProperty("minPlayers").GetInt32());
        Assert.Equal(12, world.GetProperty("maxPlayers").GetInt32());
    }

    [Fact]
    public async Task TheMostPlayersCannotBeFewerThanTheFewest()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/world-lists", ListBody("Games", ("wrld_a", 8, 4)), manager, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SchedulingAnEventPicksItsFirstDate_AndAListInUseCannotBeDeleted()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        string[] worlds = ["wrld_a", "wrld_b", "wrld_c"];
        var listId = await CreateListAsync(host, manager, ListBody("Games", [.. worlds.Select(w => (w, (int?)null, (int?)null))]));

        var created = await CreateEventAsync(host, manager, EventBody(host, listId, TimeSpan.FromDays(2)));
        var eventId = created.GetProperty("id").GetGuid();

        Assert.Contains(created.GetProperty("worldId").GetString(), worlds);
        Assert.Equal(listId, created.GetProperty("worldListId").GetGuid());

        // Modbot picked it, not the person who saved.
        var fact = Assert.Single(await host.FactsAsync(FactType.CalendarWorldPicked, eventId.ToString(), Ct));
        Assert.Null(fact.ActorId);

        var refused = await host.SendJsonAsync(HttpMethod.Delete, $"/api/world-lists/{listId}", null, manager, Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task PickAgainNeedsManageCalendar_AndTakesAnotherWorldForTheDate()
    {
        await using var host = await StartAsync();
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var listId = await CreateListAsync(host, manager, ListBody("Games", ("wrld_a", null, null), ("wrld_b", null, null)));

        var created = await CreateEventAsync(host, manager, EventBody(host, listId, TimeSpan.FromDays(2)));
        var eventId = created.GetProperty("id").GetGuid();
        var first = created.GetProperty("worldId").GetString();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{eventId}/pick-again", null, viewer, Ct)).StatusCode);

        var again = await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{eventId}/pick-again", null, manager, Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        var second = (await ApiTestHost.BodyOf(again, Ct)).GetProperty("worldId").GetString();
        Assert.NotEqual(first, second);

        await using var context = db.NewContext();
        var standing = await context.WorldPicks.AsNoTracking()
            .Where(p => p.EventId == eventId && p.Kind == WorldPickKinds.Date && p.PutBackAt == null)
            .ToListAsync(Ct);
        Assert.Equal(second, Assert.Single(standing).WorldId);
    }

    [Fact]
    public async Task NextGameFitsThePeopleInTheEventsInstance()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var listId = await CreateListAsync(
            host, manager, ListBody("Games", ("wrld_small", 2, 4), ("wrld_big", 8, null), ("wrld_any", null, null)));

        // Opens ten minutes early, so it is open as soon as it is scheduled.
        var created = await CreateEventAsync(host, manager, EventBody(host, listId, TimeSpan.FromMinutes(5)));
        var eventId = created.GetProperty("id").GetGuid();
        Assert.Equal("open", created.GetProperty("state").GetString());

        await AddInstanceAsync(host, created.GetProperty("worldId").GetString()!, people: 10);

        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{eventId}/next-game", new { }, manager, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var view = await ApiTestHost.BodyOf(response, Ct);
        Assert.Equal(10, view.GetProperty("people").GetInt32());

        var game = view.GetProperty("game");
        Assert.NotEqual("wrld_small", game.GetProperty("worldId").GetString());
        Assert.NotEqual(created.GetProperty("worldId").GetString(), game.GetProperty("worldId").GetString());

        var games = (await host.FactsAsync(FactType.CalendarWorldPicked, eventId.ToString(), Ct))
            .Select(f => JsonDocument.Parse(f.Data).RootElement)
            .Where(d => d.GetProperty("kind").GetString() == WorldPickKinds.Game)
            .ToList();
        Assert.Equal(10, Assert.Single(games).GetProperty("people").GetInt32());
    }

    [Fact]
    public async Task NoWorldThatFits_IsSaidPlainly_AndRecordsNothing()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var listId = await CreateListAsync(host, manager, ListBody("Big games", ("wrld_big", 8, null)));

        var created = await CreateEventAsync(host, manager, EventBody(host, listId, TimeSpan.FromMinutes(5)));
        var eventId = created.GetProperty("id").GetGuid();
        await AddInstanceAsync(host, "wrld_big", people: 3);

        var view = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{eventId}/next-game", new { }, manager, Ct), Ct);

        Assert.True(view.GetProperty("noneFits").GetBoolean());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("game").ValueKind);

        // Only the date's own pick.
        Assert.Single(await host.FactsAsync(FactType.CalendarWorldPicked, eventId.ToString(), Ct));
    }

    [Fact]
    public async Task WithNoCountOfPeople_ThePlayersAreIgnored()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var listId = await CreateListAsync(host, manager, ListBody("Big games", ("wrld_big", 8, null), ("wrld_huge", 20, null)));

        var created = await CreateEventAsync(host, manager, EventBody(host, listId, TimeSpan.FromMinutes(5)));
        var eventId = created.GetProperty("id").GetGuid();

        // No instance at all: nobody knows how many are there.
        var view = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{eventId}/next-game", new { }, manager, Ct), Ct);

        Assert.Equal(JsonValueKind.Null, view.GetProperty("people").ValueKind);
        Assert.False(view.GetProperty("noneFits").GetBoolean());
        Assert.NotEqual(created.GetProperty("worldId").GetString(), view.GetProperty("game").GetProperty("worldId").GetString());
    }

    [Fact]
    public async Task PickAnother_PutsTheShownWorldBack()
    {
        await using var host = await StartAsync();
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var listId = await CreateListAsync(
            host, manager, ListBody("Games", ("wrld_a", null, null), ("wrld_b", null, null), ("wrld_c", null, null), ("wrld_d", null, null)));

        var created = await CreateEventAsync(host, manager, EventBody(host, listId, TimeSpan.FromMinutes(5)));
        var eventId = created.GetProperty("id").GetGuid();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{eventId}/next-game", new { }, viewer, Ct)).StatusCode);

        var first = (await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{eventId}/next-game", new { }, manager, Ct), Ct))
            .GetProperty("game").GetProperty("worldId").GetString();

        var another = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{eventId}/next-game", new { instead = first }, manager, Ct), Ct);
        var second = another.GetProperty("game").GetProperty("worldId").GetString();
        Assert.NotEqual(first, second);

        // The viewer sees the world picked last.
        var seen = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, $"/api/calendar/events/{eventId}/next-game", null, viewer, Ct), Ct);
        Assert.Equal(second, seen.GetProperty("game").GetProperty("worldId").GetString());
        Assert.False(seen.GetProperty("canPick").GetBoolean());

        await using var context = db.NewContext();
        var shuffle = await context.WorldListShuffles.AsNoTracking().SingleAsync(s => s.ListId == listId && s.EventId == eventId, Ct);
        Assert.DoesNotContain(first!, shuffle.Played);
        Assert.Contains(second!, shuffle.Played);
    }

    [Fact]
    public async Task AnEventWithOneWorld_HasNoNextGame()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        var body = new
        {
            title = "Movie night",
            description = "Films",
            startsAt = host.Clock.UtcNow.AddMinutes(5).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            endsAt = host.Clock.UtcNow.AddHours(2).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            timeZone = "UTC",
            worldId = "wrld_cinema",
            publishToVRChat = false,
            publishToDiscord = false,
            postToChannel = false,
            autoOpen = false,
            draft = false,
        };

        var eventId = (await CreateEventAsync(host, manager, body)).GetProperty("id").GetGuid();

        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{eventId}/next-game", new { }, manager, Ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
