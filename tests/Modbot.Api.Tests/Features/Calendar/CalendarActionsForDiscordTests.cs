using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Calendar;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.TestSupport;
using Modbot.VRChat;
using Modbot.VRChat.Calendar;
using VRChat.API.Model;
using CalendarEvent = Modbot.Core.Data.Entities.CalendarEvent;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// <c>/event</c>'s side of the API (Discord commands design §3.7, step 7): Open now answers with the
/// endpoint's own words for every refusal, a one-off event's only date cancels the event, the checks
/// before a cancel say what it would refuse and change nothing, Manage calendar is needed, and the
/// suggestions list upcoming events and dates for the callers who may see them.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarActionsForDiscordTests(PostgresFixture db)
{
    private const string Group = "grp_0a17232e-6ad4-4889-8e1e-6e0c5fa815fd";
    private const ModbotPermissions Host = ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ApiTestHost> StartAsync(FakeVRChatGate? gate = null, bool group = true)
    {
        await using (var context = db.NewContext())
        {
            await context.CalendarEventPlaces.ExecuteDeleteAsync(Ct);
            await context.CalendarOpenings.ExecuteDeleteAsync(Ct);
            await context.CalendarEvents.ExecuteDeleteAsync(Ct);

            var settings = await context.GetSettingsAsync(Ct);
            settings.ManagedGroupId = group ? Group : null;
            await context.SaveChangesAsync(Ct);
        }

        // The VRChat project registers the opener in a real host; the test host builds the same one.
        return await ApiTestHost.StartAsync(db, gate, services =>
        {
            services.AddScoped<CalendarFacts>();
            services.AddScoped(sp => new PlaceStore(sp.GetRequiredService<ModbotContext>(), sp.GetRequiredService<IModbotClock>()));
            services.AddScoped(sp => new CalendarOpener(
                sp.GetRequiredService<IVRChatGate>(),
                sp.GetRequiredService<ModbotContext>(),
                sp.GetRequiredService<PlaceStore>(),
                sp.GetRequiredService<IModbotClock>(),
                sp.GetRequiredService<CalendarFacts>()));
        });
    }

    private static object Body(ApiTestHost host, string title = "Movie night", string repeat = "daily", int startsInHours = 48)
    {
        var start = host.Clock.UtcNow.AddHours(startsInHours);

        return new
        {
            title,
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

    private static async Task<(Guid Id, List<JsonElement> Dates)> CreateAsync(
        ApiTestHost host, string cookie, string title = "Movie night", string repeat = "daily", int startsInHours = 48)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", Body(host, title, repeat, startsInHours), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await ApiTestHost.BodyOf(response, Ct);
        return (view.GetProperty("id").GetGuid(), [.. view.GetProperty("occurrences").EnumerateArray()]);
    }

    private static DateTimeOffset At(JsonElement date, string name) => date.GetProperty(name).GetDateTimeOffset();

    private static StaffMember Staff(ModbotUser user, ModbotPermissions held = Host) => new(user.Id, user.Username, held);

    private static ICalendarActions Actions(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<ICalendarActions>();

    /// <summary>How many times VRChat was asked to make an instance. Setting an event up asks it nothing of the kind.</summary>
    private static int InstanceRequests(ApiTestHost host)
        => host.VRChat.Calls.Count(c => c.Endpoint.Class == VRChatEndpointClass.InstancesCreate);

    private async Task ChangeEventAsync(Guid id, Action<CalendarEvent> change)
    {
        await using var context = db.NewContext();
        var calendarEvent = await context.CalendarEvents.SingleAsync(e => e.Id == id, Ct);
        change(calendarEvent);
        await context.SaveChangesAsync(Ct);
    }

    // ── Open now: the endpoint's own words ──────────────────────────────────────────────────

    public static TheoryData<string, string> Refusals => new()
    {
        { "not-configured", "Pick a managed group first." },
        { "no-world", "The event has no world." },
        { "too-early", "It is too early to open the instance." },
        { "over", "That event has already ended." },
        { "already-open", "The instance is already open." },
        { "checking", "Checking whether VRChat opened the instance." },
    };

    /// <summary>An event that does not repeat, with the state each refusal needs.</summary>
    private async Task<Guid> ArrangeOpenAsync(ApiTestHost host, string cookie, string scenario)
    {
        var (id, _) = await CreateAsync(host, cookie, repeat: "none");
        var now = host.Clock.UtcNow;

        if (scenario != "too-early" && scenario != "over")
        {
            await ChangeEventAsync(id, e =>
            {
                e.StartsAt = now.AddMinutes(30);
                e.EndsAt = now.AddMinutes(150);
            });
        }

        if (scenario == "no-world")
            await ChangeEventAsync(id, e => e.WorldId = null);

        if (scenario is "already-open" or "checking")
        {
            await using var context = db.NewContext();
            context.CalendarOpenings.Add(new CalendarOpening
            {
                EventId = id,
                OccurrenceStartsAt = now.AddMinutes(30),
                AttemptedAt = now,
                Location = scenario == "already-open" ? $"wrld_calendar:1~group({Group})~groupAccessType(members)~region(us)" : null,
                Checking = scenario == "checking",
            });
            await context.SaveChangesAsync(Ct);
        }

        return id;
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task OpenNow_RefusesInTheEndpointsWords_ForEveryRefusal(string scenario, string words)
    {
        await using var host = await StartAsync(group: scenario != "not-configured");
        var (user, cookie) = await host.SignedInAsync(Host, Ct);

        var viaEndpoint = await ArrangeOpenAsync(host, cookie, scenario);
        var viaService = await ArrangeOpenAsync(host, cookie, scenario);

        if (scenario == "over")
            host.Clock.Advance(TimeSpan.FromDays(3));

        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{viaEndpoint}/open", null, cookie, Ct);

        using var scope = host.Services.CreateScope();
        var answer = await Actions(scope).OpenNowAsync(viaService, Staff(user), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(words, (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());

        Assert.False(answer.Opened);
        Assert.Equal(words, answer.Message);
        Assert.Equal("Movie night", answer.Title);

        // A refusal asks VRChat to make nothing.
        Assert.Equal(0, InstanceRequests(host));
    }

    [Fact]
    public async Task OpenNow_ForAnEventThatIsNotThere_SaysSo_WhereTheEndpointAnswers404()
    {
        await using var host = await StartAsync();
        var (user, cookie) = await host.SignedInAsync(Host, Ct);
        var missing = Guid.NewGuid();

        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{missing}/open", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var scope = host.Services.CreateScope();
        var answer = await Actions(scope).OpenNowAsync(missing, Staff(user), Ct);

        Assert.False(answer.Opened);
        Assert.Equal("That event does not exist.", answer.Message);
    }

    [Fact]
    public async Task OpenNow_AsksVRChatOnce_AsTheAccount_AndSaysWhatVRChatAnswered()
    {
        var gate = new FakeVRChatGate();
        gate.Returns("CreateInstance", VRChatResult<Instance>.Failure(403, "Forbidden"));
        await using var host = await StartAsync(gate);
        var (user, cookie) = await host.SignedInAsync(Host, Ct);
        var id = await ArrangeOpenAsync(host, cookie, "ready");

        using var scope = host.Services.CreateScope();
        var answer = await Actions(scope).OpenNowAsync(id, Staff(user), Ct);

        Assert.False(answer.Opened);
        Assert.Equal("VRChat refused: Forbidden", answer.Message);

        var call = Assert.Single(host.VRChat.Calls, c => c.Endpoint.Class == VRChatEndpointClass.InstancesCreate);
        Assert.Equal(VRChatCallPriority.Interactive, call.Priority);

        await using var context = db.NewContext();
        Assert.Equal(user.Id, (await context.CalendarOpenings.SingleAsync(o => o.EventId == id, Ct)).OpenedByUserId);
        Assert.Single(await host.FactsAsync(FactType.PlannedEventInstanceFailed, id.ToString(), Ct));
    }

    [Fact]
    public async Task OpenNow_WhenVRChatSaysSlowDown_SaysItWillTryAgain()
    {
        var gate = new FakeVRChatGate();
        gate.Returns("CreateInstance", VRChatResult<Instance>.Failure(429, "Too many requests"));
        await using var host = await StartAsync(gate);
        var (user, cookie) = await host.SignedInAsync(Host, Ct);
        var id = await ArrangeOpenAsync(host, cookie, "ready");

        using var scope = host.Services.CreateScope();
        var answer = await Actions(scope).OpenNowAsync(id, Staff(user), Ct);

        Assert.False(answer.Opened);
        Assert.Equal(CalendarActionsForDiscord.TryAgainSoon, answer.Message);
        Assert.Equal(1, InstanceRequests(host));
    }

    [Fact]
    public async Task OpenNow_WhenVRChatGivesNoClearAnswer_SaysItIsChecking_AndAsksOnce()
    {
        // Nothing scripted: the gate answers with no status at all, as a dropped connection does.
        await using var host = await StartAsync();
        var (user, cookie) = await host.SignedInAsync(Host, Ct);
        var id = await ArrangeOpenAsync(host, cookie, "ready");

        using var scope = host.Services.CreateScope();
        var answer = await Actions(scope).OpenNowAsync(id, Staff(user), Ct);

        Assert.False(answer.Opened);
        Assert.Equal("Checking whether VRChat opened the instance.", answer.Message);
        Assert.Equal(1, InstanceRequests(host));

        // And pressed again, it is refused until the check ends, and asks VRChat nothing.
        var again = await Actions(scope).OpenNowAsync(id, Staff(user), Ct);
        Assert.Equal("Checking whether VRChat opened the instance.", again.Message);
        Assert.Equal(1, InstanceRequests(host));
    }

    [Fact]
    public async Task EveryStep_NeedsManageCalendar_AndChangesNothingWithout()
    {
        await using var host = await StartAsync();
        var (manager, cookie) = await host.SignedInAsync(Host, Ct);
        var (viewer, _) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);
        var (id, dates) = await CreateAsync(host, cookie);
        var second = At(dates[1], "plannedStartsAt");

        using var scope = host.Services.CreateScope();
        var actions = Actions(scope);
        var weak = Staff(viewer, ModbotPermissions.ViewCalendar);

        Assert.Equal(CalendarActionsForDiscord.NoPermission, (await actions.OpenNowAsync(id, weak, Ct)).Message);
        Assert.Equal(CalendarActionsForDiscord.NoPermission, (await actions.PlanCancelDateAsync(id, second, false, weak, Ct)).Message);

        var cancel = await actions.CancelDateAsync(id, second, false, weak, Ct);
        Assert.False(cancel.Done);
        Assert.Equal(CalendarActionsForDiscord.NoPermission, cancel.Message);

        Assert.Empty(await host.FactsAsync(FactType.PlannedDateCancelled, id.ToString(), Ct));
        Assert.Equal(0, InstanceRequests(host));

        // Administrator may do everything, as in the web app.
        var admin = await actions.CancelDateAsync(id, second, false, Staff(manager, ModbotPermissions.Administrator), Ct);
        Assert.True(admin.Done);
    }

    // ── Cancel one date ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADateOfARepeatingEvent_IsPlanned_ThenCancelledOnce_LeavingTheOthers()
    {
        await using var host = await StartAsync();
        var (user, cookie) = await host.SignedInAsync(Host, Ct);
        var (id, dates) = await CreateAsync(host, cookie);
        var second = At(dates[1], "plannedStartsAt");

        using var scope = host.Services.CreateScope();
        var actions = Actions(scope);

        var plan = await actions.PlanCancelDateAsync(id, second, false, Staff(user), Ct);
        Assert.True(plan.Allowed);
        Assert.False(plan.WholeEvent);
        Assert.Equal("Movie night", plan.Title);
        Assert.Equal(second, plan.StartsAt);

        // Planning changes nothing.
        Assert.Empty(await host.FactsAsync(FactType.PlannedDateCancelled, id.ToString(), Ct));

        var first = await actions.CancelDateAsync(id, second, false, Staff(user), Ct);
        Assert.True(first.Done);
        Assert.False(first.Repeat);

        var fact = Assert.Single(await host.FactsAsync(FactType.PlannedDateCancelled, id.ToString(), Ct));
        Assert.Equal(user.Id.ToString(), fact.ActorId);
        Assert.Empty(await host.FactsAsync(FactType.PlannedEventCancelled, id.ToString(), Ct));

        var again = await actions.CancelDateAsync(id, second, false, Staff(user), Ct);
        Assert.True(again.Done);
        Assert.True(again.Repeat);
        Assert.Single(await host.FactsAsync(FactType.PlannedDateCancelled, id.ToString(), Ct));

        await using var context = db.NewContext();
        var calendarEvent = await context.CalendarEvents.AsNoTracking().SingleAsync(e => e.Id == id, Ct);
        Assert.Equal(CalendarEventStates.Scheduled, calendarEvent.State);
        Assert.Equal(At(dates[0], "startsAt"), CalendarRepeat.Next(calendarEvent, host.Clock.UtcNow)!.Value.StartsAt);
        Assert.DoesNotContain(CalendarRepeat.Between(calendarEvent, host.Clock.UtcNow, host.Clock.UtcNow.AddDays(10)), o => o.StartsAt == second);
    }

    [Fact]
    public async Task TheOnlyDateOfAOneOffEvent_IsTheWholeEvent()
    {
        await using var host = await StartAsync();
        var (user, cookie) = await host.SignedInAsync(Host, Ct);
        var (id, dates) = await CreateAsync(host, cookie, repeat: "none");
        var only = At(dates[0], "startsAt");

        using var scope = host.Services.CreateScope();
        var actions = Actions(scope);

        var plan = await actions.PlanCancelDateAsync(id, only, false, Staff(user), Ct);
        Assert.True(plan.Allowed);
        Assert.True(plan.WholeEvent);
        Assert.Equal(only, plan.StartsAt);
        Assert.Equal("Movie night", plan.Title);

        var cancelled = await actions.CancelDateAsync(id, only, false, Staff(user), Ct);
        Assert.True(cancelled.Done);

        await using (var context = db.NewContext())
            Assert.Equal(CalendarEventStates.Cancelled, (await context.CalendarEvents.AsNoTracking().SingleAsync(e => e.Id == id, Ct)).State);

        Assert.Single(await host.FactsAsync(FactType.PlannedEventCancelled, id.ToString(), Ct));
        Assert.Empty(await host.FactsAsync(FactType.PlannedDateCancelled, id.ToString(), Ct));

        var again = await actions.CancelDateAsync(id, only, false, Staff(user), Ct);
        Assert.True(again.Repeat);
        Assert.Single(await host.FactsAsync(FactType.PlannedEventCancelled, id.ToString(), Ct));

        var planAgain = await actions.PlanCancelDateAsync(id, only, false, Staff(user), Ct);
        Assert.False(planAgain.Allowed);
        Assert.Equal(CalendarActionsForDiscord.AlreadyCancelledEvent, planAgain.Message);
    }

    [Fact]
    public async Task TheEndpointStillRefusesADateOfAnEventThatDoesNotRepeat()
    {
        // The endpoint still refuses a date of an event that does not repeat; only the bot's command
        // reads it as the whole event.
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(Host, Ct);
        var (id, dates) = await CreateAsync(host, cookie, repeat: "none");

        var response = await host.SendJsonAsync(
            HttpMethod.Post, $"/api/calendar/events/{id}/dates/cancel", new { plannedStartsAt = At(dates[0], "startsAt") }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task APlanSays_WhatTheCancelWouldRefuse_InTheEndpointsWords()
    {
        await using var host = await StartAsync();
        var (user, cookie) = await host.SignedInAsync(Host, Ct);
        var (id, dates) = await CreateAsync(host, cookie);
        var second = At(dates[1], "plannedStartsAt");

        using var scope = host.Services.CreateScope();
        var actions = Actions(scope);
        var staff = Staff(user);

        Assert.Equal("That event has no date then.", (await actions.PlanCancelDateAsync(id, second.AddMinutes(5), false, staff, Ct)).Message);
        Assert.Equal("The event has no channel to post in.", (await actions.PlanCancelDateAsync(id, second, true, staff, Ct)).Message);
        Assert.Equal("That event does not exist.", (await actions.PlanCancelDateAsync(Guid.NewGuid(), second, false, staff, Ct)).Message);

        await actions.CancelDateAsync(id, second, false, staff, Ct);
        Assert.Equal(CalendarActionsForDiscord.AlreadyCancelledDate, (await actions.PlanCancelDateAsync(id, second, false, staff, Ct)).Message);

        host.Clock.Advance(TimeSpan.FromDays(2) + TimeSpan.FromHours(3));
        Assert.Equal("That date has already ended.", (await actions.PlanCancelDateAsync(id, At(dates[0], "plannedStartsAt"), false, staff, Ct)).Message);
    }

    [Fact]
    public async Task SayingSo_IsTodaysCancelBehaviour_APostInTheEventsChannel_Once()
    {
        await using var host = await StartAsync();
        var (user, cookie) = await host.SignedInAsync(Host, Ct);
        var (id, dates) = await CreateAsync(host, cookie);
        var second = At(dates[1], "plannedStartsAt");
        await ChangeEventAsync(id, e => e.ChannelId = "222222222222222222");

        using var scope = host.Services.CreateScope();
        var cancelled = await Actions(scope).CancelDateAsync(id, second, true, Staff(user), Ct);

        Assert.True(cancelled.Done);

        await using var context = db.NewContext();
        var change = await context.CalendarDateChanges.AsNoTracking().SingleAsync(c => c.EventId == id, Ct);
        Assert.Equal("222222222222222222", change.CancelPostChannelId);
        Assert.True(ApiTestHost.DataOf(Assert.Single(await host.FactsAsync(FactType.PlannedDateCancelled, id.ToString(), Ct))).GetProperty("postInChannel").GetBoolean());
    }

    // ── Suggestions ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpcomingEvents_AreSoonestFirst_FilteredByTitle_AndOnlyWhatIsStillComing()
    {
        await using var host = await StartAsync();
        var (user, cookie) = await host.SignedInAsync(Host, Ct);
        var (later, _) = await CreateAsync(host, cookie, "Karaoke", "none", startsInHours: 96);
        var (soon, _) = await CreateAsync(host, cookie, "Movie night", "none", startsInHours: 24);
        var (cancelled, _) = await CreateAsync(host, cookie, "Movie marathon", "none", startsInHours: 12);
        var (deleted, _) = await CreateAsync(host, cookie, "Movie sneak peek", "none", startsInHours: 6);

        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{cancelled}/cancel", null, cookie, Ct)).StatusCode);
        await ChangeEventAsync(deleted, e => e.DeletedAt = host.Clock.UtcNow);

        using var scope = host.Services.CreateScope();
        var actions = Actions(scope);

        var all = await actions.UpcomingAsync(string.Empty, 25, Staff(user), Ct);
        Assert.Equal([soon, later], all.Select(e => e.Id));
        Assert.Equal(["Movie night", "Karaoke"], all.Select(e => e.Title));
        Assert.EndsWith(" UTC", all[0].When, StringComparison.Ordinal);

        var movies = await actions.UpcomingAsync("MOVIE", 25, Staff(user), Ct);
        Assert.Equal([soon], movies.Select(e => e.Id));

        Assert.Single(await actions.UpcomingAsync(string.Empty, 1, Staff(user), Ct));
    }

    [Fact]
    public async Task UpcomingEvents_ShowARepeatingEventAtItsNextDate()
    {
        await using var host = await StartAsync();
        var (user, cookie) = await host.SignedInAsync(Host, Ct);
        var (id, dates) = await CreateAsync(host, cookie);

        host.Clock.Advance(TimeSpan.FromDays(2) + TimeSpan.FromHours(3));

        using var scope = host.Services.CreateScope();
        var event0 = Assert.Single(await Actions(scope).UpcomingAsync(string.Empty, 25, Staff(user), Ct));

        Assert.Equal(id, event0.Id);
        Assert.Equal(At(dates[1], "startsAt"), event0.NextStartsAt);
    }

    [Fact]
    public async Task Suggestions_NeedManageCalendarAndSeeCalendar_AndAreEmptyForAnyoneElse()
    {
        await using var host = await StartAsync();
        var (user, cookie) = await host.SignedInAsync(Host, Ct);
        var (id, _) = await CreateAsync(host, cookie);

        using var scope = host.Services.CreateScope();
        var actions = Actions(scope);

        foreach (var held in new[] { ModbotPermissions.ManageCalendar, ModbotPermissions.ViewCalendar, ModbotPermissions.Kick })
        {
            Assert.Empty(await actions.UpcomingAsync(string.Empty, 25, Staff(user, held), Ct));
            Assert.Empty(await actions.DatesAsync(id, 10, Staff(user, held), Ct));
        }

        Assert.NotEmpty(await actions.UpcomingAsync(string.Empty, 25, Staff(user, ModbotPermissions.Administrator), Ct));
        Assert.NotEmpty(await actions.DatesAsync(id, 10, Staff(user, ModbotPermissions.Administrator), Ct));
    }

    [Fact]
    public async Task TheDateList_IsTheNextTenDates_SoonestFirst_WithAMovedDateAtItsNewTimeAndACancelledOneLeftOut()
    {
        await using var host = await StartAsync();
        var (user, cookie) = await host.SignedInAsync(Host, Ct);
        var (id, dates) = await CreateAsync(host, cookie);

        var second = At(dates[1], "plannedStartsAt");
        var third = At(dates[2], "plannedStartsAt");

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{id}/dates/cancel", new { plannedStartsAt = second }, cookie, Ct)).StatusCode);

        var moved = third.AddHours(3);
        Assert.Equal(
            HttpStatusCode.OK,
            (await host.SendJsonAsync(
                HttpMethod.Put,
                $"/api/calendar/events/{id}/dates",
                new { plannedStartsAt = third, startsAt = moved, endsAt = moved.AddHours(2), title = (string?)null, description = (string?)null },
                cookie,
                Ct)).StatusCode);

        using var scope = host.Services.CreateScope();
        var offered = await Actions(scope).DatesAsync(id, 10, Staff(user), Ct);

        Assert.Equal(10, offered.Count);
        Assert.Equal(offered.OrderBy(d => d.StartsAt).Select(d => d.PlannedStartsAt), offered.Select(d => d.PlannedStartsAt));
        Assert.DoesNotContain(offered, d => d.PlannedStartsAt == second);

        var movedOne = offered.Single(d => d.PlannedStartsAt == third);
        Assert.Equal(moved, movedOne.StartsAt);
        Assert.All(offered.Where(d => d.PlannedStartsAt != third), d => Assert.Equal(d.PlannedStartsAt, d.StartsAt));
        Assert.All(offered, d => Assert.EndsWith(" UTC", d.When, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheDateList_OffersNothingForAnEventThatIsCancelledOrNotThere()
    {
        await using var host = await StartAsync();
        var (user, cookie) = await host.SignedInAsync(Host, Ct);
        var (id, _) = await CreateAsync(host, cookie);
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{id}/cancel", null, cookie, Ct)).StatusCode);

        using var scope = host.Services.CreateScope();
        var actions = Actions(scope);

        Assert.Empty(await actions.DatesAsync(id, 10, Staff(user), Ct));
        Assert.Empty(await actions.DatesAsync(Guid.NewGuid(), 10, Staff(user), Ct));
    }
}
