using System.Globalization;
using System.Net;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Calendar;
using Modbot.Core.Calendar;
using Modbot.Core.Discord;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Calendar;
using StoredSettings = Modbot.Core.Data.Entities.Settings;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// Calendar design §14 (2026-10-01): the form's preview drawn by the code that sends each place,
/// which places are set up, and the cancel that can tell members in the channel -- once, and only
/// when it was ticked.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarPlacesTests(PostgresFixture db)
{
    private const string Channel = "222222222222222222";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ApiTestHost> StartAsync()
    {
        await using (var context = db.NewContext())
        {
            await context.CalendarEventPlaces.ExecuteDeleteAsync(Ct);
            await context.CalendarEvents.ExecuteDeleteAsync(Ct);
        }

        return await ApiTestHost.StartAsync(db);
    }

    private static object Event(
        ApiTestHost host,
        string title = "Movie night",
        string description = "Bring snacks",
        string repeat = "none",
        string? channelId = Channel,
        bool postToChannel = true)
    {
        var start = host.Clock.UtcNow.AddDays(2);

        return new
        {
            title,
            description,
            startsAt = start.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            endsAt = start.AddHours(2).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            timeZone = "Europe/London",
            repeat,
            repeatDays = Array.Empty<string>(),
            repeatUntil = (string?)null,
            worldId = "wrld_calendar",
            accessType = "members",
            region = "us",
            category = "film_media",
            languages = new[] { "eng" },
            platforms = new[] { "standalonewindows", "android" },
            tags = new[] { "movies" },
            visibility = "group",
            notifyMembers = true,
            publishToVRChat = true,
            publishToDiscord = true,
            postToChannel,
            channelId,
            autoOpen = false,
            openMinutesBefore = 10,
            draft = false,
        };
    }

    private static async Task<Guid> CreateAsync(ApiTestHost host, string cookie, object body)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", body, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("id").GetGuid();
    }

    private async Task<List<CalendarEventPlace>> CancelPostsAsync(Guid id)
    {
        await using var context = db.NewContext();
        return await context.CalendarEventPlaces.AsNoTracking()
            .Where(p => p.EventId == id && p.Place == CalendarPlaces.CancelPost)
            .ToListAsync(Ct);
    }

    // ── The cancel post ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ATickedCancel_LeavesOneCancelPostForTheDiscordLoop_InTheEventsChannel()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var id = await CreateAsync(host, manager, Event(host));

        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{id}/cancel", new { postInChannel = true }, manager, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var post = Assert.Single(await CancelPostsAsync(id));
        Assert.Equal(CalendarPlaceStates.Waiting, post.State);
        Assert.Equal(Channel, post.ChannelId);
        Assert.NotNull(post.OccurrenceStartsAt);

        var fact = Assert.Single(await host.FactsAsync(FactType.PlannedEventCancelled, id.ToString(), Ct));
        Assert.True(ApiTestHost.DataOf(fact).GetProperty("postInChannel").GetBoolean());

        // Cancelling again changes nothing and never makes a second post.
        response = await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{id}/cancel", new { postInChannel = true }, manager, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Single(await CancelPostsAsync(id));
        Assert.Single(await host.FactsAsync(FactType.PlannedEventCancelled, id.ToString(), Ct));
    }

    [Fact]
    public async Task AnUntickedCancel_OrOneWithNoBody_PostsNothing()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var unticked = await CreateAsync(host, manager, Event(host));
        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{unticked}/cancel", new { postInChannel = false }, manager, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await CancelPostsAsync(unticked));

        // A client from before the tick sends no body at all.
        var bare = await CreateAsync(host, manager, Event(host, title: "Quiz night"));
        response = await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{bare}/cancel", null, manager, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await CancelPostsAsync(bare));
    }

    [Fact]
    public async Task ATickedCancelWithNoChannelIsRefused_AndCancelsNothing()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var id = await CreateAsync(host, manager, Event(host, channelId: null, postToChannel: false));

        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/calendar/events/{id}/cancel", new { postInChannel = true }, manager, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var context = db.NewContext();
        Assert.Equal(CalendarEventStates.Scheduled, (await context.CalendarEvents.SingleAsync(e => e.Id == id, Ct)).State);
        Assert.Empty(await CancelPostsAsync(id));
    }

    // ── Try again on VRChat's calendar ──────────────────────────────────────────────────

    /// <summary>
    /// Calendar design §3.1 (2026-10-01): a create VRChat gave no answer to, and that was not on its
    /// calendar afterwards, is sent again only when a moderator asks, and only from that state.
    /// </summary>
    [Fact]
    public async Task TryAgain_SendsOnlyAVRChatCreateThatWasNotAdded_AndNeedsManageCalendar()
    {
        await using var host = await StartAsync();
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var id = await CreateAsync(host, manager, Event(host));
        var path = $"/api/calendar/events/{id}/vrchat/try-again";

        // No VRChat place yet.
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendJsonAsync(HttpMethod.Post, path, null, manager, Ct)).StatusCode);

        await using (var context = db.NewContext())
        {
            context.CalendarEventPlaces.Add(new CalendarEventPlace
            {
                EventId = id,
                Place = CalendarPlaces.VRChat,
                State = CalendarPlaceStates.Failed,
                FailedFingerprint = CalendarVRChatPublisher.NotAddedFingerprint,
                Error = CalendarVRChatPublisher.NotAddedError,
                ErrorAt = host.Clock.UtcNow,
                UpdatedAt = host.Clock.UtcNow,
            });
            await context.SaveChangesAsync(Ct);
        }

        var shown = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, $"/api/calendar/events/{id}", null, manager, Ct), Ct);
        var vrchat = shown.GetProperty("places").EnumerateArray().Single(p => p.GetProperty("place").GetString() == CalendarPlaces.VRChat);
        Assert.True(vrchat.GetProperty("canTryAgain").GetBoolean());

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Post, path, null, viewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(HttpMethod.Post, path, null, manager, Ct)).StatusCode);

        await using (var context = db.NewContext())
        {
            var place = await context.CalendarEventPlaces.AsNoTracking().SingleAsync(p => p.EventId == id && p.Place == CalendarPlaces.VRChat, Ct);
            Assert.Equal(CalendarPlaceStates.Waiting, place.State);
            Assert.Null(place.FailedFingerprint);
            Assert.Null(place.Error);
            Assert.Null(place.ErrorAt);
        }

        // Nothing left to try again.
        Assert.Equal(HttpStatusCode.Conflict, (await host.SendJsonAsync(HttpMethod.Post, path, null, manager, Ct)).StatusCode);
    }

    // ── The preview ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePreviewNeedsManageCalendar_AndSavesNothing()
    {
        await using var host = await StartAsync();
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var body = new { eventId = (Guid?)null, @event = Event(host) };

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/preview", body, viewer, Ct)).StatusCode);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/preview", body, manager, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = db.NewContext();
        Assert.False(await context.CalendarEvents.AnyAsync(Ct));
        Assert.Empty(await host.FactsAsync(FactType.PlannedEventCreated, Guid.Empty.ToString(), Ct));
    }

    /// <summary>
    /// The preview's VRChat part is the request the VRChat publisher sends, and its phone calendar
    /// part is what the feed writes: the same event, run through each, says the same thing.
    /// </summary>
    [Fact]
    public async Task ThePreviewSaysWhatVRChatAndTheFeedAreSent()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/calendar/preview", new { eventId = (Guid?)null, @event = Event(host, repeat: "weekly") }, manager, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await ApiTestHost.BodyOf(response, Ct);

        // The same event, filled in the way the endpoint fills it.
        var e = new CalendarEvent();
        var request = new CalendarEventRequest(
            "Movie night", "Bring snacks",
            host.Clock.UtcNow.AddDays(2).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            host.Clock.UtcNow.AddDays(2).AddHours(2).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            "Europe/London", "weekly", [], null, "wrld_calendar", "members", "us", null, null, "film_media",
            ["eng"], ["standalonewindows", "android"], ["movies"], "group", true, true, true, true, Channel, false, 10, false);
        Assert.Null(CalendarEndpoints.Apply(request, e, preview: true));
        e.State = CalendarEventStates.Scheduled;
        CalendarTimeline.Advance(e, host.Clock.UtcNow);

        var sent = CalendarVRChatRequests.Create(e);
        var vrchat = preview.GetProperty("vrChat");

        Assert.False(vrchat.GetProperty("update").GetBoolean());
        Assert.Equal(sent.Title, vrchat.GetProperty("title").GetString());
        Assert.Equal(sent.Description, vrchat.GetProperty("description").GetString());
        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(sent.StartsAt, DateTimeKind.Utc)), vrchat.GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal("film_media", vrchat.GetProperty("category").GetString());
        Assert.Equal("group", vrchat.GetProperty("visibility").GetString());
        Assert.Equal(new[] { "standalonewindows", "android" }, vrchat.GetProperty("platforms").EnumerateArray().Select(p => p.GetString()!));
        Assert.True(vrchat.GetProperty("notify").GetBoolean());
        Assert.Equal("weekly", vrchat.GetProperty("repeat").GetProperty("frequency").GetString());
        Assert.Equal("Europe/London", vrchat.GetProperty("repeat").GetProperty("timeZone").GetString());

        var entry = CalendarFeedWriter.Entry(e, new Dictionary<string, string>());
        var feed = preview.GetProperty("feed");

        Assert.Equal(entry.Title, feed.GetProperty("title").GetString());
        Assert.Equal(entry.Notes, feed.GetProperty("notes").GetString());
        Assert.Equal(entry.Location, feed.GetProperty("location").GetString());
        Assert.Equal(entry.Repeat, feed.GetProperty("repeat").GetString());
        Assert.StartsWith("FREQ=WEEKLY", feed.GetProperty("repeat").GetString(), StringComparison.Ordinal);

        // No Discord bot in this host: no Discord preview, rather than a made-up one.
        Assert.Equal(System.Text.Json.JsonValueKind.Null, preview.GetProperty("discordEvent").ValueKind);
    }

    [Fact]
    public async Task ThePreviewLetsAnUnfinishedEventThrough_ButNotOneTooLong()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        // No title, no description, no channel yet: still drawn.
        var unfinished = await host.SendJsonAsync(
            HttpMethod.Post, "/api/calendar/preview",
            new { eventId = (Guid?)null, @event = Event(host, title: "", description: "", channelId: null) }, manager, Ct);
        Assert.Equal(HttpStatusCode.OK, unfinished.StatusCode);

        var tooLong = await host.SendJsonAsync(
            HttpMethod.Post, "/api/calendar/preview",
            new { eventId = (Guid?)null, @event = Event(host, title: new string('a', 101)) }, manager, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(
            $"The title is longer than {CalendarEvent.MaxTitleLength} characters.",
            (await ApiTestHost.BodyOf(tooLong, Ct)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task ThePreviewOfAnEventOnVRChatAlready_IsAnUpdate_WhichNotifiesNobody()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        var id = await CreateAsync(host, manager, Event(host));

        await using (var context = db.NewContext())
        {
            context.CalendarEventPlaces.Add(new CalendarEventPlace
            {
                EventId = id,
                Place = CalendarPlaces.VRChat,
                State = CalendarPlaceStates.Published,
                ExternalId = "cal_1",
                UpdatedAt = host.Clock.UtcNow,
            });
            await context.SaveChangesAsync(Ct);
        }

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/calendar/preview", new { eventId = id, @event = Event(host) }, manager, Ct);
        var vrchat = (await ApiTestHost.BodyOf(response, Ct)).GetProperty("vrChat");

        Assert.True(vrchat.GetProperty("update").GetBoolean());
        Assert.False(vrchat.GetProperty("notify").GetBoolean());
    }

    // ── Which places are set up ─────────────────────────────────────────────────────────

    private sealed class Bot(DiscordBotState state) : IDiscordBotStatus
    {
        public DiscordBotSnapshot Snapshot() => new(state, null, null, null, 0, false, null, 0);
    }

    [Fact]
    public void VRChatIsSetUpWithAGroupAndAnAccount_DiscordWithAServerAndAConnectedBot()
    {
        var everything = new StoredSettings
        {
            ManagedGroupId = "grp_1",
            VRChatUsername = "modbot",
            VRChatPasswordEncrypted = "sealed",
            DiscordGuildId = "111111111111111111",
        };

        Assert.Equal(new CalendarReadyView(true, true), CalendarReadiness.Of(everything, new Bot(DiscordBotState.Connected)));

        // No group: nothing to publish to or open in.
        Assert.False(CalendarReadiness.VRChat(new StoredSettings { VRChatUsername = "modbot", VRChatPasswordEncrypted = "sealed" }));

        // No account to sign in as.
        Assert.False(CalendarReadiness.VRChat(new StoredSettings { ManagedGroupId = "grp_1" }));

        // The Discord loop runs only while the bot is connected, and a server event needs the server.
        Assert.False(CalendarReadiness.Discord(everything, new Bot(DiscordBotState.Connecting)));
        Assert.False(CalendarReadiness.Discord(everything, new Bot(DiscordBotState.Failed)));
        Assert.False(CalendarReadiness.Discord(everything, null));
        Assert.False(CalendarReadiness.Discord(new StoredSettings(), new Bot(DiscordBotState.Connected)));
    }

    [Fact]
    public void OnlyTheTickedPlacesThatAreNotSetUpAreNamed()
    {
        var nothing = new CalendarReadyView(false, false);

        Assert.Equal(
            new[] { "vrchat", "instance", "discordEvent", "channelPost" },
            CalendarReadiness.NotSetUp(nothing, wantsVRChat: true, wantsInstance: true, wantsDiscordEvent: true, wantsChannelPost: true));

        Assert.Equal(
            new[] { "discordEvent" },
            CalendarReadiness.NotSetUp(new CalendarReadyView(true, false), true, true, true, false));

        Assert.Empty(CalendarReadiness.NotSetUp(nothing, false, false, false, false));
    }

    [Fact]
    public async Task TheCalendarSaysWhichPlacesAreSetUp()
    {
        await using var host = await StartAsync();
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);

        var view = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/calendar", null, viewer, Ct), Ct);
        var ready = view.GetProperty("ready");

        // No Discord bot in this host, so Discord is never set up here. VRChat's answer depends on
        // what other tests left in settings; only that it is given is checked.
        Assert.False(ready.GetProperty("discord").GetBoolean());
        Assert.Contains(ready.GetProperty("vrChat").ValueKind, new[] { System.Text.Json.JsonValueKind.True, System.Text.Json.JsonValueKind.False });
    }
}
