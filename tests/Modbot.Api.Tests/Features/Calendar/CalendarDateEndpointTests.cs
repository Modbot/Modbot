using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// Calendar design §2.2: one date of a repeating event cancelled or changed on its own -- what a
/// drag's "This date" and the popup's "This date" send -- leaving the other dates as they are.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarDateEndpointTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ApiTestHost> StartAsync()
    {
        await using (var context = db.NewContext())
            await context.CalendarEvents.ExecuteDeleteAsync(Ct);

        return await ApiTestHost.StartAsync(db);
    }

    private static object Body(ApiTestHost host, string repeat = "daily", int hourShift = 0)
    {
        var start = host.Clock.UtcNow.AddDays(2).AddHours(hourShift);

        return new
        {
            title = "Movie night",
            description = "Bring snacks",
            startsAt = start.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            endsAt = start.AddHours(2).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            timeZone = "UTC",
            repeat,
            repeatDays = Array.Empty<string>(),
            repeatUntil = (string?)null,
            worldId = "wrld_calendar",
            accessType = "members",
            region = "us",
            category = "hangout",
            languages = Array.Empty<string>(),
            platforms = Array.Empty<string>(),
            tags = Array.Empty<string>(),
            visibility = "group",
            notifyMembers = false,
            publishToVRChat = true,
            publishToDiscord = true,
            postToChannel = false,
            autoOpen = false,
            openMinutesBefore = 10,
            draft = false,
        };
    }

    private static async Task<(Guid Id, List<JsonElement> Dates)> CreateAsync(ApiTestHost host, string cookie, object body)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", body, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await ApiTestHost.BodyOf(response, Ct);
        return (view.GetProperty("id").GetGuid(), [.. view.GetProperty("occurrences").EnumerateArray()]);
    }

    private static async Task<JsonElement> EventAsync(ApiTestHost host, string cookie, Guid id) =>
        await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, $"/api/calendar/events/{id}", null, cookie, Ct), Ct);

    private static DateTimeOffset At(JsonElement date, string name) => date.GetProperty(name).GetDateTimeOffset();

    [Fact]
    public async Task MovingOneDateMovesOnlyThatDate_AndIsAFact()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var (id, dates) = await CreateAsync(host, manager, Body(host));

        var second = At(dates[1], "plannedStartsAt");
        var moved = second.AddHours(3);

        var response = await host.SendJsonAsync(
            HttpMethod.Put,
            $"/api/calendar/events/{id}/dates",
            new { plannedStartsAt = second, startsAt = moved, endsAt = moved.AddHours(2), title = (string?)null, description = (string?)null },
            manager,
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var after = (await EventAsync(host, manager, id)).GetProperty("occurrences").EnumerateArray().ToList();
        Assert.Equal(At(dates[0], "startsAt"), At(after[0], "startsAt"));
        Assert.Equal(moved, At(after[1], "startsAt"));
        Assert.Equal(second, At(after[1], "plannedStartsAt"));
        Assert.Equal(At(dates[2], "startsAt"), At(after[2], "startsAt"));

        var fact = Assert.Single(await host.FactsAsync(FactType.PlannedDateChanged, id.ToString(), Ct));
        Assert.Contains("before", fact.Data, StringComparison.Ordinal);
        Assert.Empty(await host.FactsAsync(FactType.PlannedEventChanged, id.ToString(), Ct));
    }

    [Fact]
    public async Task PuttingADateBackAsPlannedLeavesNothingOfItsOwn()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var (id, dates) = await CreateAsync(host, manager, Body(host));

        var second = At(dates[1], "plannedStartsAt");
        var path = $"/api/calendar/events/{id}/dates";

        await host.SendJsonAsync(HttpMethod.Put, path, new { plannedStartsAt = second, startsAt = second.AddHours(1), endsAt = second.AddHours(3), title = (string?)null, description = (string?)null }, manager, Ct);

        // An undo sends the planned time back.
        var undone = await host.SendJsonAsync(HttpMethod.Put, path, new { plannedStartsAt = second, startsAt = second, endsAt = second.AddHours(2), title = (string?)null, description = (string?)null }, manager, Ct);
        Assert.Equal(HttpStatusCode.OK, undone.StatusCode);

        await using var context = db.NewContext();
        Assert.False(await context.CalendarDateChanges.AnyAsync(c => c.EventId == id, Ct));
    }

    [Fact]
    public async Task CancellingOneDateLeavesTheOthers_AndListsItAsCancelled()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var (id, dates) = await CreateAsync(host, manager, Body(host));

        var second = At(dates[1], "plannedStartsAt");

        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{id}/dates/cancel", new { plannedStartsAt = second }, manager, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var view = await EventAsync(host, manager, id);
        Assert.Equal("scheduled", view.GetProperty("state").GetString());
        Assert.DoesNotContain(view.GetProperty("occurrences").EnumerateArray(), o => At(o, "startsAt") == second);
        Assert.Equal(second, At(Assert.Single(view.GetProperty("cancelledDates").EnumerateArray()), "startsAt"));

        Assert.Single(await host.FactsAsync(FactType.PlannedDateCancelled, id.ToString(), Ct));
        Assert.Empty(await host.FactsAsync(FactType.PlannedEventCancelled, id.ToString(), Ct));

        // Twice is the same as once.
        var again = await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{id}/dates/cancel", new { plannedStartsAt = second }, manager, Ct);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Single(await host.FactsAsync(FactType.PlannedDateCancelled, id.ToString(), Ct));
    }

    [Fact]
    public async Task CancellingTheNextDateMovesTheEventOnToTheOneAfter()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var (id, dates) = await CreateAsync(host, manager, Body(host));

        await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{id}/dates/cancel", new { plannedStartsAt = At(dates[0], "plannedStartsAt") }, manager, Ct);

        var view = await EventAsync(host, manager, id);
        Assert.Equal(At(dates[1], "startsAt"), At(view, "occurrenceStartsAt"));
    }

    [Fact]
    public async Task MovingTheWholeSeriesKeepsACancelledDateCancelled()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var (id, dates) = await CreateAsync(host, manager, Body(host));

        var second = At(dates[1], "plannedStartsAt");
        await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{id}/dates/cancel", new { plannedStartsAt = second }, manager, Ct);

        // "All dates", an hour later.
        var response = await host.SendJsonAsync(HttpMethod.Put, $"/api/calendar/events/{id}", Body(host, hourShift: 1), manager, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var view = await EventAsync(host, manager, id);
        Assert.Equal(second.AddHours(1), At(Assert.Single(view.GetProperty("cancelledDates").EnumerateArray()), "startsAt"));
        Assert.DoesNotContain(view.GetProperty("occurrences").EnumerateArray(), o => At(o, "startsAt") == second.AddHours(1));
    }

    [Fact]
    public async Task ADateIsRefused_ForAOneOffEvent_ATimeTheRepeatDoesNotHave_OrOntoAnotherDate()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var (once, onceDates) = await CreateAsync(host, manager, Body(host, repeat: "none"));
        var onlyDate = At(onceDates[0], "startsAt");
        var notRepeating = await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{once}/dates/cancel", new { plannedStartsAt = onlyDate }, manager, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, notRepeating.StatusCode);

        var (id, dates) = await CreateAsync(host, manager, Body(host));
        var second = At(dates[1], "plannedStartsAt");

        var notADate = await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{id}/dates/cancel", new { plannedStartsAt = second.AddMinutes(5) }, manager, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, notADate.StatusCode);

        var third = At(dates[2], "startsAt");
        var onto = await host.SendJsonAsync(
            HttpMethod.Put,
            $"/api/calendar/events/{id}/dates",
            new { plannedStartsAt = second, startsAt = third, endsAt = third.AddHours(2), title = (string?)null, description = (string?)null },
            manager,
            Ct);
        Assert.Equal(HttpStatusCode.BadRequest, onto.StatusCode);
    }

    [Fact]
    public async Task ADateThatHasStartedKeepsItsStart_ButItsEndCanChange()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var (id, dates) = await CreateAsync(host, manager, Body(host));

        var first = At(dates[0], "plannedStartsAt");
        host.Clock.Advance(first - host.Clock.UtcNow + TimeSpan.FromMinutes(30));

        var path = $"/api/calendar/events/{id}/dates";

        var moved = await host.SendJsonAsync(HttpMethod.Put, path, new { plannedStartsAt = first, startsAt = first.AddHours(1), endsAt = first.AddHours(3), title = (string?)null, description = (string?)null }, manager, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);

        var longer = await host.SendJsonAsync(HttpMethod.Put, path, new { plannedStartsAt = first, startsAt = first, endsAt = first.AddHours(3), title = (string?)null, description = (string?)null }, manager, Ct);
        Assert.Equal(HttpStatusCode.OK, longer.StatusCode);
    }

    [Fact]
    public async Task ChangingOneDateNeedsManageCalendar()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);
        var (id, dates) = await CreateAsync(host, manager, Body(host));

        var refused = await host.SendJsonAsync(
            HttpMethod.Post, $"/api/calendar/events/{id}/dates/cancel", new { plannedStartsAt = At(dates[1], "plannedStartsAt") }, viewer, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }
}
