using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Calendar;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Api.Tests.Features.Places;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// What each time an event ran did: a calendar opening leads to its instance and that instance's own
/// figures, an event Modbot did not open is matched to the group's instance in its world by time,
/// and each time is set beside the event's earlier ones.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarResultsTests(PostgresFixture db)
{
    private const ModbotPermissions Sees = ModbotPermissions.ViewCalendar | ModbotPermissions.ViewAnalytics;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ReadSurfaceTestHost> StartAsync()
    {
        var host = await ReadSurfaceTestHost.StartAsync(db);
        await host.ResetAsync(Ct);

        await WithDbAsync(host, async context =>
        {
            await context.CalendarOpenings.ExecuteDeleteAsync(Ct);
            await context.CalendarEventPlaces.ExecuteDeleteAsync(Ct);
            await context.CalendarEvents.ExecuteDeleteAsync(Ct);

            var settings = await context.GetSettingsAsync(Ct);
            settings.ManagedGroupId = "grp_1";
            await context.SaveChangesAsync(Ct);
        });

        return host;
    }

    private static async Task WithDbAsync(ReadSurfaceTestHost host, Func<ModbotContext, Task> act)
    {
        using var scope = host.Services.CreateScope();
        await act(scope.ServiceProvider.GetRequiredService<ModbotContext>());
    }

    private static async Task<CalendarEvent> EventAsync(
        ReadSurfaceTestHost host,
        DateTimeOffset startsAt,
        string title = "Movie night",
        string repeat = CalendarRepeats.None,
        string? worldId = "wrld_a",
        string state = CalendarEventStates.Finished,
        DateTimeOffset? deletedAt = null)
    {
        var calendarEvent = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = title,
            Description = "Bring snacks",
            StartsAt = startsAt,
            EndsAt = startsAt.AddHours(2),
            TimeZone = "UTC",
            Repeat = repeat,
            RepeatDays = repeat == CalendarRepeats.Weekly ? [DayName(startsAt)] : [],
            WorldId = worldId,
            State = state,
            CreatedAt = startsAt.AddDays(-30),
            UpdatedAt = startsAt.AddDays(-30),
            DeletedAt = deletedAt,
        };

        await WithDbAsync(host, async context =>
        {
            context.CalendarEvents.Add(calendarEvent);
            await context.SaveChangesAsync(Ct);
        });

        return calendarEvent;
    }

    private static string DayName(DateTimeOffset at) => at.UtcDateTime.DayOfWeek switch
    {
        DayOfWeek.Monday => "MO",
        DayOfWeek.Tuesday => "TU",
        DayOfWeek.Wednesday => "WE",
        DayOfWeek.Thursday => "TH",
        DayOfWeek.Friday => "FR",
        DayOfWeek.Saturday => "SA",
        _ => "SU",
    };

    private static async Task<VRChatInstance> InstanceAsync(
        ReadSurfaceTestHost host,
        string number,
        DateTimeOffset openedAt,
        DateTimeOffset closedAt,
        int peak,
        string worldId = "wrld_a",
        string groupId = "grp_1")
    {
        var instance = new VRChatInstance
        {
            Id = Guid.NewGuid(),
            Location = $"{worldId}:{number}",
            WorldId = worldId,
            VRChatInstanceId = number,
            GroupId = groupId,
            Type = "group",
            GroupAccessType = "members",
            Region = "us",
            OpenedAt = openedAt,
            LastSeenAt = closedAt,
            ClosedAt = closedAt,
            ClosedBy = "list",
            LastUserCount = 0,
            PeakUserCount = peak,
            SeenInGroupList = true,
        };

        await WithDbAsync(host, async context =>
        {
            context.VRChatInstances.Add(instance);
            await context.SaveChangesAsync(Ct);
        });

        return instance;
    }

    private static Task OpeningAsync(ReadSurfaceTestHost host, CalendarEvent calendarEvent, DateTimeOffset occurrence, VRChatInstance instance) =>
        WithDbAsync(host, async context =>
        {
            context.CalendarOpenings.Add(new CalendarOpening
            {
                EventId = calendarEvent.Id,
                OccurrenceStartsAt = occurrence,
                AttemptedAt = occurrence.AddMinutes(-10),
                Location = instance.Location,
                InstanceId = instance.Id,
            });
            await context.SaveChangesAsync(Ct);
        });

    /// <summary>
    /// The test clock's now, to the whole minute: the database keeps microseconds, and a time asked
    /// for must be the very instant an occurrence starts.
    /// </summary>
    private static DateTimeOffset Minute(ReadSurfaceTestHost host)
    {
        var now = host.Clock.UtcNow;
        return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
    }

    private static string ResultsPath(CalendarEvent calendarEvent, DateTimeOffset at) =>
        $"/api/calendar/events/{calendarEvent.Id}/results?at={Uri.EscapeDataString(at.ToString("O"))}";

    [Fact]
    public async Task SeeingResults_NeedsSeeCalendar_AndSeeAnalytics()
    {
        await using var host = await StartAsync();
        var start = Minute(host).AddDays(-1);
        var calendarEvent = await EventAsync(host, start);

        var calendarOnly = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);
        var both = await host.SignedInAsync(Sees, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(ResultsPath(calendarEvent, start), calendarOnly, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/calendar/past", calendarOnly, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync(ResultsPath(calendarEvent, start), both, Ct)).StatusCode);

        // The calendar says which, so the page asks only when it may.
        var page = await host.GetAsync("/api/calendar", calendarOnly, Ct);
        Assert.Contains("\"canSeeResults\":false", await page.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        page = await host.GetAsync("/api/calendar", both, Ct);
        Assert.Contains("\"canSeeResults\":true", await page.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    /// <summary>
    /// The opening leads to the instance, whose figures are the instance popup's own; new members and
    /// join requests count from when the event opened until a day after it ended, and no further.
    /// </summary>
    [Fact]
    public async Task AnOpenedTime_IsItsInstance_WithTheJoinsOfThatEveningAndTheDayAfter()
    {
        await using var host = await StartAsync();
        var start = Minute(host).AddDays(-3);
        var calendarEvent = await EventAsync(host, start);

        // Opened in another world than the event names: the opening says which instance, not the world.
        var instance = await InstanceAsync(host, "1001", start.AddMinutes(-10), start.AddHours(2), peak: 11, worldId: "wrld_other");
        await OpeningAsync(host, calendarEvent, start, instance);

        await PlacesFixtures.PersonAsync(host, "usr_ada", "Ada", start, Ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_ada", start.AddMinutes(5), "wrld_other", "1001"), Ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_ada", start.AddMinutes(65), "wrld_other", "1001"), Ct);

        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_m1", start.AddMinutes(30)), Ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_m2", start.AddHours(20)), Ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_m3", start.AddHours(27)), Ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_m4", start.AddHours(-1)), Ct);
        await host.WriteFactAsync(AuditFact(FactType.JoinRequestCreated, "usr_r1", start.AddHours(1)), Ct);

        var cookie = await host.SignedInAsync(Sees | ModbotPermissions.ViewAuditLog, Ct);
        var view = await host.GetJsonAsync<CalendarResultsView>(ResultsPath(calendarEvent, start), cookie, Ct);

        var occurrence = view.Occurrence;
        Assert.Equal(instance.Id, occurrence.Instance?.Id);
        Assert.True(occurrence.OpenedByModbot);
        Assert.Equal(11, occurrence.Instance?.PeakPeople);
        Assert.Equal(130m, occurrence.Instance?.MinutesOpen);

        // The start and twenty hours later count; twenty-seven hours later (past the day after the
        // two-hour event) and the hour before do not.
        Assert.Equal(2, occurrence.NewMembers);
        Assert.Equal(1, occurrence.JoinRequests);

        Assert.True(view.CanSeeWhoWasThere);
        Assert.Equal(1, occurrence.Seen?.Visitors);
        Assert.Equal(60m, occurrence.Seen?.MinutesSeen);

        var person = Assert.Single(view.People);
        Assert.Equal("Ada", person.DisplayName);

        Assert.Null(view.Usual);
    }

    /// <summary>
    /// With no opening, the group's instance in the event's world that was open for most of the
    /// event is the one: not a shorter one beside it, and never another group's.
    /// </summary>
    [Fact]
    public async Task WithoutAnOpening_TheGroupsInstanceOpenLongestInTheWorld_IsMatched()
    {
        await using var host = await StartAsync();
        var start = Minute(host).AddDays(-2);
        var calendarEvent = await EventAsync(host, start);

        await InstanceAsync(host, "2001", start.AddMinutes(90), start.AddHours(3), peak: 40);
        var longest = await InstanceAsync(host, "2002", start.AddMinutes(-30), start.AddHours(2), peak: 15);
        await InstanceAsync(host, "2003", start.AddMinutes(-60), start.AddHours(4), peak: 80, groupId: "grp_2");
        await InstanceAsync(host, "2004", start.AddMinutes(-60), start.AddHours(4), peak: 80, worldId: "wrld_b");

        var cookie = await host.SignedInAsync(Sees, Ct);
        var view = await host.GetJsonAsync<CalendarResultsView>(ResultsPath(calendarEvent, start), cookie, Ct);

        Assert.Equal(longest.Id, view.Occurrence.Instance?.Id);
        Assert.False(view.Occurrence.OpenedByModbot);
        Assert.Equal(15, view.Occurrence.Instance?.PeakPeople);
    }

    /// <summary>Who was seen is moderation history, as in the instance popup.</summary>
    [Fact]
    public async Task WhoWasSeen_NeedsSeeTheAuditLog()
    {
        await using var host = await StartAsync();
        var start = Minute(host).AddDays(-2);
        var calendarEvent = await EventAsync(host, start);
        var instance = await InstanceAsync(host, "3001", start, start.AddHours(2), peak: 5);
        await OpeningAsync(host, calendarEvent, start, instance);

        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_ada", start.AddMinutes(5), "wrld_a", "3001"), Ct);

        var cookie = await host.SignedInAsync(Sees, Ct);
        var view = await host.GetJsonAsync<CalendarResultsView>(ResultsPath(calendarEvent, start), cookie, Ct);

        Assert.False(view.CanSeeWhoWasThere);
        Assert.Null(view.Occurrence.Seen);
        Assert.Empty(view.People);
        Assert.Equal(5, view.Occurrence.Instance?.PeakPeople);
    }

    /// <summary>A weekly event's time is set beside the middle of its earlier times.</summary>
    [Fact]
    public async Task EarlierTimes_AreSetBeside_TheirMiddleValue()
    {
        await using var host = await StartAsync();
        var first = Minute(host).AddDays(-15);
        var calendarEvent = await EventAsync(host, first, repeat: CalendarRepeats.Weekly, state: CalendarEventStates.Scheduled);

        var second = first.AddDays(7);
        var third = first.AddDays(14);

        await OpeningAsync(host, calendarEvent, first, await InstanceAsync(host, "4001", first, first.AddHours(2), peak: 4));
        await OpeningAsync(host, calendarEvent, second, await InstanceAsync(host, "4002", second, second.AddHours(2), peak: 6));
        await OpeningAsync(host, calendarEvent, third, await InstanceAsync(host, "4003", third, third.AddHours(2), peak: 20));

        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_m1", first.AddMinutes(10)), Ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_m2", first.AddMinutes(20)), Ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_m3", second.AddMinutes(10)), Ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_m4", second.AddMinutes(20)), Ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_m5", second.AddMinutes(30)), Ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_m6", second.AddMinutes(40)), Ct);

        var cookie = await host.SignedInAsync(Sees, Ct);
        var view = await host.GetJsonAsync<CalendarResultsView>(ResultsPath(calendarEvent, third), cookie, Ct);

        Assert.Equal(20, view.Occurrence.Instance?.PeakPeople);

        var usual = Assert.IsType<CalendarUsual>(view.Usual);
        Assert.Equal(2, usual.Times);
        Assert.Equal(5, usual.PeakPeople);
        Assert.Equal(120m, usual.MinutesOpen);
        Assert.Equal(3, usual.NewMembers);
        Assert.Null(usual.PeopleSeen);
    }

    [Fact]
    public async Task ATimeTheEventHasNotRun_Is404()
    {
        await using var host = await StartAsync();
        var start = Minute(host).AddDays(-2);
        var calendarEvent = await EventAsync(host, start);
        var later = await EventAsync(host, Minute(host).AddDays(2), state: CalendarEventStates.Scheduled);

        var cookie = await host.SignedInAsync(Sees, Ct);

        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync(ResultsPath(calendarEvent, start.AddMinutes(1)), cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync(ResultsPath(later, later.StartsAt), cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync($"/api/calendar/events/{calendarEvent.Id}/results", cookie, Ct)).StatusCode);
    }

    /// <summary>Past events: every time that ran, the most at once first; drafts and deleted events never.</summary>
    [Fact]
    public async Task PastEvents_AreRankedByMostAtOnce()
    {
        await using var host = await StartAsync();
        var now = Minute(host);

        var quiet = await EventAsync(host, now.AddDays(-10), title: "Quiet one");
        var busy = await EventAsync(host, now.AddDays(-5), title: "Busy one");
        var noWorld = await EventAsync(host, now.AddDays(-4), title: "Nowhere", worldId: null);
        await EventAsync(host, now.AddDays(-3), title: "Draft", state: CalendarEventStates.Draft);
        await EventAsync(host, now.AddDays(-3), title: "Deleted", deletedAt: now.AddDays(-1));
        await EventAsync(host, now.AddDays(-200), title: "Long ago");

        await OpeningAsync(host, quiet, quiet.StartsAt, await InstanceAsync(host, "5001", quiet.StartsAt, quiet.EndsAt, peak: 3));
        await OpeningAsync(host, busy, busy.StartsAt, await InstanceAsync(host, "5002", busy.StartsAt, busy.EndsAt, peak: 9));

        var cookie = await host.SignedInAsync(Sees, Ct);
        var view = await host.GetJsonAsync<CalendarPastView>("/api/calendar/past", cookie, Ct);

        Assert.Equal(new[] { "Busy one", "Quiet one", "Nowhere" }, view.Occurrences.Select(o => o.Title));
        Assert.Null(view.Occurrences[2].Instance);
        Assert.Equal(noWorld.Id, view.Occurrences[2].EventId);
        Assert.All(view.Occurrences, o => Assert.Null(o.Seen));
    }
}
