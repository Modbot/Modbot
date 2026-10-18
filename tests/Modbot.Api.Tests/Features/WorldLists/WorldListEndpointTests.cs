using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat;
using VRChat.API.Model;

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
            await context.CalendarEvents.IgnoreAutoIncludes().ExecuteDeleteAsync(Ct);
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

    /// <summary>Worlds Modbot has read from VRChat, so a list can take them in.</summary>
    private async Task KnowAsync(DateTimeOffset now, params string[] worldIds)
    {
        await using var context = db.NewContext();

        foreach (var id in worldIds)
        {
            if (await context.VRChatWorlds.AnyAsync(w => w.WorldId == id, Ct))
                continue;

            context.VRChatWorlds.Add(new VRChatWorld
            {
                WorldId = id,
                Name = id,
                FirstSeenAt = now,
                LastSeenAt = now,
                LastRefreshedAt = now,
            });
        }

        await context.SaveChangesAsync(Ct);
    }

    /// <summary>Makes a list; the worlds in <paramref name="body"/> are first made known, as a search or Add would.</summary>
    private async Task<Guid> CreateListAsync(ApiTestHost host, string cookie, object body)
    {
        var ids = JsonSerializer.SerializeToElement(body).GetProperty("worlds").EnumerateArray()
            .Select(w => w.GetProperty("worldId").GetString()!)
            .ToArray();
        await KnowAsync(host.Clock.UtcNow, ids);

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
        await KnowAsync(host.Clock.UtcNow, "wrld_prop");
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Post, "/api/world-lists", body, viewer, Ct)).StatusCode);

        var id = await CreateListAsync(host, manager, body);
        Assert.Single(await host.FactsAsync(FactType.WorldListCreated, id.ToString(), Ct));

        var lists = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/world-lists", null, viewer, Ct), Ct);
        var world = Assert.Single(Assert.Single(lists.GetProperty("lists").EnumerateArray()).GetProperty("worlds").EnumerateArray());
        Assert.Equal(4, world.GetProperty("minPlayers").GetInt32());
        Assert.Equal(12, world.GetProperty("maxPlayers").GetInt32());
    }

    [Fact]
    public async Task AWorldInAList_CarriesItsBuilds_SoTheEventFormCanTickThem()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        await KnowAsync(host.Clock.UtcNow, "wrld_builds", "wrld_unread");
        await using (var context = db.NewContext())
        {
            await context.VRChatWorlds.Where(w => w.WorldId == "wrld_builds")
                .ExecuteUpdateAsync(
                    s => s.SetProperty(w => w.Platforms, "[\"android\",\"standalonewindows\"]")
                        .SetProperty(w => w.ImageUrl, "https://api.vrchat.cloud/api/1/file/file_full/1/file"),
                    Ct);
        }

        await CreateListAsync(host, manager, ListBody("Builds", ("wrld_builds", null, null), ("wrld_unread", null, null)));

        var lists = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/world-lists", null, manager, Ct), Ct);
        var worlds = Assert.Single(lists.GetProperty("lists").EnumerateArray()).GetProperty("worlds").EnumerateArray().ToList();

        var read = Assert.Single(worlds, w => w.GetProperty("worldId").GetString() == "wrld_builds");
        Assert.Equal(["android", "standalonewindows"], read.GetProperty("platforms").EnumerateArray().Select(p => p.GetString()).ToArray());

        Assert.Equal("https://api.vrchat.cloud/api/1/file/file_full/1/file", read.GetProperty("imageUrl").GetString());

        var unread = Assert.Single(worlds, w => w.GetProperty("worldId").GetString() == "wrld_unread");
        Assert.Equal(JsonValueKind.Null, unread.GetProperty("platforms").ValueKind);
    }

    [Fact]
    public async Task TheMostPlayersCannotBeFewerThanTheFewest()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/world-lists", ListBody("Games", ("wrld_a", 8, 4)), manager, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Task<HttpResponseMessage> FindAsync(ApiTestHost host, string cookie, string text) =>
        host.SendJsonAsync(HttpMethod.Post, "/api/world-lists/worlds/find", new { text }, cookie, Ct);

    private async Task<VRChatWorld?> RowOfAsync(string worldId)
    {
        await using var context = db.NewContext();
        return await context.VRChatWorlds.AsNoTracking().FirstOrDefaultAsync(w => w.WorldId == worldId, Ct);
    }

    [Fact]
    public async Task ATypedNameVRChatRefuses_IsTurnedDown_AndStoresNothing()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        host.VRChat.Returns("GetWorld", VRChatResult<World>.Failure(403, "Forbidden"));

        var response = await FindAsync(host, manager, "Just B Club 4.0");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            "No world by that name or id. Paste its link.",
            (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
        Assert.Null(await RowOfAsync("Just B Club 4.0"));
    }

    [Fact]
    public async Task ALinkVRChatRefuses_ShowsVRChatsWords_AndStoresNothing()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        host.VRChat.Returns("GetWorld", VRChatResult<World>.Failure(404, "World not found"));

        var response = await FindAsync(host, manager, "https://vrchat.com/home/world/wrld_gone_for_good");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("World not found", (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
        Assert.Null(await RowOfAsync("wrld_gone_for_good"));
    }

    [Theory]
    [InlineData(429, VRChatFailureKind.RateLimited)]
    [InlineData(0, VRChatFailureKind.SignInWaiting)]
    [InlineData(0, VRChatFailureKind.Network)]
    [InlineData(503, VRChatFailureKind.Other)]
    public async Task WhenVRChatCannotBeAsked_TheWorldIsKeptByItsId(int status, VRChatFailureKind kind)
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var id = $"wrld_busy_{status}_{kind}".ToLowerInvariant();
        host.VRChat.Returns("GetWorld", VRChatResult<World>.Failure(status, "Not now", kind: kind));

        var response = await FindAsync(host, manager, id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = await RowOfAsync(id);
        Assert.NotNull(row);
        Assert.Null(row.LastRefreshedAt);
    }

    [Fact]
    public async Task AWorldVRChatFinds_IsAdded_WithItsName()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        host.VRChat.Returns("GetWorld", new World { Id = "wrld_real_one", Name = "Real One" });

        var response = await FindAsync(host, manager, "wrld_real_one");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Real One", (await ApiTestHost.BodyOf(response, Ct)).GetProperty("name").GetString());
        Assert.NotNull((await RowOfAsync("wrld_real_one"))?.LastRefreshedAt);
    }

    [Fact]
    public async Task AWorldVRChatRefusedBefore_IsAskedAboutAgain()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        await using (var context = db.NewContext())
        {
            context.VRChatWorlds.Add(new VRChatWorld
            {
                WorldId = "wrld_refused_once",
                FirstSeenAt = host.Clock.UtcNow,
                LastSeenAt = host.Clock.UtcNow,
                RefreshError = "VRChat returned 403",
            });
            await context.SaveChangesAsync(Ct);
        }

        host.VRChat.Returns("GetWorld", new World { Id = "wrld_refused_once", Name = "Back Again" });

        var response = await FindAsync(host, manager, "wrld_refused_once");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(host.VRChat.Calls);
        Assert.Null((await RowOfAsync("wrld_refused_once"))?.RefreshError);
    }

    private static Task<HttpResponseMessage> SearchVRChatAsync(ApiTestHost host, string cookie, string text) =>
        host.SendJsonAsync(HttpMethod.Post, "/api/world-lists/worlds/search-vrchat", new { text }, cookie, Ct);

    [Fact]
    public async Task SearchingVRChat_NeedsManageCalendar_AndAsksVRChatNothingWithout()
    {
        await using var host = await StartAsync();
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);

        var response = await SearchVRChatAsync(host, viewer, "Prop Hunt");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(host.VRChat.Calls);
    }

    [Fact]
    public async Task SearchingVRChat_KeepsEachWorldFound_SoAListTakesItIn_WithoutMarkingItRead()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        host.VRChat.Returns("SearchWorlds", new List<LimitedWorld>
        {
            new() { Id = "wrld_found_one", Name = "Found One", AuthorName = "Maker", ThumbnailImageUrl = "https://example.test/one.png" },
            new() { Id = "wrld_found_two", Name = "Found Two", AuthorName = "Other" },
        });

        var response = await SearchVRChatAsync(host, manager, "Found");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var hits = (await ApiTestHost.BodyOf(response, Ct)).EnumerateArray().ToList();
        Assert.Equal(2, hits.Count);
        Assert.Equal("Found One", hits[0].GetProperty("name").GetString());
        Assert.Equal("Maker", hits[0].GetProperty("authorName").GetString());

        var call = Assert.Single(host.VRChat.Calls);
        Assert.Equal(VRChatEndpointClass.WorldsSearch, call.Endpoint.Class);
        Assert.Equal(VRChatCallPriority.Interactive, call.Priority);

        var row = await RowOfAsync("wrld_found_one");
        Assert.NotNull(row);
        Assert.Equal("Found One", row.Name);
        Assert.Equal("Maker", row.AuthorName);
        Assert.Equal("https://example.test/one.png", row.ThumbnailImageUrl);
        Assert.Null(row.LastRefreshedAt);
        Assert.Null(row.RefreshError);

        var save = await host.SendJsonAsync(
            HttpMethod.Post, "/api/world-lists", ListBody("Games", ("wrld_found_one", null, null)), manager, Ct);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
    }

    [Fact]
    public async Task SearchingVRChat_DoesNotOverwriteAWorldModbotHasRead_ButClearsAnEarlierRefusal()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        await using (var context = db.NewContext())
        {
            context.VRChatWorlds.AddRange(
                new VRChatWorld
                {
                    WorldId = "wrld_read_before",
                    Name = "Read Before",
                    FirstSeenAt = host.Clock.UtcNow,
                    LastSeenAt = host.Clock.UtcNow,
                    LastRefreshedAt = host.Clock.UtcNow,
                },
                new VRChatWorld
                {
                    WorldId = "wrld_refused_before",
                    FirstSeenAt = host.Clock.UtcNow,
                    LastSeenAt = host.Clock.UtcNow,
                    RefreshError = "VRChat returned 403",
                });
            await context.SaveChangesAsync(Ct);
        }

        host.VRChat.Returns("SearchWorlds", new List<LimitedWorld>
        {
            new() { Id = "wrld_read_before", Name = "Renamed In Search" },
            new() { Id = "wrld_refused_before", Name = "Listed Now" },
        });

        Assert.Equal(HttpStatusCode.OK, (await SearchVRChatAsync(host, manager, "Before")).StatusCode);

        var read = await RowOfAsync("wrld_read_before");
        Assert.Equal("Read Before", read?.Name);
        Assert.NotNull(read?.LastRefreshedAt);

        var refused = await RowOfAsync("wrld_refused_before");
        Assert.Equal("Listed Now", refused?.Name);
        Assert.Null(refused?.RefreshError);
        Assert.Null(refused?.LastRefreshedAt);
    }

    [Theory]
    [InlineData(429, VRChatFailureKind.RateLimited)]
    [InlineData(0, VRChatFailureKind.RateLimited)]
    [InlineData(0, VRChatFailureKind.SignInWaiting)]
    [InlineData(0, VRChatFailureKind.NotConfigured)]
    [InlineData(0, VRChatFailureKind.Network)]
    public async Task SearchingVRChat_WhenItCannotBeAsked_SaysSoAndStoresNothing(int status, VRChatFailureKind kind)
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        host.VRChat.Returns("SearchWorlds", VRChatResult<List<LimitedWorld>>.Failure(status, "Cold stopped", kind: kind));

        var response = await SearchVRChatAsync(host, manager, "Nothing stored");

        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal("VRChat can't be asked right now.", (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());

        await using var context = db.NewContext();
        Assert.Empty(await context.VRChatWorlds.Where(w => w.Name == "Nothing stored").ToListAsync(Ct));
    }

    [Fact]
    public async Task SearchingVRChat_TooSoonAfterTheLast_ShowsTheWait_AndSendsNothing()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        host.VRChat.Returns("SearchWorlds", new List<LimitedWorld>());

        Assert.Equal(HttpStatusCode.OK, (await SearchVRChatAsync(host, manager, "First")).StatusCode);

        host.Clock.Advance(TimeSpan.FromSeconds(4));
        var tooSoon = await SearchVRChatAsync(host, manager, "Second");

        Assert.Equal(HttpStatusCode.TooManyRequests, tooSoon.StatusCode);
        Assert.Equal(
            "Wait 6 seconds, then search again.",
            (await ApiTestHost.BodyOf(tooSoon, Ct)).GetProperty("error").GetString());
        Assert.Single(host.VRChat.Calls);

        host.Clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(HttpStatusCode.OK, (await SearchVRChatAsync(host, manager, "Third")).StatusCode);
        Assert.Equal(2, host.VRChat.Calls.Count);
    }

    [Fact]
    public async Task SearchingVRChat_WithNothingTyped_IsTurnedDown_AndSendsNothing()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        var response = await SearchVRChatAsync(host, manager, "   ");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.VRChat.Calls);
    }

    [Fact]
    public async Task SavingAList_RefusesANewWorldModbotDoesNotKnow_AndMakesNoPlaceholder()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/world-lists", ListBody("Games", ("wrld_made_up_for_a_test", null, null)), manager, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("wrld_made_up_for_a_test", (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
        Assert.Null(await RowOfAsync("wrld_made_up_for_a_test"));

        await using var read = db.NewContext();
        Assert.Empty(await read.WorldLists.ToListAsync(Ct));
    }

    [Fact]
    public async Task SavingAList_RefusesANewWorldVRChatRefused_ButKeepsOnesAlreadyOnTheList()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var listId = await CreateListAsync(host, manager, ListBody("Games", ("wrld_stays_a", null, null), ("wrld_stays_b", 2, 6)));

        // One of them is later refused by VRChat, and another world was refused from the start.
        await using (var context = db.NewContext())
        {
            await context.VRChatWorlds.Where(w => w.WorldId == "wrld_stays_b")
                .ExecuteUpdateAsync(u => u.SetProperty(w => w.LastRefreshedAt, (DateTimeOffset?)null).SetProperty(w => w.RefreshError, "VRChat returned 403"), Ct);
            context.VRChatWorlds.Add(new VRChatWorld
            {
                WorldId = "wrld_never_allowed",
                FirstSeenAt = host.Clock.UtcNow,
                LastSeenAt = host.Clock.UtcNow,
                RefreshError = "VRChat returned 404",
            });
            await context.SaveChangesAsync(Ct);
        }

        var unchanged = await host.SendJsonAsync(
            HttpMethod.Put, $"/api/world-lists/{listId}", ListBody("Games", ("wrld_stays_a", null, null), ("wrld_stays_b", 2, 6)), manager, Ct);
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);

        var renamed = await host.SendJsonAsync(
            HttpMethod.Put, $"/api/world-lists/{listId}", ListBody("Party games", ("wrld_stays_a", null, null), ("wrld_stays_b", 2, 8)), manager, Ct);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var refused = await host.SendJsonAsync(
            HttpMethod.Put,
            $"/api/world-lists/{listId}",
            ListBody("Party games", ("wrld_stays_a", null, null), ("wrld_stays_b", 2, 8), ("wrld_never_allowed", null, null)),
            manager,
            Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("wrld_never_allowed", (await ApiTestHost.BodyOf(refused, Ct)).GetProperty("error").GetString());

        await using var read = db.NewContext();
        Assert.Equal(2, await read.WorldListItems.CountAsync(i => i.ListId == listId, Ct));
    }

    [Fact]
    public async Task AListWithNoWorlds_IsRefused_WhenMadeAndWhenChanged_AndAnEmptyOneAlreadyThereStaysUntilEdited()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        var made = await host.SendJsonAsync(HttpMethod.Post, "/api/world-lists", ListBody("Nothing"), manager, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, made.StatusCode);
        Assert.Equal("A list needs at least one world.", (await ApiTestHost.BodyOf(made, Ct)).GetProperty("error").GetString());

        var listId = await CreateListAsync(host, manager, ListBody("Games", ("wrld_only_one", null, null)));

        var emptied = await host.SendJsonAsync(HttpMethod.Put, $"/api/world-lists/{listId}", ListBody("Games"), manager, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, emptied.StatusCode);

        await using (var read = db.NewContext())
            Assert.Equal(1, await read.WorldListItems.CountAsync(i => i.ListId == listId, Ct));

        // A list that was empty before the rule stays as it is, and shows with no worlds.
        var oldId = Guid.CreateVersion7();
        await using (var write = db.NewContext())
        {
            write.WorldLists.Add(new WorldList { Id = oldId, Name = "Old empty", CreatedAt = host.Clock.UtcNow, UpdatedAt = host.Clock.UtcNow });
            await write.SaveChangesAsync(Ct);
        }

        var lists = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/world-lists", null, manager, Ct), Ct);
        var old = lists.GetProperty("lists").EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == oldId);
        Assert.Empty(old.GetProperty("worlds").EnumerateArray());

        // Edited, it needs a world like any other.
        var renamed = await host.SendJsonAsync(HttpMethod.Put, $"/api/world-lists/{oldId}", ListBody("Old empty, renamed"), manager, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, renamed.StatusCode);
    }

    /// <summary>The Seen list as the migration makes it; the tests above empty the table first.</summary>
    private async Task EnsureSeenListAsync(DateTimeOffset now)
    {
        await using var context = db.NewContext();
        if (await context.WorldLists.AnyAsync(l => l.Id == WorldList.SeenId, Ct))
            return;

        context.WorldLists.Add(new WorldList { Id = WorldList.SeenId, Name = WorldList.SeenName, CreatedAt = now, UpdatedAt = now });
        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task TheSeenList_ComesFirst_HoldsEveryWorldButJunk_AndCannotBeChangedOrDeleted()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var now = host.Clock.UtcNow;

        await EnsureSeenListAsync(now);
        await KnowAsync(now, "wrld_seen_read");
        await CreateListAsync(host, manager, ListBody("A list of mine", ("wrld_seen_read", null, null)));

        await using (var context = db.NewContext())
        {
            // Named by a search, not read yet: not junk. Refused by VRChat and an id nothing confirmed: junk.
            context.VRChatWorlds.AddRange(
                new VRChatWorld { WorldId = "wrld_seen_searched", Name = "Searched", FirstSeenAt = now, LastSeenAt = now },
                new VRChatWorld { WorldId = "wrld_seen_refused", FirstSeenAt = now, LastSeenAt = now, RefreshError = "VRChat returned 404" },
                new VRChatWorld { WorldId = "wrld_seen_unnamed", FirstSeenAt = now, LastSeenAt = now });
            await context.SaveChangesAsync(Ct);
        }

        var lists = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/world-lists", null, manager, Ct), Ct);
        var first = lists.GetProperty("lists").EnumerateArray().First();

        Assert.True(first.GetProperty("seen").GetBoolean());
        Assert.Equal(WorldList.SeenId, first.GetProperty("id").GetGuid());

        var worldIds = first.GetProperty("worlds").EnumerateArray().Select(w => w.GetProperty("worldId").GetString()).ToArray();
        Assert.Contains("wrld_seen_read", worldIds);
        Assert.Contains("wrld_seen_searched", worldIds);
        Assert.DoesNotContain("wrld_seen_refused", worldIds);
        Assert.DoesNotContain("wrld_seen_unnamed", worldIds);

        // Any number of players fits a seen world.
        Assert.All(first.GetProperty("worlds").EnumerateArray(), w => Assert.Equal(System.Text.Json.JsonValueKind.Null, w.GetProperty("minPlayers").ValueKind));

        var changed = await host.SendJsonAsync(
            HttpMethod.Put, $"/api/world-lists/{WorldList.SeenId}", ListBody("Seen", ("wrld_seen_read", null, null)), manager, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);

        var deleted = await host.SendJsonAsync(HttpMethod.Delete, $"/api/world-lists/{WorldList.SeenId}", null, manager, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, deleted.StatusCode);
    }

    [Fact]
    public async Task AListCannotBeNamedSeen_SoTheMenusShowOnlyTheAutomaticOne()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        await KnowAsync(host.Clock.UtcNow, "wrld_named_seen");

        foreach (var name in new[] { "Seen", " seen ", "SEEN" })
        {
            var response = await host.SendJsonAsync(
                HttpMethod.Post, "/api/world-lists", ListBody(name, ("wrld_named_seen", null, null)), manager, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task TheSeenList_CanBeAskedForWithoutItsWorlds_ForAPicker()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var now = host.Clock.UtcNow;

        await EnsureSeenListAsync(now);
        await KnowAsync(now, "wrld_picker_read");

        var lists = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, "/api/world-lists?seenWorlds=false", null, manager, Ct), Ct);
        var first = lists.GetProperty("lists").EnumerateArray().First();

        Assert.True(first.GetProperty("seen").GetBoolean());
        Assert.Empty(first.GetProperty("worlds").EnumerateArray());
    }

    [Fact]
    public async Task AnEventPickingFromTheSeenList_GetsAWorldThatIsNotJunk()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var now = host.Clock.UtcNow;

        await EnsureSeenListAsync(now);
        await KnowAsync(now, "wrld_seen_only");

        await using (var context = db.NewContext())
        {
            context.VRChatWorlds.AddRange(
                new VRChatWorld { WorldId = "wrld_seen_refused", FirstSeenAt = now, LastSeenAt = now, RefreshError = "VRChat returned 404" },
                new VRChatWorld { WorldId = "wrld_seen_unnamed", FirstSeenAt = now, LastSeenAt = now });
            await context.SaveChangesAsync(Ct);
        }

        // Every other world Modbot knows from earlier tests is as good as this one, so the pick is one of
        // the not-junk worlds, never a junk one.
        var created = await CreateEventAsync(host, manager, EventBody(host, WorldList.SeenId, TimeSpan.FromDays(2)));

        var picked = created.GetProperty("worldId").GetString();
        Assert.NotNull(picked);
        Assert.NotEqual("wrld_seen_refused", picked);
        Assert.NotEqual("wrld_seen_unnamed", picked);

        await using var read = db.NewContext();
        var row = await read.VRChatWorlds.AsNoTracking().SingleAsync(w => w.WorldId == picked, Ct);
        Assert.True(row.LastRefreshedAt is not null || (row.RefreshError is null && row.Name is not null));
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
