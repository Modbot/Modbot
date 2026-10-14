using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Calendar;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// An event's dates over time: the interested counts kept per date, with their averages, for
/// somebody who may see both the calendar and the analytics.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarSeriesEndpointTests(PostgresFixture db)
{
    private const ModbotPermissions Sees = ModbotPermissions.ViewCalendar | ModbotPermissions.ViewAnalytics;

    private static readonly DateTimeOffset Friday = new(2026, 9, 4, 20, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<CalendarEvent> EventWithCountsAsync(ReadSurfaceTestHost host)
    {
        var calendarEvent = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = "Movie night",
            StartsAt = Friday,
            EndsAt = Friday.AddHours(2),
            TimeZone = "UTC",
            Repeat = CalendarRepeats.Weekly,
            RepeatDays = ["FR"],
            State = CalendarEventStates.Scheduled,
            CreatedAt = Friday.AddDays(-30),
            UpdatedAt = Friday.AddDays(-30),
        };

        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        await context.CalendarEvents.IgnoreAutoIncludes().ExecuteDeleteAsync(Ct);
        context.CalendarEvents.Add(calendarEvent);
        await context.SaveChangesAsync(Ct);

        var store = new CalendarInterestStore(context);
        var now = host.Clock.UtcNow;
        await store.RecordDiscordAsync(calendarEvent.Id, Friday, 10, now, Ct);
        await store.RecordDiscordAsync(calendarEvent.Id, Friday.AddDays(7), 20, now, Ct);
        await store.RecordVRChatAsync(calendarEvent.Id, Friday.AddDays(7), 6, now, Ct);

        return calendarEvent;
    }

    [Fact]
    public async Task ItListsTheDatesEarliestFirst_WithTheAveragesOverTheDatesThatHaveEachCount()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(db);
        await host.ResetAsync(Ct);
        var cookie = await host.SignedInAsync(Sees, Ct);
        var calendarEvent = await EventWithCountsAsync(host);

        var view = await host.GetJsonAsync<CalendarSeriesView>(
            $"/api/calendar/events/{calendarEvent.Id}/series", cookie, Ct);

        Assert.Equal("Movie night", view.Title);
        Assert.Equal([Friday, Friday.AddDays(7)], view.Dates.Select(d => d.StartsAt));
        Assert.Equal([10, 20], view.Dates.Select(d => d.DiscordInterested));
        Assert.Equal([null, 6], view.Dates.Select(d => d.VRChatInterested));
        Assert.All(view.Dates, d => Assert.Null(d.Came));
        Assert.Equal(15m, view.AverageDiscordInterested);
        Assert.Equal(6m, view.AverageVRChatInterested);
        Assert.Null(view.AverageCame);
    }

    [Fact]
    public async Task AnEventThatIsNotThere_IsNotFound()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(db);
        await host.ResetAsync(Ct);
        var cookie = await host.SignedInAsync(Sees, Ct);

        var response = await host.GetAsync($"/api/calendar/events/{Guid.CreateVersion7()}/series", cookie, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ItNeedsBothTheCalendarAndTheAnalytics()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(db);
        await host.ResetAsync(Ct);
        var calendarEvent = await EventWithCountsAsync(host);
        var url = $"/api/calendar/events/{calendarEvent.Id}/series";

        var onlyCalendar = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);
        var onlyAnalytics = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(url, onlyCalendar, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(url, onlyAnalytics, Ct)).StatusCode);
    }
}
