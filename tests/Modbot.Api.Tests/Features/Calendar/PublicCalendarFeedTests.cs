using System.Globalization;
using System.Net;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// The calendar's public feed (calendar design §6.1): off and 404 until someone with Manage calendar
/// turns it on, the switch a fact like any setting, and only the events visible to everyone in it.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PublicCalendarFeedTests(PostgresFixture db)
{
    private const string FeedPath = "/api/calendar/public.ics";
    private const string Address = "https://modbot.example";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ApiTestHost> StartAsync()
    {
        await using (var context = db.NewContext())
        {
            await context.CalendarEvents.ExecuteDeleteAsync(Ct);
        }

        var host = await ApiTestHost.StartAsync(db);

        // The settings row is shared with every other test: start each one off, with an address.
        using var scope = host.Services.CreateScope();
        var stored = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await stored.GetSettingsAsync(Ct);
        settings.CalendarPublicFeed = false;
        settings.PublicAddress = Address;
        await stored.SaveChangesAsync(Ct);

        return host;
    }

    private static object Body(
        ApiTestHost host, string title, string visibility = "public", string accessType = "members", bool draft = false)
    {
        var start = host.Clock.UtcNow.AddDays(2);

        return new
        {
            title,
            description = "Come along",
            startsAt = start.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            endsAt = start.AddHours(2).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            timeZone = "UTC",
            repeat = "none",
            repeatDays = Array.Empty<string>(),
            repeatUntil = (string?)null,
            worldId = "wrld_calendar",
            accessType,
            region = "us",
            category = "hangout",
            languages = new[] { "eng" },
            platforms = new[] { "standalonewindows" },
            tags = Array.Empty<string>(),
            visibility,
            notifyMembers = false,
            publishToVRChat = true,
            publishToDiscord = false,
            postToChannel = false,
            autoOpen = false,
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

    private static Task<HttpResponseMessage> SwitchAsync(ApiTestHost host, string cookie, bool on) =>
        host.SendJsonAsync(HttpMethod.Put, "/api/calendar/public-feed", new { on }, cookie, Ct);

    private static bool IsTheSwitch(ModbotEvent fact) =>
        ApiTestHost.DataOf(fact).TryGetProperty("setting", out var setting) && setting.GetString() == "calendarPublicFeed";

    private static async Task<int> SwitchFactsAsync(ApiTestHost host) =>
        (await host.FactsAsync(FactType.SettingsChanged, "settings", Ct)).Count(IsTheSwitch);

    [Fact]
    public void ItIsOffOnANewInstall() =>
        Assert.False(new Modbot.Core.Data.Entities.Settings().CalendarPublicFeed);

    [Fact]
    public async Task TheAddressIsNotFoundWhileTheSwitchIsOff()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        await CreateAsync(host, manager, Body(host, "Movie night"));

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(FeedPath, Ct)).StatusCode);

        var view = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/calendar/public-feed", null, manager, Ct), Ct);
        Assert.False(view.GetProperty("on").GetBoolean());
        Assert.Equal(FeedPath, view.GetProperty("path").GetString());
        Assert.Equal(Address + FeedPath, view.GetProperty("url").GetString());
    }

    [Fact]
    public async Task TheSwitchNeedsManageCalendar_AndEveryChangeIsAFact()
    {
        await using var host = await StartAsync();
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var before = await SwitchFactsAsync(host);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, "/api/calendar/public-feed", null, viewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SwitchAsync(host, viewer, on: true)).StatusCode);

        var on = await SwitchAsync(host, manager, on: true);
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True((await ApiTestHost.BodyOf(on, Ct)).GetProperty("on").GetBoolean());

        var fact = (await host.FactsAsync(FactType.SettingsChanged, "settings", Ct))
            .First(IsTheSwitch);
        var data = ApiTestHost.DataOf(fact);
        Assert.False(data.GetProperty("before").GetBoolean());
        Assert.True(data.GetProperty("after").GetBoolean());

        var served = await host.Client.GetAsync(FeedPath, Ct);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("text/calendar", served.Content.Headers.ContentType?.MediaType);

        // The same answer again changes nothing and says nothing.
        Assert.Equal(HttpStatusCode.OK, (await SwitchAsync(host, manager, on: true)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SwitchAsync(host, manager, on: false)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(FeedPath, Ct)).StatusCode);

        Assert.Equal(before + 2, await SwitchFactsAsync(host));
    }

    [Fact]
    public async Task OnlyPublicEventsAreListed_NeverMembersOnlyDraftsOrDeletedOnes()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var open = await CreateAsync(host, manager, Body(host, "Open movie night"));
        var members = await CreateAsync(host, manager, Body(host, "Members' meeting", visibility: "group"));
        var draft = await CreateAsync(host, manager, Body(host, "Draft night", draft: true));
        var deleted = await CreateAsync(host, manager, Body(host, "Deleted night"));

        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(HttpMethod.Delete, $"/api/calendar/events/{deleted}", null, manager, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SwitchAsync(host, manager, on: true)).StatusCode);

        var feed = await (await host.Client.GetAsync(FeedPath, Ct)).Content.ReadAsStringAsync(Ct);

        Assert.StartsWith("BEGIN:VCALENDAR\r\n", feed, StringComparison.Ordinal);
        Assert.Contains($"UID:{open:D}@modbot", feed, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Open movie night", feed, StringComparison.Ordinal);

        Assert.DoesNotContain(members.ToString("D"), feed, StringComparison.Ordinal);
        Assert.DoesNotContain(draft.ToString("D"), feed, StringComparison.Ordinal);
        Assert.DoesNotContain(deleted.ToString("D"), feed, StringComparison.Ordinal);
        Assert.DoesNotContain("Members' meeting", feed, StringComparison.Ordinal);

        // Nothing points at Modbot's own calendar page, which asks for a sign-in.
        Assert.DoesNotContain("/calendar?event=", feed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJoinLinkIsOnlyForAnEventAnyoneCanJoin()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var anyone = await CreateAsync(host, manager, Body(host, "Anyone night", accessType: "public"));
        var friends = await CreateAsync(host, manager, Body(host, "Friends night", accessType: "plus"));
        var members = await CreateAsync(host, manager, Body(host, "Members night", accessType: "members"));

        Assert.Equal(HttpStatusCode.OK, (await SwitchAsync(host, manager, on: true)).StatusCode);

        var feed = await (await host.Client.GetAsync(FeedPath, Ct)).Content.ReadAsStringAsync(Ct);

        Assert.Contains($"URL:{Address}/api/calendar/join/{anyone:D}", feed, StringComparison.Ordinal);
        Assert.DoesNotContain($"/api/calendar/join/{friends:D}", feed, StringComparison.Ordinal);
        Assert.DoesNotContain($"/api/calendar/join/{members:D}", feed, StringComparison.Ordinal);

        // All three are visible to everyone, so all three are listed.
        Assert.Contains($"UID:{friends:D}@modbot", feed, StringComparison.Ordinal);
        Assert.Contains($"UID:{members:D}@modbot", feed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACalendarAppThatHasTheFeedGetsNotModified()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);
        await CreateAsync(host, manager, Body(host, "Movie night"));
        Assert.Equal(HttpStatusCode.OK, (await SwitchAsync(host, manager, on: true)).StatusCode);

        var first = await host.Client.GetAsync(FeedPath, Ct);
        var tag = first.Headers.ETag?.Tag;
        Assert.NotNull(tag);
        Assert.Contains("max-age=", first.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);

        using var again = new HttpRequestMessage(HttpMethod.Get, FeedPath);
        again.Headers.TryAddWithoutValidation("If-None-Match", tag);

        Assert.Equal(HttpStatusCode.NotModified, (await host.Client.SendAsync(again, Ct)).StatusCode);
    }
}
