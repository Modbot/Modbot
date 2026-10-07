using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Live.Stream;
using Modbot.Api.Features.Twitch;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Twitch;

/// <summary>
/// What staff read about Twitch (Twitch design, steps 1 and 3): the Live on Twitch card's data, the
/// recent streams, linking a stream to a calendar event with Manage calendar, the Health card, and
/// who is sent the card's live updates.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class TwitchEndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PostgresFixture _db;

    public TwitchEndpointsTests(PostgresFixture db) => _db = db;

    private async Task<ApiTestHost> StartAsync()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        await using var context = _db.NewContext();
        await context.TwitchStreams.ExecuteDeleteAsync(Ct);
        await context.CalendarDateChanges.ExecuteDeleteAsync(Ct);
        await context.CalendarEventPlaces.ExecuteDeleteAsync(Ct);
        await context.CalendarEvents.ExecuteDeleteAsync(Ct);

        return await ApiTestHost.StartAsync(_db);
    }

    private async Task SetUpTwitchAsync(bool live = true)
    {
        await using var context = _db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        settings.TwitchClientId = FakeTwitch.ClientId;
        settings.TwitchClientSecretEncrypted = "secret";
        settings.TwitchChannelLogin = FakeTwitch.Login;
        settings.TwitchChannelId = FakeTwitch.ChannelId;
        settings.TwitchChannelName = FakeTwitch.DisplayName;
        settings.TwitchCheckedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        settings.TwitchLiveOn = live;
        await context.SaveChangesAsync(Ct);
    }

    private async Task<string> AddStreamAsync(DateTimeOffset startedAt, DateTimeOffset? endedAt = null, Guid? eventId = null)
    {
        await using var context = _db.NewContext();
        var stream = new TwitchStream
        {
            Id = "s" + Guid.NewGuid().ToString("N")[..12],
            StartedAt = startedAt,
            FirstSeenAt = startedAt,
            LastSeenAt = startedAt,
            EndedAt = endedAt,
            Title = "Movie night",
            Category = "VRChat",
            Viewers = 12,
            PeakViewers = 20,
            EventId = eventId,
        };

        context.TwitchStreams.Add(stream);
        await context.SaveChangesAsync(Ct);
        return stream.Id;
    }

    private async Task<Guid> AddEventAsync(string title = "Movie night")
    {
        await using var context = _db.NewContext();
        var calendarEvent = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = title,
            StartsAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.Zero),
            TimeZone = "UTC",
            State = CalendarEventStates.Scheduled,
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

        context.CalendarEvents.Add(calendarEvent);
        await context.SaveChangesAsync(Ct);
        return calendarEvent.Id;
    }

    // ── The card ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheCardIsNotDrawnWhileThePollIsOffOrTwitchIsNotSetUp()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);

        var notSetUp = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/twitch/live", null, cookie, Ct), Ct);
        Assert.False(notSetUp.GetProperty("on").GetBoolean());

        await SetUpTwitchAsync(live: false);
        await AddStreamAsync(host.Clock.UtcNow);
        var off = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/twitch/live", null, cookie, Ct), Ct);
        Assert.False(off.GetProperty("on").GetBoolean());
        Assert.Equal(JsonValueKind.Null, off.GetProperty("stream").ValueKind);
    }

    [Fact]
    public async Task TheCardShowsTheStreamThatIsLive_AndNothingWhenTheChannelIsNot()
    {
        await using var host = await StartAsync();
        await SetUpTwitchAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);

        var offline = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/twitch/live", null, cookie, Ct), Ct);
        Assert.True(offline.GetProperty("on").GetBoolean());
        Assert.Equal(JsonValueKind.Null, offline.GetProperty("stream").ValueKind);

        var eventId = await AddEventAsync("Movie night");
        await AddStreamAsync(host.Clock.UtcNow.AddHours(-5), host.Clock.UtcNow.AddHours(-4));
        var id = await AddStreamAsync(host.Clock.UtcNow.AddMinutes(-20), eventId: eventId);

        var live = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/twitch/live", null, cookie, Ct), Ct);
        var stream = live.GetProperty("stream");

        Assert.Equal(FakeTwitch.DisplayName, live.GetProperty("channelName").GetString());
        Assert.Equal(id, stream.GetProperty("id").GetString());
        Assert.Equal("Movie night", stream.GetProperty("title").GetString());
        Assert.Equal("VRChat", stream.GetProperty("category").GetString());
        Assert.Equal(12, stream.GetProperty("viewers").GetInt32());
        Assert.Equal("Movie night", stream.GetProperty("eventTitle").GetString());
        Assert.Equal("https://www.twitch.tv/ourgroup", stream.GetProperty("link").GetString());
    }

    [Fact]
    public async Task TheCardNeedsSeeLiveInstances()
    {
        await using var host = await StartAsync();
        await SetUpTwitchAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Get, "/api/twitch/live", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public void TheCardsUpdatesReachOnlyWhoMaySeeLiveInstances()
    {
        var sees = LiveScope.ForPerson(ModbotPermissions.ViewLiveInstances);
        var does = LiveScope.ForPerson(ModbotPermissions.ViewPosts | ModbotPermissions.ViewCalendar);

        foreach (var type in new[] { FactType.TwitchOnline, FactType.TwitchUpdated, FactType.TwitchOffline, FactType.TwitchLinked })
        {
            Assert.True(sees.CanSee(LiveKinds.Fact, type));
            Assert.False(does.CanSee(LiveKinds.Fact, type));
        }

        Assert.True(sees.SeesAnything);
    }

    // ── Linking a stream to an event (step 3) ─────────────────────────────────────────────

    [Fact]
    public async Task StaffWithManageCalendarCanLinkAStreamToAnEvent_AndClearIt()
    {
        await using var host = await StartAsync();
        await SetUpTwitchAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageCalendar, Ct);
        var eventId = await AddEventAsync("Dance party");
        var streamId = await AddStreamAsync(host.Clock.UtcNow.AddHours(-3), host.Clock.UtcNow.AddHours(-2));

        var linked = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Put, $"/api/twitch/streams/{streamId}/event", new { eventId }, cookie, Ct), Ct);

        Assert.Equal(eventId, linked.GetProperty("eventId").GetGuid());
        Assert.Equal("Dance party", linked.GetProperty("eventTitle").GetString());

        await using (var context = _db.NewContext())
        {
            var row = await context.TwitchStreams.AsNoTracking().SingleAsync(s => s.Id == streamId, Ct);
            Assert.Equal(eventId, row.EventId);
            Assert.True(row.EventSetByStaff);
        }

        var cleared = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Put, $"/api/twitch/streams/{streamId}/event", new { eventId = (Guid?)null }, cookie, Ct), Ct);

        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("eventId").ValueKind);

        // Both changes are facts, with a person behind them.
        var facts = await host.FactsAsync(FactType.TwitchLinked, streamId, Ct);
        Assert.Equal(2, facts.Count);
        Assert.All(facts, f => Assert.NotNull(f.ActorId));
    }

    [Fact]
    public async Task ALinkToAnEventThatDoesNotExistIsRefused_AndSoIsAStreamThatDoesNot()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageCalendar, Ct);
        var streamId = await AddStreamAsync(host.Clock.UtcNow.AddHours(-3), host.Clock.UtcNow.AddHours(-2));

        var noEvent = await host.SendJsonAsync(
            HttpMethod.Put, $"/api/twitch/streams/{streamId}/event", new { eventId = Guid.NewGuid() }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noEvent.StatusCode);

        var noStream = await host.SendJsonAsync(
            HttpMethod.Put, "/api/twitch/streams/nothing/event", new { eventId = Guid.NewGuid() }, cookie, Ct);
        Assert.Equal(HttpStatusCode.NotFound, noStream.StatusCode);
    }

    [Fact]
    public async Task LinkingNeedsManageCalendar()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ViewLiveInstances, Ct);
        var streamId = await AddStreamAsync(host.Clock.UtcNow.AddHours(-3), host.Clock.UtcNow.AddHours(-2));

        var put = await host.SendJsonAsync(HttpMethod.Put, $"/api/twitch/streams/{streamId}/event", new { eventId = (Guid?)null }, cookie, Ct);
        var list = await host.SendJsonAsync(HttpMethod.Get, "/api/twitch/streams", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
    }

    [Fact]
    public async Task TheRecentStreamsAreListedNewestFirst()
    {
        await using var host = await StartAsync();
        await SetUpTwitchAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageCalendar, Ct);
        var older = await AddStreamAsync(host.Clock.UtcNow.AddDays(-2), host.Clock.UtcNow.AddDays(-2).AddHours(2));
        var newer = await AddStreamAsync(host.Clock.UtcNow.AddDays(-1), host.Clock.UtcNow.AddDays(-1).AddHours(2));

        var list = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/twitch/streams", null, cookie, Ct), Ct);

        Assert.Equal([newer, older], list.EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToArray());
    }

    [Fact]
    public async Task TheCalendarShowsTheStreamsLinkedToAnEvent()
    {
        await using var host = await StartAsync();
        await SetUpTwitchAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);
        var eventId = await AddEventAsync("Movie night");
        var streamId = await AddStreamAsync(new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 1, 1, 30, 0, TimeSpan.Zero), eventId);

        var calendar = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, "/api/calendar?from=2025-12-30T00:00:00Z&to=2026-01-10T00:00:00Z", null, cookie, Ct), Ct);

        var shown = calendar.GetProperty("events").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == eventId);
        var streams = shown.GetProperty("twitchStreams").EnumerateArray().ToList();

        var stream = Assert.Single(streams);
        Assert.Equal(streamId, stream.GetProperty("id").GetString());
        Assert.Equal("https://www.twitch.tv/ourgroup", stream.GetProperty("link").GetString());
    }

    // ── Health ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HealthSaysWhatCheckAndThePollFound()
    {
        var now = new DateTimeOffset(2026, 10, 7, 19, 0, 0, TimeSpan.Zero);
        var settings = new Core.Data.Entities.Settings
        {
            TwitchClientId = FakeTwitch.ClientId,
            TwitchClientSecretEncrypted = "x",
            TwitchChannelLogin = FakeTwitch.Login,
            TwitchChannelId = FakeTwitch.ChannelId,
            TwitchCheckedAt = now.AddDays(-1),
            TwitchLiveOn = true,
            TwitchPolledAt = now.AddMinutes(-1),
        };

        var fine = TwitchEndpoints.HealthOf(settings, now);
        Assert.True(fine.On);
        Assert.False(fine.Silent);

        settings.TwitchPolledAt = now.AddMinutes(-6);
        Assert.True(TwitchEndpoints.HealthOf(settings, now).Silent);

        // A limit is said as a limit, not as silence.
        settings.TwitchStoppedUntil = now.AddMinutes(10);
        var limited = TwitchEndpoints.HealthOf(settings, now);
        Assert.False(limited.Silent);
        Assert.Equal(now.AddMinutes(10), limited.LimitedUntil);

        settings.TwitchStoppedUntil = null;
        settings.TwitchPollProblem = "Twitch did not answer.";
        settings.TwitchProblem = "Twitch did not accept the client id and secret.";
        var problems = TwitchEndpoints.HealthOf(settings, now);
        Assert.Equal("Twitch did not answer.", problems.PollProblem);
        Assert.Equal("Twitch did not accept the client id and secret.", problems.CheckProblem);

        // Switched off, there is nothing to say about the poll.
        settings.TwitchLiveOn = false;
        var off = TwitchEndpoints.HealthOf(settings, now);
        Assert.False(off.On);
        Assert.False(off.Silent);
        Assert.Null(off.PollProblem);
    }

    [Fact]
    public async Task HealthNeedsTheOperationalLog()
    {
        await using var host = await StartAsync();
        var (_, allowed) = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, Ct);
        var (_, refused) = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Get, "/api/twitch/health", null, allowed, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, "/api/twitch/health", null, refused, Ct)).StatusCode);
    }
}
