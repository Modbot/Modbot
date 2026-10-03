using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// The calendar's endpoints for Google Calendar (Google Calendar design, step 2): the tick that starts
/// on for a new event everyone may see, readiness, and Try again for the place and for one date.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarGoogleEndpointTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ApiTestHost> StartAsync(bool googleReady)
    {
        await using (var context = db.NewContext())
        {
            await context.CalendarDateChanges.ExecuteDeleteAsync(Ct);
            await context.CalendarEventPlaces.ExecuteDeleteAsync(Ct);
            await context.CalendarEvents.ExecuteDeleteAsync(Ct);

            var settings = await context.GetSettingsAsync(Ct);
            settings.GoogleClientEmail = googleReady ? "modbot@test-project.iam.gserviceaccount.com" : null;
            settings.GoogleKeyId = googleReady ? "0123456789abcdef" : null;
            settings.GooglePrivateKeyEncrypted = googleReady ? "sealed" : null;
            settings.GoogleCalendarId = googleReady ? "c_events@group.calendar.google.com" : null;
            settings.GoogleCheckedAt = googleReady ? DateTimeOffset.UnixEpoch : null;
            settings.GoogleCanChange = googleReady;
            settings.GoogleProblem = null;
            settings.GoogleSendingOn = googleReady;
            settings.GoogleRemovingEvents = false;
            await context.SaveChangesAsync(Ct);
        }

        return await ApiTestHost.StartAsync(db);
    }

    private static Dictionary<string, object?> Event(ApiTestHost host, string visibility, bool? publishToGoogle = null, string repeat = "none")
    {
        var start = host.Clock.UtcNow.AddDays(2);

        var body = new Dictionary<string, object?>
        {
            ["title"] = "Movie night",
            ["description"] = "Bring snacks",
            ["startsAt"] = start.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            ["endsAt"] = start.AddHours(2).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            ["timeZone"] = "UTC",
            ["repeat"] = repeat,
            ["repeatDays"] = Array.Empty<string>(),
            ["repeatUntil"] = null,
            ["worldId"] = "wrld_calendar",
            ["accessType"] = "public",
            ["region"] = "us",
            ["category"] = "film_media",
            ["languages"] = new[] { "eng" },
            ["platforms"] = new[] { "standalonewindows" },
            ["tags"] = Array.Empty<string>(),
            ["visibility"] = visibility,
            ["notifyMembers"] = false,
            ["publishToVRChat"] = false,
            ["publishToDiscord"] = false,
            ["postToChannel"] = false,
            ["channelId"] = null,
            ["autoOpen"] = false,
            ["openMinutesBefore"] = 10,
            ["draft"] = false,
        };

        if (publishToGoogle is { } ticked)
            body["publishToGoogle"] = ticked;

        return body;
    }

    private static async Task<Guid> CreateAsync(ApiTestHost host, string cookie, object body)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", body, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("id").GetGuid();
    }

    private async Task<CalendarEvent> EventAsync(Guid id)
    {
        await using var context = db.NewContext();
        return await context.CalendarEvents.AsNoTracking().SingleAsync(e => e.Id == id, Ct);
    }

    [Fact]
    public async Task ANewEventEveryoneMaySeeStartsTicked_OnceGoogleIsSetUp()
    {
        await using var host = await StartAsync(googleReady: true);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var everyone = await CreateAsync(host, cookie, Event(host, "public"));
        var members = await CreateAsync(host, cookie, Event(host, "group"));
        var unticked = await CreateAsync(host, cookie, Event(host, "public", publishToGoogle: false));

        Assert.True((await EventAsync(everyone)).PublishToGoogle);
        Assert.False((await EventAsync(members)).PublishToGoogle);
        Assert.False((await EventAsync(unticked)).PublishToGoogle);

        // An edit that does not say keeps the tick.
        var edited = await host.SendJsonAsync(HttpMethod.Put, $"/api/calendar/events/{everyone}", Event(host, "public"), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.True((await EventAsync(everyone)).PublishToGoogle);
        Assert.True((await ApiTestHost.BodyOf(edited, Ct)).GetProperty("publishToGoogle").GetBoolean());
    }

    [Fact]
    public async Task WithoutGoogleANewEventIsNotTicked_AndReadinessSaysSo()
    {
        await using var host = await StartAsync(googleReady: false);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var id = await CreateAsync(host, cookie, Event(host, "public"));
        Assert.False((await EventAsync(id)).PublishToGoogle);

        var calendar = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/calendar", null, cookie, Ct), Ct);
        Assert.False(calendar.GetProperty("ready").GetProperty("google").GetBoolean());
    }

    [Fact]
    public async Task ReadinessSaysGoogleOnceItIsSetUpAndSending()
    {
        await using var host = await StartAsync(googleReady: true);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);

        var calendar = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/calendar", null, cookie, Ct), Ct);

        Assert.True(calendar.GetProperty("ready").GetProperty("google").GetBoolean());
    }

    [Fact]
    public async Task TryAgainSendsAFailedGooglePlaceAgain_AndRefusesASecondPress()
    {
        await using var host = await StartAsync(googleReady: true);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var id = await CreateAsync(host, cookie, Event(host, "public"));

        await using (var context = db.NewContext())
        {
            context.CalendarEventPlaces.Add(new CalendarEventPlace
            {
                EventId = id,
                Place = CalendarPlaces.Google,
                State = CalendarPlaceStates.Failed,
                ExternalId = "mb0123456789abcdefghijklmnop0",
                FailedFingerprint = "refused",
                Error = "Bad Request",
                ErrorAt = host.Clock.UtcNow,
                UpdatedAt = host.Clock.UtcNow,
            });
            await context.SaveChangesAsync(Ct);
        }

        var first = await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{id}/googleCalendar/try-again", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        await using (var context = db.NewContext())
        {
            var place = await context.CalendarEventPlaces.AsNoTracking().SingleAsync(p => p.EventId == id && p.Place == CalendarPlaces.Google, Ct);
            Assert.Equal(CalendarPlaceStates.Waiting, place.State);
            Assert.Null(place.Error);
            Assert.Null(place.FailedFingerprint);

            // The id stays: an insert that may have gone through is read back by it first.
            Assert.Equal("mb0123456789abcdefghijklmnop0", place.ExternalId);
        }

        var second = await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{id}/googleCalendar/try-again", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task TryAgainSendsOneDateWhoseGoogleWriteFailed()
    {
        await using var host = await StartAsync(googleReady: true);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var id = await CreateAsync(host, cookie, Event(host, "public", repeat: "weekly"));
        var e = await EventAsync(id);
        var planned = e.StartsAt.AddDays(7);

        await using (var context = db.NewContext())
        {
            context.CalendarDateChanges.Add(new CalendarDateChange
            {
                Id = Guid.CreateVersion7(),
                EventId = id,
                PlannedStartsAt = planned,
                Title = "Double feature",
                GoogleError = "Could not find this date on Google.",
                GoogleErrorAt = host.Clock.UtcNow,
                GoogleFailedFingerprint = "refused",
                CreatedAt = host.Clock.UtcNow,
                UpdatedAt = host.Clock.UtcNow,
            });
            await context.SaveChangesAsync(Ct);
        }

        var shown = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, $"/api/calendar/events/{id}", null, cookie, Ct), Ct);
        Assert.Contains(shown.GetProperty("occurrences").EnumerateArray(), o =>
            o.TryGetProperty("googleError", out var error) && error.GetString() == "Could not find this date on Google.");

        var response = await host.SendJsonAsync(
            HttpMethod.Post, $"/api/calendar/events/{id}/googleCalendar/try-again", new { plannedStartsAt = planned }, cookie, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var after = db.NewContext();
        var date = await after.CalendarDateChanges.AsNoTracking().SingleAsync(c => c.EventId == id, Ct);
        Assert.Null(date.GoogleError);
        Assert.Null(date.GoogleFailedFingerprint);
    }

    // ── Step 3: the preview and the event's Open ───────────────────────────────────────────────

    /// <summary>
    /// The preview's Google card is the body the Google loop sends: its title, times, zone, place and
    /// repeat line, the description as Google shows it with the world line under it, the calendar's
    /// name, and a padlock for a calendar Check found not public.
    /// </summary>
    [Fact]
    public async Task ThePreviewSaysWhatGoogleIsSent()
    {
        await using var host = await StartAsync(googleReady: true);

        await using (var context = db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.GoogleCalendarName = "Group events";
            settings.GooglePublic = Modbot.Core.Google.GooglePublic.No;
            await context.SaveChangesAsync(Ct);
        }

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var body = Event(host, "public", repeat: "weekly");
        body["description"] = "Bring <snacks> & drinks";
        body["timeZone"] = "Europe/London";
        // A world no other test names, so Modbot knows no name for it.
        body["worldId"] = "wrld_google_preview";

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/preview", new { eventId = (Guid?)null, @event = body }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var google = (await ApiTestHost.BodyOf(response, Ct)).GetProperty("google");

        Assert.Equal("Group events", google.GetProperty("calendarName").GetString());
        Assert.Equal("Movie night", google.GetProperty("title").GetString());
        Assert.Equal(
            "Bring <snacks> & drinks\n\nWorld: wrld_google_preview\nhttps://vrchat.com/home/world/wrld_google_preview",
            google.GetProperty("description").GetString());
        Assert.Equal("wrld_google_preview", google.GetProperty("location").GetString());
        Assert.Equal("Europe/London", google.GetProperty("timeZone").GetString());
        Assert.True(google.GetProperty("private").GetBoolean());

        var repeat = google.GetProperty("repeat");
        Assert.Equal("weekly", repeat.GetProperty("frequency").GetString());
        Assert.Equal(1, repeat.GetProperty("every").GetInt32());
        Assert.Single(repeat.GetProperty("days").EnumerateArray());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, repeat.GetProperty("until").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, repeat.GetProperty("times").ValueKind);
    }

    [Fact]
    public void ThePreviewsRepeatIsTheLineGoogleIsSent_ItsLastDayInTheEventsZone()
    {
        // The last moment of 2026-12-31 in London is 23:59:59 UTC; in Tokyo it is 14:59:59 UTC.
        var london = CalendarPreviews.GoogleRepeat(
            ["RRULE:FREQ=WEEKLY;BYDAY=MO,TH;INTERVAL=2;WKST=MO;UNTIL=20261231T235959Z", "EXDATE;TZID=Europe/London:20261012T200000"],
            "Europe/London");
        var tokyo = CalendarPreviews.GoogleRepeat(["RRULE:FREQ=DAILY;UNTIL=20261231T145959Z"], "Asia/Tokyo");
        var counted = CalendarPreviews.GoogleRepeat(["RRULE:FREQ=MONTHLY;COUNT=6"], "UTC");

        Assert.NotNull(london);
        Assert.Equal("weekly", london.Frequency);
        Assert.Equal(2, london.Every);
        Assert.Equal(new[] { "MO", "TH" }, london.Days);
        Assert.Equal("2026-12-31", london.Until);
        Assert.Null(london.Times);

        Assert.Equal("2026-12-31", tokyo!.Until);
        Assert.Equal(6, counted!.Times);
        Assert.Null(CalendarPreviews.GoogleRepeat([], "UTC"));
    }

    [Fact]
    public async Task APublishedGoogleEventHasItsOpenLink_AndAFailedOneHasNone()
    {
        await using var host = await StartAsync(googleReady: true);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var published = await CreateAsync(host, cookie, Event(host, "public"));
        var failed = await CreateAsync(host, cookie, Event(host, "public"));
        const string link = "https://www.google.com/calendar/event?eid=bWIw";

        await using (var context = db.NewContext())
        {
            foreach (var (id, state) in new[] { (published, CalendarPlaceStates.Published), (failed, CalendarPlaceStates.Failed) })
            {
                context.CalendarEventPlaces.Add(new CalendarEventPlace
                {
                    EventId = id,
                    Place = CalendarPlaces.Google,
                    State = state,
                    ExternalId = "mb0123456789abcdefghijklmnop0",
                    GoogleLink = link,
                    UpdatedAt = host.Clock.UtcNow,
                });
            }

            await context.SaveChangesAsync(Ct);
        }

        static System.Text.Json.JsonElement GooglePlace(System.Text.Json.JsonElement shown) =>
            shown.GetProperty("places").EnumerateArray().Single(p => p.GetProperty("place").GetString() == CalendarPlaces.Google);

        var shownPublished = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, $"/api/calendar/events/{published}", null, cookie, Ct), Ct);
        var shownFailed = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, $"/api/calendar/events/{failed}", null, cookie, Ct), Ct);

        Assert.Equal(link, GooglePlace(shownPublished).GetProperty("link").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, GooglePlace(shownFailed).GetProperty("link").ValueKind);
    }
}
