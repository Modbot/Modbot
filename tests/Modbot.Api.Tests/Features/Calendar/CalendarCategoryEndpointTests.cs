using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// The categories admins make in Modbot for events: under the calendar's permissions, a name used
/// once whatever its letter case, every change a fact, any number on an event, kept when a save
/// names none, cleared by an empty list, and let go of when the category is deleted.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarCategoryEndpointTests(PostgresFixture db)
{
    private const string Path = "/api/calendar/categories";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const ModbotPermissions Manager = ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar;

    private async Task<ApiTestHost> StartAsync()
    {
        await using (var context = db.NewContext())
        {
            await context.CalendarEvents.IgnoreAutoIncludes().ExecuteDeleteAsync(Ct);
            await context.CalendarCategories.ExecuteDeleteAsync(Ct);
        }

        return await ApiTestHost.StartAsync(db);
    }

    private static async Task<Guid> CreateCategoryAsync(ApiTestHost host, string cookie, string name)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, Path, new { name }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("id").GetGuid();
    }

    private static Dictionary<string, object?> EventBody(ApiTestHost host, Guid[]? categoryIds)
    {
        var start = host.Clock.UtcNow.AddDays(2);

        var body = new Dictionary<string, object?>
        {
            ["title"] = "Karaoke night",
            ["description"] = "Bring a song",
            ["startsAt"] = start.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            ["endsAt"] = start.AddHours(2).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            ["timeZone"] = "UTC",
            ["repeat"] = "none",
            ["draft"] = true,
        };

        // Left out when null: a save that names none keeps what the event has.
        if (categoryIds is not null)
            body["categoryIds"] = categoryIds;

        return body;
    }

    private static async Task<JsonElement> SaveEventAsync(ApiTestHost host, string cookie, HttpMethod method, string path, Guid[]? categoryIds)
    {
        var response = await host.SendJsonAsync(method, path, EventBody(host, categoryIds), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.BodyOf(response, Ct);
    }

    private static string[] NamesOf(JsonElement view) =>
        [.. view.GetProperty("categories").EnumerateArray().Select(c => c.GetProperty("name").GetString()!)];

    [Fact]
    public async Task SeeingCategoriesNeedsSeeCalendar_AndChangingThemNeedsManageCalendar()
    {
        await using var host = await StartAsync();
        var (_, nobody) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Path, null, nobody, Ct)).StatusCode);

        var seen = await host.SendJsonAsync(HttpMethod.Get, Path, null, viewer, Ct);
        Assert.Equal(HttpStatusCode.OK, seen.StatusCode);
        Assert.False((await ApiTestHost.BodyOf(seen, Ct)).GetProperty("canManage").GetBoolean());

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Post, Path, new { name = "Karaoke" }, viewer, Ct)).StatusCode);

        var id = await CreateCategoryAsync(host, manager, "Karaoke");

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Put, $"{Path}/{id}", new { name = "Open mic" }, viewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Delete, $"{Path}/{id}", null, viewer, Ct)).StatusCode);
    }

    [Fact]
    public async Task ANameIsKeptOnce_WhateverItsLetterCase()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        await CreateCategoryAsync(host, manager, "Karaoke");
        var other = await CreateCategoryAsync(host, manager, "Open mic");

        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendJsonAsync(HttpMethod.Post, Path, new { name = "KARAOKE" }, manager, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendJsonAsync(HttpMethod.Post, Path, new { name = "   " }, manager, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendJsonAsync(HttpMethod.Put, $"{Path}/{other}", new { name = "karaoke" }, manager, Ct)).StatusCode);
    }

    [Fact]
    public async Task MakingRenamingAndDeletingEachLeaveAFact()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        var id = await CreateCategoryAsync(host, manager, "Karaoke");
        Assert.Single(await host.FactsAsync(FactType.CalendarCategoryCreated, id.ToString(), Ct));

        var renamed = await host.SendJsonAsync(HttpMethod.Put, $"{Path}/{id}", new { name = "Open mic" }, manager, Ct);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Open mic", (await ApiTestHost.BodyOf(renamed, Ct)).GetProperty("name").GetString());
        Assert.Single(await host.FactsAsync(FactType.CalendarCategoryChanged, id.ToString(), Ct));

        var deleted = await host.SendJsonAsync(HttpMethod.Delete, $"{Path}/{id}", null, manager, Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Single(await host.FactsAsync(FactType.CalendarCategoryDeleted, id.ToString(), Ct));
    }

    [Fact]
    public async Task AnEventCarriesAnyNumberOfCategories_AndKeepsThemWhenASaveNamesNone()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var karaoke = await CreateCategoryAsync(host, manager, "Karaoke");
        var movies = await CreateCategoryAsync(host, manager, "Movie night");

        var created = await SaveEventAsync(host, manager, HttpMethod.Post, "/api/calendar/events", [karaoke, movies]);
        var eventId = created.GetProperty("id").GetGuid();
        Assert.Equal(["Karaoke", "Movie night"], NamesOf(created));

        var kept = await SaveEventAsync(host, manager, HttpMethod.Put, $"/api/calendar/events/{eventId}", null);
        Assert.Equal(["Karaoke", "Movie night"], NamesOf(kept));

        var one = await SaveEventAsync(host, manager, HttpMethod.Put, $"/api/calendar/events/{eventId}", [movies]);
        Assert.Equal(["Movie night"], NamesOf(one));
    }

    [Fact]
    public async Task AnEmptyListTakesEveryCategoryOffTheEvent()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var karaoke = await CreateCategoryAsync(host, manager, "Karaoke");

        var created = await SaveEventAsync(host, manager, HttpMethod.Post, "/api/calendar/events", [karaoke]);
        var eventId = created.GetProperty("id").GetGuid();
        Assert.Single(created.GetProperty("categories").EnumerateArray());

        var cleared = await SaveEventAsync(host, manager, HttpMethod.Put, $"/api/calendar/events/{eventId}", []);
        Assert.Empty(cleared.GetProperty("categories").EnumerateArray());
    }

    [Fact]
    public async Task ACategoryThatIsGoneCannotBePicked()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/calendar/events", EventBody(host, [Guid.CreateVersion7()]), manager, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeletingACategoryTakesItOffTheEventsThatCarriedIt()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var karaoke = await CreateCategoryAsync(host, manager, "Karaoke");
        var movies = await CreateCategoryAsync(host, manager, "Movie night");

        var created = await SaveEventAsync(host, manager, HttpMethod.Post, "/api/calendar/events", [karaoke, movies]);
        var eventId = created.GetProperty("id").GetGuid();

        var listed = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, manager, Ct), Ct);
        Assert.All(listed.GetProperty("categories").EnumerateArray(), c => Assert.Equal(1, c.GetProperty("usedBy").GetInt32()));

        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(HttpMethod.Delete, $"{Path}/{karaoke}", null, manager, Ct)).StatusCode);

        var after = await SaveEventAsync(host, manager, HttpMethod.Put, $"/api/calendar/events/{eventId}", null);
        Assert.Equal(["Movie night"], NamesOf(after));

        await using var context = db.NewContext();
        Assert.True(await context.CalendarEvents.AnyAsync(e => e.Id == eventId, Ct));
    }

    [Fact]
    public async Task ACategoryFollowsItsNameForItsBlueskyTag_UntilOneIsSet()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        var made = await host.SendJsonAsync(HttpMethod.Post, Path, new { name = "Dance night" }, manager, Ct);
        var view = await ApiTestHost.BodyOf(made, Ct);
        var id = view.GetProperty("id").GetGuid();
        Assert.Equal("DanceNight", view.GetProperty("blueskyTag").GetString());
        Assert.False(view.GetProperty("blueskyTagIsOwn").GetBoolean());

        // A rename carries the name's tag along.
        var renamed = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Put, $"{Path}/{id}", new { name = "Disco night" }, manager, Ct), Ct);
        Assert.Equal("DiscoNight", renamed.GetProperty("blueskyTag").GetString());

        // Its own tag, typed with a # and a space, is cleaned and kept through a rename that names none.
        var own = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Put, $"{Path}/{id}", new { name = "Disco night", blueskyTag = "#Dance floor" }, manager, Ct), Ct);
        Assert.Equal("Dancefloor", own.GetProperty("blueskyTag").GetString());
        Assert.True(own.GetProperty("blueskyTagIsOwn").GetBoolean());

        var kept = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Put, $"{Path}/{id}", new { name = "Disco" }, manager, Ct), Ct);
        Assert.Equal("Dancefloor", kept.GetProperty("blueskyTag").GetString());

        // The tag the name makes is no choice at all; an empty one is none.
        var back = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Put, $"{Path}/{id}", new { name = "Disco", blueskyTag = "Disco" }, manager, Ct), Ct);
        Assert.False(back.GetProperty("blueskyTagIsOwn").GetBoolean());

        var none = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Put, $"{Path}/{id}", new { name = "Disco", blueskyTag = "" }, manager, Ct), Ct);
        Assert.Equal(string.Empty, none.GetProperty("blueskyTag").GetString());
        Assert.True(none.GetProperty("blueskyTagIsOwn").GetBoolean());

        Assert.Equal(4, (await host.FactsAsync(FactType.CalendarCategoryChanged, id.ToString(), Ct)).Count(f => ApiTestHost.DataOf(f).ToString().Contains("blueskyTag", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ABlueskyTagLongerThanBlueskyTakesIsRefused()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var id = await CreateCategoryAsync(host, manager, "Karaoke");
        var tooLong = new string('a', 65);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendJsonAsync(HttpMethod.Post, Path, new { name = "Movie night", blueskyTag = tooLong }, manager, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendJsonAsync(HttpMethod.Put, $"{Path}/{id}", new { name = "Karaoke", blueskyTag = tooLong }, manager, Ct)).StatusCode);
    }

    [Fact]
    public async Task ABlueskyTagOfFewGraphemesButMoreCharactersThanTheColumnIsRefusedNotAServerError()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var id = await CreateCategoryAsync(host, manager, "Karaoke");

        // One letter with 300 combining marks: one grapheme, 301 characters.
        var marks = "a" + new string('́', 300);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendJsonAsync(HttpMethod.Post, Path, new { name = "Movie night", blueskyTag = marks }, manager, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendJsonAsync(HttpMethod.Put, $"{Path}/{id}", new { name = "Karaoke", blueskyTag = marks }, manager, Ct)).StatusCode);
    }
}
