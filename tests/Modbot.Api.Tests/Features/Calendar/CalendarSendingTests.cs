using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat;
using Modbot.VRChat.Calendar;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// Calendar design §17 (2026-10-02), from a recorded test on a live install: Try again on every
/// failed place, a save that clears the old failure at once, and a refused save that names every
/// problem together, the missing VRChat permission first.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarSendingTests(PostgresFixture db)
{
    private const string Channel = "222222222222222222";

    private const string PermissionSentence = "Modbot's VRChat account needs Manage Group Calendar in this group.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ApiTestHost> StartAsync()
    {
        await using (var context = db.NewContext())
        {
            await context.CalendarDateChanges.ExecuteDeleteAsync(Ct);
            await context.CalendarEventPlaces.ExecuteDeleteAsync(Ct);
            await context.CalendarEvents.ExecuteDeleteAsync(Ct);
        }

        return await ApiTestHost.StartAsync(db);
    }

    /// <summary>
    /// The managed group and Modbot's own permissions in it, as the group poll last read them.
    /// Returns what was there, for the test to put back: the API tests share one database.
    /// </summary>
    private async Task<(string? GroupId, List<string>? Permissions)> AccountAsync(string? groupId, List<string>? permissions)
    {
        await using var context = db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        var was = (settings.ManagedGroupId, settings.VRChatAccountPermissions);
        settings.ManagedGroupId = groupId;
        settings.VRChatAccountPermissions = permissions;
        await context.SaveChangesAsync(Ct);
        return was;
    }

    private static Dictionary<string, object?> Event(ApiTestHost host, string repeat = "none")
    {
        var start = host.Clock.UtcNow.AddDays(2);

        return new Dictionary<string, object?>
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
            ["accessType"] = "members",
            ["region"] = "us",
            ["category"] = "film_media",
            ["languages"] = new[] { "eng" },
            ["platforms"] = new[] { "standalonewindows" },
            ["tags"] = Array.Empty<string>(),
            ["visibility"] = "group",
            ["notifyMembers"] = false,
            ["publishToVRChat"] = true,
            ["publishToDiscord"] = true,
            ["postToChannel"] = true,
            ["channelId"] = Channel,
            ["autoOpen"] = false,
            ["openMinutesBefore"] = 10,
            ["draft"] = false,
        };
    }

    private static async Task<Guid> CreateAsync(ApiTestHost host, string cookie, object body)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", body, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("id").GetGuid();
    }

    private async Task AddPlaceAsync(Guid id, string place, Action<CalendarEventPlace> shape)
    {
        await using var context = db.NewContext();
        var row = new CalendarEventPlace
        {
            EventId = id,
            Place = place,
            State = CalendarPlaceStates.Failed,
            Error = "No.",
            ErrorAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        shape(row);
        context.CalendarEventPlaces.Add(row);
        await context.SaveChangesAsync(Ct);
    }

    private async Task<CalendarEventPlace> PlaceAsync(Guid id, string place)
    {
        await using var context = db.NewContext();
        return await context.CalendarEventPlaces.AsNoTracking().SingleAsync(p => p.EventId == id && p.Place == place, Ct);
    }

    private static async Task<JsonElement> PlaceViewAsync(ApiTestHost host, string cookie, Guid id, string place)
    {
        var shown = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, $"/api/calendar/events/{id}", null, cookie, Ct), Ct);
        return shown.GetProperty("places").EnumerateArray().Single(p => p.GetProperty("place").GetString() == place);
    }

    // ── Try again ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryAgain_SendsEveryFailedPlaceAgain_RefusesASecondPress_AndNeedsManageCalendar()
    {
        await using var host = await StartAsync();
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var id = await CreateAsync(host, manager, Event(host));

        await AddPlaceAsync(id, CalendarPlaces.VRChat, p => p.FailedFingerprint = "refused");
        await AddPlaceAsync(id, CalendarPlaces.DiscordEvent, p => p.FailedFingerprint = "refused");
        await AddPlaceAsync(id, CalendarPlaces.ChannelPost, p => p.FailedFingerprint = null);

        foreach (var place in new[] { CalendarPlaces.VRChat, CalendarPlaces.DiscordEvent, CalendarPlaces.ChannelPost })
        {
            var path = $"/api/calendar/events/{id}/{place}/try-again";

            Assert.True((await PlaceViewAsync(host, manager, id, place)).GetProperty("canTryAgain").GetBoolean());
            Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Post, path, null, viewer, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(HttpMethod.Post, path, null, manager, Ct)).StatusCode);

            var row = await PlaceAsync(id, place);
            Assert.Equal(CalendarPlaceStates.Waiting, row.State);
            Assert.Null(row.Error);
            Assert.Null(row.FailedFingerprint);
            Assert.False((await PlaceViewAsync(host, manager, id, place)).GetProperty("canTryAgain").GetBoolean());

            // A second press finds it being sent already.
            Assert.Equal(HttpStatusCode.Conflict, (await host.SendJsonAsync(HttpMethod.Post, path, null, manager, Ct)).StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{id}/nowhere/try-again", null, manager, Ct)).StatusCode);
    }

    [Fact]
    public async Task TryAgain_OnOneDate_ClearsThatDatesVRChatFailure_Once()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var id = await CreateAsync(host, manager, Event(host, repeat: "daily"));

        DateTimeOffset planned;

        await using (var context = db.NewContext())
        {
            var e = await context.CalendarEvents.SingleAsync(x => x.Id == id, Ct);
            planned = e.StartsAt.AddDays(1);
            e.DateChanges.Add(new CalendarDateChange
            {
                Id = Guid.CreateVersion7(),
                EventId = id,
                PlannedStartsAt = planned,
                Title = "Its own title",
                CreatedAt = host.Clock.UtcNow,
                UpdatedAt = host.Clock.UtcNow,
                VRChatError = "Could not find this date on VRChat's calendar.",
                VRChatErrorAt = host.Clock.UtcNow,
                VRChatFailedFingerprint = "refused",
            });
            await context.SaveChangesAsync(Ct);
        }

        var path = $"/api/calendar/events/{id}/vrchat/try-again";
        var body = new { plannedStartsAt = planned };

        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(HttpMethod.Post, path, body, manager, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await host.SendJsonAsync(HttpMethod.Post, path, body, manager, Ct)).StatusCode);

        await using (var check = db.NewContext())
        {
            var change = await check.CalendarDateChanges.AsNoTracking().SingleAsync(c => c.EventId == id, Ct);
            Assert.Null(change.VRChatError);
            Assert.Null(change.VRChatFailedFingerprint);
        }

        // Discord has no dates of their own to try again.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{id}/discordEvent/try-again", body, manager, Ct)).StatusCode);
    }

    // ── A save clears the old failure ───────────────────────────────────────────────────

    [Fact]
    public async Task ASave_ClearsFailedPlacesAtOnce_ButLeavesACreateThatWasNotAddedToItsTryAgain()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var body = Event(host);
        var id = await CreateAsync(host, manager, body);

        await AddPlaceAsync(id, CalendarPlaces.VRChat, p =>
        {
            p.FailedFingerprint = "refused";
            p.MissingGroupPermission = VRChatGroupPermissions.ManageCalendar;
        });
        await AddPlaceAsync(id, CalendarPlaces.DiscordEvent, p => p.FailedFingerprint = "refused");

        // Saved again with nothing changed: the moderator's way of saying "send it again".
        var response = await host.SendJsonAsync(HttpMethod.Put, $"/api/calendar/events/{id}", body, manager, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var places = (await ApiTestHost.BodyOf(response, Ct)).GetProperty("places").EnumerateArray().ToList();
        Assert.All(places, p => Assert.Equal(CalendarPlaceStates.Waiting, p.GetProperty("state").GetString()));
        Assert.All(places, p => Assert.Equal(JsonValueKind.Null, p.GetProperty("error").ValueKind));

        var vrchat = await PlaceAsync(id, CalendarPlaces.VRChat);
        Assert.Null(vrchat.FailedFingerprint);
        Assert.Null(vrchat.MissingGroupPermission);
        Assert.Null(vrchat.ErrorAt);

        // A create VRChat gave no answer to and did not add keeps its own Try again.
        await using (var context = db.NewContext())
        {
            var row = await context.CalendarEventPlaces.SingleAsync(p => p.EventId == id && p.Place == CalendarPlaces.VRChat, Ct);
            row.State = CalendarPlaceStates.Failed;
            row.FailedFingerprint = CalendarVRChatPublisher.NotAddedFingerprint;
            row.Error = CalendarVRChatPublisher.NotAddedError;
            row.ErrorAt = host.Clock.UtcNow;
            await context.SaveChangesAsync(Ct);
        }

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Put, $"/api/calendar/events/{id}", body, manager, Ct)).StatusCode);
        Assert.True(CalendarVRChatPublisher.NotAdded(await PlaceAsync(id, CalendarPlaces.VRChat)));
    }

    // ── Every problem at once ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ARefusedSave_NamesEveryProblemAtOnce_TheMissingPermissionFirst()
    {
        await using var host = await StartAsync();
        var was = await AccountAsync("grp_1", [VRChatGroupPermissions.ViewAuditLog]);

        try
        {
            var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

            var body = Event(host);
            body["title"] = "";
            body["description"] = "";
            body["vrChatImageId"] = "https://example.com/a picture.png";
            body["channelId"] = null;

            var response = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", body, manager, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var answer = await ApiTestHost.BodyOf(response, Ct);
            var problems = answer.GetProperty("problems").EnumerateArray().Select(p => p.GetString()).ToList();

            Assert.Equal(
                new List<string?>
                {
                    PermissionSentence,
                    CalendarVRChatChecks.NoTitle,
                    CalendarVRChatChecks.NoDescription,

                    // Text with no VRChat file id in it (an address that is not VRChat's) is
                    // answered with the sentence that asks for a link or an id, not the
                    // publisher's "not a file id" for an id the event already holds (§15.1, §17.2).
                    Core.Files.VRChatFileIds.NotFound,
                    "Pick a channel to post to.",
                },
                problems);
            Assert.Equal(string.Join(" ", problems), answer.GetProperty("error").GetString());
        }
        finally
        {
            await AccountAsync(was.GroupId, was.Permissions);
        }
    }

    [Fact]
    public async Task TheMissingPermissionAlone_DoesNotRefuseASave_AndAPictureAddressIsKeptAsItsId()
    {
        await using var host = await StartAsync();
        var was = await AccountAsync("grp_1", [VRChatGroupPermissions.ViewAuditLog]);

        try
        {
            var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

            var body = Event(host);
            body["vrChatImageId"] = "https://api.vrchat.cloud/api/1/file/file_0a1b2c/1/file";

            var id = await CreateAsync(host, manager, body);

            await using var context = db.NewContext();
            Assert.Equal("file_0a1b2c", (await context.CalendarEvents.AsNoTracking().SingleAsync(e => e.Id == id, Ct)).VRChatImageId);
        }
        finally
        {
            await AccountAsync(was.GroupId, was.Permissions);
        }
    }

    [Fact]
    public async Task AFailureFoundBeforeSending_ShowsEveryProblem_AndTheMissingPermission_NotVRChatsWords()
    {
        await using var host = await StartAsync();
        var was = await AccountAsync("grp_1", [VRChatGroupPermissions.ViewAuditLog]);

        try
        {
            var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
            var id = await CreateAsync(host, manager, Event(host));

            await AddPlaceAsync(id, CalendarPlaces.VRChat, p =>
            {
                p.FailedFingerprint = "checked";
                p.MissingGroupPermission = VRChatGroupPermissions.ManageCalendar;
                p.Problems = [CalendarVRChatChecks.NotAPictureId];
                p.Error = PermissionSentence + " " + CalendarVRChatChecks.NotAPictureId;
            });

            var vrchat = await PlaceViewAsync(host, manager, id, CalendarPlaces.VRChat);

            Assert.Equal(
                new List<string?> { CalendarVRChatChecks.NotAPictureId },
                vrchat.GetProperty("problems").EnumerateArray().Select(p => p.GetString()).ToList());

            var missing = vrchat.GetProperty("missingGroupPermission");
            Assert.Equal(VRChatGroupPermissions.ManageCalendar, missing.GetProperty("permission").GetString());
            Assert.Equal(JsonValueKind.Null, missing.GetProperty("said").ValueKind);
        }
        finally
        {
            await AccountAsync(was.GroupId, was.Permissions);
        }
    }
}
