using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Calendar;
using Modbot.Api.Features.Users;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// Cancelling an event and cancelling one date of a repeating one now live in
/// <see cref="CalendarCancellations"/>, which the endpoints and <c>/event</c> in Discord both call
/// (Discord commands design §3.7, step 7). Each case is arranged twice, the same way, and cancelled
/// once through the endpoint and once through the service: the two must give the same answer, leave
/// the same rows and write the same fact. The expected answers are written out, so they also pin
/// what the endpoints have always said.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarCancellationsTests(PostgresFixture db)
{
    private const string EventChannel = "222222222222222222";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ApiTestHost> StartAsync()
    {
        await using (var context = db.NewContext())
            await context.CalendarEvents.ExecuteDeleteAsync(Ct);

        return await ApiTestHost.StartAsync(db);
    }

    private static object Body(ApiTestHost host, string repeat = "daily")
    {
        var start = host.Clock.UtcNow.AddDays(2);

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

    private static async Task<(Guid Id, List<JsonElement> Dates)> CreateAsync(ApiTestHost host, string cookie, string repeat = "daily")
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", Body(host, repeat), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await ApiTestHost.BodyOf(response, Ct);
        return (view.GetProperty("id").GetGuid(), [.. view.GetProperty("occurrences").EnumerateArray()]);
    }

    private static DateTimeOffset At(JsonElement date, string name) => date.GetProperty(name).GetDateTimeOffset();

    private async Task GiveChannelAsync(Guid id)
    {
        await using var context = db.NewContext();
        var calendarEvent = await context.CalendarEvents.SingleAsync(x => x.Id == id, Ct);
        calendarEvent.ChannelId = EventChannel;
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>What an endpoint answered: the status, and the error sentence it carried.</summary>
    private static async Task<(int Status, string? Error)> AnswerOfAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        string? error = null;

        if (text.Length > 0 && JsonDocument.Parse(text).RootElement.TryGetProperty("error", out var found))
            error = found.GetString();

        return ((int)response.StatusCode, error);
    }

    /// <summary>The same answer, read off the service's result the way the endpoints turn it into one.</summary>
    private static (int Status, string? Error) AnswerOf(CalendarCancelResult result) => result.Status switch
    {
        CalendarCancelStatus.Done or CalendarCancelStatus.AlreadyCancelled => (204, null),
        CalendarCancelStatus.NotFound => (404, null),
        _ => (result.HttpStatus, result.Error),
    };

    /// <summary>Everything a cancel leaves behind, to hold two runs to the same thing.</summary>
    private sealed record Left(
        string? State,
        bool EventCancelledAt,
        int Version,
        bool DateCancelled,
        string? DatePostChannel,
        string? CancelPostChannel,
        int DateFacts,
        int EventFacts,
        bool? SaidSo,
        string? ActorId);

    private async Task<Left> LeftAsync(ApiTestHost host, Guid id, DateTimeOffset planned)
    {
        await using var context = db.NewContext();
        var calendarEvent = await context.CalendarEvents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, Ct);
        var change = calendarEvent?.DateChanges.FirstOrDefault(c => c.PlannedStartsAt == planned);
        var place = await context.CalendarEventPlaces.AsNoTracking()
            .SingleOrDefaultAsync(p => p.EventId == id && p.Place == CalendarPlaces.CancelPost, Ct);

        var dateFacts = await host.FactsAsync(FactType.PlannedDateCancelled, id.ToString(), Ct);
        var eventFacts = await host.FactsAsync(FactType.PlannedEventCancelled, id.ToString(), Ct);
        var fact = dateFacts.FirstOrDefault() ?? eventFacts.FirstOrDefault();

        return new Left(
            calendarEvent?.State,
            calendarEvent?.CancelledAt is not null,
            calendarEvent?.Version ?? 0,
            change?.Cancelled ?? false,
            change?.CancelPostChannelId,
            place?.ChannelId,
            dateFacts.Count,
            eventFacts.Count,
            fact is null ? null : ApiTestHost.DataOf(fact).GetProperty("postInChannel").GetBoolean(),
            fact?.ActorId);
    }

    // ── One date ────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string, int, string?> DateCases => new()
    {
        { "second-date", 204, null },
        { "already-cancelled", 204, null },
        { "say-so-no-channel", 400, "The event has no channel to post in." },
        { "say-so-with-channel", 204, null },
        { "one-off", 400, "Only a repeating event has dates of its own." },
        { "not-a-date", 400, "That event has no date then." },
        { "event-cancelled", 409, "A cancelled event cannot be changed." },
        { "unknown-event", 404, null },
        { "date-ended", 409, "That date has already ended." },
    };

    private async Task<(Guid Id, DateTimeOffset Planned, bool SayIt)> ArrangeDateAsync(ApiTestHost host, string cookie, string scenario)
    {
        if (scenario == "unknown-event")
            return (Guid.NewGuid(), host.Clock.UtcNow.AddDays(3), false);

        if (scenario == "one-off")
        {
            var (once, onceDates) = await CreateAsync(host, cookie, "none");
            return (once, At(onceDates[0], "startsAt"), false);
        }

        var (id, dates) = await CreateAsync(host, cookie);
        var first = At(dates[0], "plannedStartsAt");
        var second = At(dates[1], "plannedStartsAt");

        switch (scenario)
        {
            case "already-cancelled":
                Assert.Equal(
                    HttpStatusCode.NoContent,
                    (await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{id}/dates/cancel", new { plannedStartsAt = second }, cookie, Ct)).StatusCode);
                return (id, second, false);

            case "say-so-no-channel":
                return (id, second, true);

            case "say-so-with-channel":
                await GiveChannelAsync(id);
                return (id, second, true);

            case "not-a-date":
                return (id, second.AddMinutes(5), false);

            case "event-cancelled":
                Assert.Equal(
                    HttpStatusCode.NoContent,
                    (await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{id}/cancel", null, cookie, Ct)).StatusCode);
                return (id, second, false);

            case "date-ended":
                return (id, first, false);

            default:
                return (id, second, false);
        }
    }

    [Theory]
    [MemberData(nameof(DateCases))]
    public async Task CancellingADate_ThroughTheService_IsTheEndpointsAnswer_Rows_AndFact(string scenario, int status, string? error)
    {
        await using var host = await StartAsync();
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        // Two events, arranged the same way: one is cancelled by the endpoint, one by the service.
        var viaEndpoint = await ArrangeDateAsync(host, cookie, scenario);
        var viaService = await ArrangeDateAsync(host, cookie, scenario);

        if (scenario == "date-ended")
            host.Clock.Advance(TimeSpan.FromDays(2) + TimeSpan.FromHours(3));

        var endpoint = await AnswerOfAsync(await host.SendJsonAsync(
            HttpMethod.Post,
            $"/api/calendar/events/{viaEndpoint.Id}/dates/cancel",
            new { plannedStartsAt = viaEndpoint.Planned, postInChannel = viaEndpoint.SayIt },
            cookie,
            Ct));

        using var scope = host.Services.CreateScope();
        var service = AnswerOf(await scope.ServiceProvider.GetRequiredService<CalendarCancellations>()
            .CancelDateAsync(viaService.Id, viaService.Planned, viaService.SayIt, new Actor(user.Id, user.Username), Ct));

        Assert.Equal((status, error), endpoint);
        Assert.Equal(endpoint, service);

        var left = await LeftAsync(host, viaEndpoint.Id, viaEndpoint.Planned);
        Assert.Equal(left, await LeftAsync(host, viaService.Id, viaService.Planned));

        if (status == 204 && scenario != "already-cancelled")
        {
            Assert.True(left.DateCancelled);
            Assert.Equal(1, left.DateFacts);
            Assert.Equal(user.Id.ToString(), left.ActorId);
        }
    }

    [Fact]
    public async Task CancellingTheSameDateTwice_ThroughTheService_IsTheSameAsOnce()
    {
        await using var host = await StartAsync();
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var (id, planned, _) = await ArrangeDateAsync(host, cookie, "second-date");

        using var scope = host.Services.CreateScope();
        var cancellations = scope.ServiceProvider.GetRequiredService<CalendarCancellations>();
        var actor = new Actor(user.Id, user.Username);

        Assert.Equal(CalendarCancelStatus.Done, (await cancellations.CancelDateAsync(id, planned, false, actor, Ct)).Status);
        Assert.Equal(CalendarCancelStatus.AlreadyCancelled, (await cancellations.CancelDateAsync(id, planned, false, actor, Ct)).Status);

        Assert.Single(await host.FactsAsync(FactType.PlannedDateCancelled, id.ToString(), Ct));
    }

    [Theory]
    [MemberData(nameof(DateCases))]
    public async Task TheChecksBeforeADateIsCancelled_SayWhatTheCancelWouldRefuse_AndChangeNothing(string scenario, int status, string? error)
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var (id, planned, sayIt) = await ArrangeDateAsync(host, cookie, scenario);

        if (scenario == "date-ended")
            host.Clock.Advance(TimeSpan.FromDays(2) + TimeSpan.FromHours(3));

        var before = await LeftAsync(host, id, planned);

        using var scope = host.Services.CreateScope();
        var check = await scope.ServiceProvider.GetRequiredService<CalendarCancellations>().CheckDateAsync(id, planned, sayIt, Ct);

        if (scenario is "second-date" or "say-so-with-channel")
        {
            Assert.Null(check);
        }
        else if (scenario == "already-cancelled")
        {
            Assert.Equal(CalendarCancelStatus.AlreadyCancelled, check?.Status);
        }
        else
        {
            Assert.NotNull(check);
            Assert.Equal((status, error), AnswerOf(check));
        }

        Assert.Equal(before, await LeftAsync(host, id, planned));
    }

    // ── A whole event ───────────────────────────────────────────────────────────────────────

    public static TheoryData<string, int, string?> EventCases => new()
    {
        { "ok", 204, null },
        { "already-cancelled", 204, null },
        { "say-so-no-channel", 400, "The event has no channel to post in." },
        { "say-so-with-channel", 204, null },
        { "one-off", 204, null },
        { "unknown-event", 404, null },
    };

    private async Task<(Guid Id, bool SayIt)> ArrangeEventAsync(ApiTestHost host, string cookie, string scenario)
    {
        if (scenario == "unknown-event")
            return (Guid.NewGuid(), false);

        var (id, _) = await CreateAsync(host, cookie, scenario == "one-off" ? "none" : "daily");

        switch (scenario)
        {
            case "already-cancelled":
                Assert.Equal(
                    HttpStatusCode.NoContent,
                    (await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{id}/cancel", null, cookie, Ct)).StatusCode);
                return (id, false);

            case "say-so-no-channel":
                return (id, true);

            case "say-so-with-channel":
                await GiveChannelAsync(id);
                return (id, true);

            default:
                return (id, false);
        }
    }

    [Theory]
    [MemberData(nameof(EventCases))]
    public async Task CancellingAnEvent_ThroughTheService_IsTheEndpointsAnswer_Rows_AndFact(string scenario, int status, string? error)
    {
        await using var host = await StartAsync();
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var viaEndpoint = await ArrangeEventAsync(host, cookie, scenario);
        var viaService = await ArrangeEventAsync(host, cookie, scenario);

        var endpoint = await AnswerOfAsync(await host.SendJsonAsync(
            HttpMethod.Post, $"/api/calendar/events/{viaEndpoint.Id}/cancel", new { postInChannel = viaEndpoint.SayIt }, cookie, Ct));

        using var scope = host.Services.CreateScope();
        var service = AnswerOf(await scope.ServiceProvider.GetRequiredService<CalendarCancellations>()
            .CancelEventAsync(viaService.Id, viaService.SayIt, new Actor(user.Id, user.Username), Ct));

        Assert.Equal((status, error), endpoint);
        Assert.Equal(endpoint, service);

        var left = await LeftAsync(host, viaEndpoint.Id, DateTimeOffset.MinValue);
        Assert.Equal(left, await LeftAsync(host, viaService.Id, DateTimeOffset.MinValue));

        if (status == 204 && scenario != "already-cancelled")
        {
            Assert.Equal(CalendarEventStates.Cancelled, left.State);
            Assert.Equal(1, left.EventFacts);
            Assert.Equal(user.Id.ToString(), left.ActorId);
            Assert.Equal(viaEndpoint.SayIt ? EventChannel : null, left.CancelPostChannel);
        }
    }

    [Fact]
    public async Task CancellingAnEventTwice_ThroughTheService_NeverMakesASecondPostOrFact()
    {
        await using var host = await StartAsync();
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var (id, _) = await ArrangeEventAsync(host, cookie, "say-so-with-channel");

        using var scope = host.Services.CreateScope();
        var cancellations = scope.ServiceProvider.GetRequiredService<CalendarCancellations>();
        var actor = new Actor(user.Id, user.Username);

        Assert.Equal(CalendarCancelStatus.Done, (await cancellations.CancelEventAsync(id, true, actor, Ct)).Status);
        Assert.Equal(CalendarCancelStatus.AlreadyCancelled, (await cancellations.CancelEventAsync(id, true, actor, Ct)).Status);

        Assert.Single(await host.FactsAsync(FactType.PlannedEventCancelled, id.ToString(), Ct));

        await using var context = db.NewContext();
        Assert.Single(await context.CalendarEventPlaces.Where(p => p.EventId == id && p.Place == CalendarPlaces.CancelPost).ToListAsync(Ct));
    }
}
