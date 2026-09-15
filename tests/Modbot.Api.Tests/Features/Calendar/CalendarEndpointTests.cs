using System.Globalization;
using System.Net;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// The calendar's API (calendar design): events stored with a fact for every change, gated on
/// ViewCalendar and ManageCalendar, and the feed behind a token that can be replaced.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarEndpointTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ApiTestHost> StartAsync()
    {
        await using (var context = db.NewContext())
        {
            await context.CalendarEvents.ExecuteDeleteAsync(Ct);
            await context.CalendarFeeds.ExecuteDeleteAsync(Ct);
        }

        return await ApiTestHost.StartAsync(db);
    }

    private static object Body(ApiTestHost host, string title = "Movie night", bool draft = false, string repeat = "none", string[]? days = null)
    {
        var start = host.Clock.UtcNow.AddDays(2);

        return new
        {
            title,
            description = "Bring snacks",
            startsAt = start.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            endsAt = start.AddHours(2).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            timeZone = "UTC",
            repeat,
            repeatDays = days ?? [],
            repeatUntil = (string?)null,
            worldId = "wrld_calendar",
            accessType = "members",
            region = "us",
            category = "hangout",
            languages = new[] { "eng" },
            platforms = new[] { "standalonewindows" },
            tags = Array.Empty<string>(),
            visibility = "group",
            notifyMembers = false,
            publishToVRChat = true,
            publishToDiscord = true,
            postToChannel = false,
            autoOpen = true,
            openMinutesBefore = 10,
            draft,
        };
    }

    private static async Task<Guid> CreateAsync(ApiTestHost host, string cookie, object body)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", body, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task SeeingTheCalendarNeedsSeeCalendar()
    {
        await using var host = await StartAsync();
        var (_, nobody) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, "/api/calendar", null, nobody, Ct)).StatusCode);

        var response = await host.SendJsonAsync(HttpMethod.Get, "/api/calendar", null, viewer, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await ApiTestHost.BodyOf(response, Ct)).GetProperty("canManage").GetBoolean());
    }

    [Fact]
    public async Task PlanningAnEventNeedsManageCalendar_AndIsAFact()
    {
        await using var host = await StartAsync();
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var refused = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", Body(host), viewer, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var id = await CreateAsync(host, manager, Body(host));

        Assert.Single(await host.FactsAsync(FactType.PlannedEventCreated, id.ToString(), Ct));

        var view = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/calendar", null, viewer, Ct), Ct);
        var e = Assert.Single(view.GetProperty("events").EnumerateArray());
        Assert.Equal("scheduled", e.GetProperty("state").GetString());
        Assert.Equal(1, e.GetProperty("occurrences").GetArrayLength());
    }

    [Fact]
    public async Task ChangingAnEventBumpsItsVersion_AndRecordsBeforeAndAfter()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var id = await CreateAsync(host, manager, Body(host));

        var response = await host.SendJsonAsync(HttpMethod.Put, $"/api/calendar/events/{id}", Body(host, "Movie night: Alien"), manager, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ApiTestHost.BodyOf(response, Ct);
        Assert.Equal(2, body.GetProperty("version").GetInt32());

        var fact = Assert.Single(await host.FactsAsync(FactType.PlannedEventChanged, id.ToString(), Ct));
        Assert.Contains("Movie night: Alien", fact.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWeeklyEventAlwaysIncludesTheDayItStartsOn()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", Body(host, repeat: "weekly", days: []), manager, Ct);
        var body = await ApiTestHost.BodyOf(response, Ct);

        var startDay = host.Clock.UtcNow.AddDays(2).DayOfWeek.ToString()[..2].ToUpperInvariant();
        Assert.Equal(startDay, Assert.Single(body.GetProperty("repeatDays").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task AnEndBeforeTheStartIsRefused()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ManageCalendar, Ct);

        var start = host.Clock.UtcNow.AddDays(2);
        var body = new
        {
            title = "Backwards",
            startsAt = start.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            endsAt = start.AddHours(-1).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            timeZone = "UTC",
        };

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", body, manager, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CancellingAndDeletingAreFacts_AndADeletedEventLeavesTheCalendar()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var id = await CreateAsync(host, manager, Body(host));

        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{id}/cancel", null, manager, Ct)).StatusCode);

        await using (var context = db.NewContext())
            Assert.Equal(CalendarEventStates.Cancelled, (await context.CalendarEvents.SingleAsync(e => e.Id == id, Ct)).State);

        Assert.Single(await host.FactsAsync(FactType.PlannedEventCancelled, id.ToString(), Ct));

        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(HttpMethod.Delete, $"/api/calendar/events/{id}", null, manager, Ct)).StatusCode);
        Assert.Single(await host.FactsAsync(FactType.PlannedEventDeleted, id.ToString(), Ct));

        var view = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/calendar", null, manager, Ct), Ct);
        Assert.Equal(0, view.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public async Task TheFeedIsBehindAToken_AndANewLinkStopsTheOldOne()
    {
        await using var host = await StartAsync();
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var id = await CreateAsync(host, manager, Body(host));

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, "/api/calendar/feed", null, viewer, Ct)).StatusCode);

        var first = (await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/feed", null, manager, Ct), Ct))
            .GetProperty("path").GetString()!;

        // No cookie: the token is the key.
        var feed = await host.Client.GetAsync(first, Ct);
        Assert.Equal(HttpStatusCode.OK, feed.StatusCode);
        Assert.Equal("text/calendar", feed.Content.Headers.ContentType?.MediaType);

        var text = await feed.Content.ReadAsStringAsync(Ct);
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", text, StringComparison.Ordinal);
        Assert.Contains($"UID:{id:D}@modbot", text, StringComparison.Ordinal);

        // Shown again, the same link.
        var shown = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/calendar/feed", null, manager, Ct), Ct);
        Assert.Equal(first, shown.GetProperty("path").GetString());

        var second = (await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/feed", null, manager, Ct), Ct))
            .GetProperty("path").GetString()!;

        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(first, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(second, Ct)).StatusCode);

        Assert.Equal(2, (await host.FactsAsync(FactType.CalendarFeedRegenerated, "calendar-feed", Ct)).Count);
    }
}
