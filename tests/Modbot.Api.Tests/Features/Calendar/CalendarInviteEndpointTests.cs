using System.Globalization;
using System.Net;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// Calendar auto-invite design §9: who an event invites is set by whoever may edit it, picking a list
/// needs See members and See profiles as the Lists page does, and a list an event invites cannot be
/// deleted from under it.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarInviteEndpointTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const ModbotPermissions Manager = ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar;

    private const ModbotPermissions ManagerWhoSeesLists =
        Manager | ModbotPermissions.ViewMembers | ModbotPermissions.ViewProfile;

    private async Task<ApiTestHost> StartAsync()
    {
        await using (var context = db.NewContext())
        {
            await context.CalendarEvents.IgnoreAutoIncludes().ExecuteDeleteAsync(Ct);
            await context.DiscordListRoles.ExecuteDeleteAsync(Ct);
            await context.SavedLists.ExecuteDeleteAsync(Ct);
        }

        return await ApiTestHost.StartAsync(db);
    }

    private async Task<Guid> AddListAsync(ApiTestHost host, string name = "Regulars")
    {
        var list = new SavedList
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            CreatedAt = host.Clock.UtcNow,
            UpdatedAt = host.Clock.UtcNow,
        };

        await using var context = db.NewContext();
        context.SavedLists.Add(list);
        await context.SaveChangesAsync(Ct);
        return list.Id;
    }

    private static object Body(
        ApiTestHost host,
        Guid? list = null,
        string title = "Movie night",
        bool announceInDiscord = false,
        bool postToChannel = false)
    {
        var start = host.Clock.UtcNow.AddDays(2);

        return new
        {
            title,
            description = "Bring snacks",
            startsAt = start.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            endsAt = start.AddHours(2).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            timeZone = "UTC",
            repeat = "none",
            repeatDays = Array.Empty<string>(),
            repeatUntil = (string?)null,
            worldId = "wrld_calendar",
            accessType = "members",
            region = "us",
            category = "hangout",
            languages = new[] { "eng" },
            platforms = new[] { "standalonewindows" },
            tags = Array.Empty<string>(),
            visibility = "group",
            notifyMembers = false,
            publishToVRChat = false,
            publishToDiscord = false,
            postToChannel,
            channelId = postToChannel ? "123" : null,
            autoOpen = true,
            openMinutesBefore = 10,
            draft = false,
            inviteListId = list,
            announceFirstJoinInDiscord = announceInDiscord,
            announceFirstJoinInVRChat = true,
        };
    }

    [Fact]
    public async Task PickingAListNeedsSeeMembersAndSeeProfiles()
    {
        await using var host = await StartAsync();
        var list = await AddListAsync(host);
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var (_, seesLists) = await host.SignedInAsync(ManagerWhoSeesLists, Ct);

        var refused = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", Body(host, list), manager, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var saved = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", Body(host, list), seesLists, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var body = await ApiTestHost.BodyOf(saved, Ct);
        Assert.Equal(list, body.GetProperty("inviteListId").GetGuid());
        Assert.Equal("Regulars", body.GetProperty("inviteListName").GetString());
        Assert.True(body.GetProperty("announceFirstJoinInVRChat").GetBoolean());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task TheBlueskyReplyOnFirstJoinIsKeptOnlyWhileTheEventGoesToBluesky(bool publishToBluesky, bool expected)
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        var json = System.Text.Json.JsonSerializer.SerializeToNode(Body(host))!.AsObject();
        json["publishToBluesky"] = publishToBluesky;
        json["announceFirstJoinOnBluesky"] = true;

        var saved = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", json, manager, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var body = await ApiTestHost.BodyOf(saved, Ct);
        Assert.Equal(expected, body.GetProperty("announceFirstJoinOnBluesky").GetBoolean());
    }

    [Fact]
    public async Task KeepingTheListAlreadyThereNeedsNothingMore_ChangingItDoes()
    {
        await using var host = await StartAsync();
        var list = await AddListAsync(host);
        var other = await AddListAsync(host, "Newcomers");
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var (_, seesLists) = await host.SignedInAsync(ManagerWhoSeesLists, Ct);

        var created = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", Body(host, list), seesLists, Ct);
        var id = (await ApiTestHost.BodyOf(created, Ct)).GetProperty("id").GetGuid();

        var kept = await host.SendJsonAsync(HttpMethod.Put, $"/api/calendar/events/{id}", Body(host, list, "Movie night: Alien"), manager, Ct);
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);

        var changed = await host.SendJsonAsync(HttpMethod.Put, $"/api/calendar/events/{id}", Body(host, other), manager, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, changed.StatusCode);
    }

    [Fact]
    public async Task AListThatIsGoneCannotBePicked()
    {
        await using var host = await StartAsync();
        var (_, seesLists) = await host.SignedInAsync(ManagerWhoSeesLists, Ct);

        var refused = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", Body(host, Guid.CreateVersion7()), seesLists, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task TheDiscordPostForTheFirstPersonNeedsTheChannelPost()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        var refused = await host.SendJsonAsync(
            HttpMethod.Post, "/api/calendar/events", Body(host, announceInDiscord: true), manager, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var saved = await host.SendJsonAsync(
            HttpMethod.Post, "/api/calendar/events", Body(host, announceInDiscord: true, postToChannel: true), manager, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    [Fact]
    public async Task TheChoicesLeaveListsOutWithoutSeeMembersAndSeeProfiles()
    {
        await using var host = await StartAsync();
        await AddListAsync(host);
        var (_, manager) = await host.SignedInAsync(Manager, Ct);
        var (_, seesLists) = await host.SignedInAsync(ManagerWhoSeesLists, Ct);

        var without = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, "/api/calendar/invite-choices", null, manager, Ct), Ct);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, without.GetProperty("lists").ValueKind);
        Assert.True(without.GetProperty("staff").GetArrayLength() > 0);

        var with = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, "/api/calendar/invite-choices", null, seesLists, Ct), Ct);
        Assert.Equal("Regulars", Assert.Single(with.GetProperty("lists").EnumerateArray()).GetProperty("name").GetString());
    }

    private const ModbotPermissions TemplateManager = ModbotPermissions.ViewCalendar | ModbotPermissions.ManageTemplates;

    private static object TemplateBody(Guid? list) => new
    {
        name = "Movie night",
        fields = new Dictionary<string, object?>
        {
            ["title"] = "Movie night",
            ["description"] = "Bring snacks",
            ["lengthMinutes"] = 120,
            ["repeat"] = "none",
            ["repeatDays"] = Array.Empty<string>(),
            ["category"] = "gaming",
            ["languages"] = Array.Empty<string>(),
            ["platforms"] = Array.Empty<string>(),
            ["tags"] = Array.Empty<string>(),
            ["publishToVRChat"] = false,
            ["publishToDiscord"] = true,
            ["postToChannel"] = false,
            ["autoOpen"] = false,
            ["inviteListId"] = list,
        },
    };

    [Fact]
    public async Task ATemplateManagerSeesTheListNamesAndNothingElse_ForTheTemplateEditorOnly()
    {
        await using var host = await StartAsync();
        await AddListAsync(host);
        var (_, templates) = await host.SignedInAsync(TemplateManager, Ct);

        var forTemplate = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, "/api/calendar/invite-choices?template=true", null, templates, Ct), Ct);
        var list = Assert.Single(forTemplate.GetProperty("lists").EnumerateArray());
        Assert.Equal("Regulars", list.GetProperty("name").GetString());
        Assert.Equal(["id", "name"], list.EnumerateObject().Select(p => p.Name).Order().ToArray());

        var forEvent = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, "/api/calendar/invite-choices", null, templates, Ct), Ct);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, forEvent.GetProperty("lists").ValueKind);
    }

    [Fact]
    public async Task ManageCalendarAloneStillGetsNoLists_EvenAskingForTheTemplateEditor()
    {
        await using var host = await StartAsync();
        await AddListAsync(host);
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        var answer = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, "/api/calendar/invite-choices?template=true", null, manager, Ct), Ct);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, answer.GetProperty("lists").ValueKind);
    }

    [Fact]
    public async Task ATemplateManagerSavesATemplateWithAList_ButNotOneThatIsGone()
    {
        await using var host = await StartAsync();
        var list = await AddListAsync(host);
        var (_, templates) = await host.SignedInAsync(TemplateManager, Ct);

        var saved = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/templates", TemplateBody(list), templates, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(list, (await ApiTestHost.BodyOf(saved, Ct)).GetProperty("fields").GetProperty("inviteListId").GetGuid());

        var gone = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/templates", TemplateBody(Guid.CreateVersion7()), templates, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, gone.StatusCode);
    }

    [Fact]
    public async Task WithoutTheListsRule_AnEventWithAListIsRefused_EvenForSomebodyWhoManagesTemplates()
    {
        await using var host = await StartAsync();
        var list = await AddListAsync(host);
        var (_, both) = await host.SignedInAsync(Manager | ModbotPermissions.ManageTemplates, Ct);
        var (_, manager) = await host.SignedInAsync(Manager, Ct);

        var byBoth = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", Body(host, list), both, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, byBoth.StatusCode);

        var byManager = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", Body(host, list), manager, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, byManager.StatusCode);
    }

    [Fact]
    public async Task StaffTurnEventInvitesOffAndOn_OnTheirAccountPage()
    {
        await using var host = await StartAsync();
        var (_, staff) = await host.SignedInAsync(Manager, Ct);

        var off = await host.SendJsonAsync(HttpMethod.Put, "/api/auth/event-invites", new { getsEventInvites = false }, staff, Ct);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await ApiTestHost.BodyOf(off, Ct)).GetProperty("getsEventInvites").GetBoolean());

        var on = await host.SendJsonAsync(HttpMethod.Put, "/api/auth/event-invites", new { getsEventInvites = true }, staff, Ct);
        Assert.True((await ApiTestHost.BodyOf(on, Ct)).GetProperty("getsEventInvites").GetBoolean());
    }

    [Fact]
    public async Task AListAnEventInvitesCannotBeDeleted()
    {
        await using var host = await StartAsync();
        var list = await AddListAsync(host);
        var (_, seesLists) = await host.SignedInAsync(ManagerWhoSeesLists | ModbotPermissions.ManageLists, Ct);

        var created = await host.SendJsonAsync(HttpMethod.Post, "/api/calendar/events", Body(host, list), seesLists, Ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var deleted = await host.SendJsonAsync(HttpMethod.Delete, $"/api/lists/{list}", null, seesLists, Ct);
        Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.Contains("Movie night", (await ApiTestHost.BodyOf(deleted, Ct)).GetProperty("error").GetString(), StringComparison.Ordinal);
    }
}
