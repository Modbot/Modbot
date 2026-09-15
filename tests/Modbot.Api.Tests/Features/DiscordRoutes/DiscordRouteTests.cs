using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.DiscordRoutes;
using Modbot.Api.Features.Health;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Tests.Features.DiscordRoutes;

/// <summary>
/// The channels events are sent to: listed with what they can be set to, created, changed and
/// deleted only by somebody who can change settings, with every change on the record.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordRouteTests
{
    private const string Channel = "101";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _db;

    public DiscordRouteTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ReadSurfaceTestHost> StartAsync(PostgresFixture db)
    {
        var host = await ReadSurfaceTestHost.StartAsync(db);
        await host.ResetAsync(Ct);
        return host;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }

    [Fact]
    public async Task TheList_OffersEveryEventGroup_TheGroupsRoles_AndModbotsRoles()
    {
        await using var host = await StartAsync(_db);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var settings = await db.GetSettingsAsync(Ct);
            settings.GroupInfoSnapshot = new GroupInfoSnapshot(
                "Test", "TEST", "0001", null, null, "usr_owner", null, null, false, 3, 0,
                [
                    new GroupRoleSnapshot("grol_member", "Member", null, 2, false, false, true, true, []),
                    new GroupRoleSnapshot("grol_mod", "Moderator", null, 1, true, false, false, false, []),
                ]).ToJson();
            await db.SaveChangesAsync(Ct);
        }

        var body = await host.GetJsonAsync<DiscordRoutesResponse>("/api/discord/routes", cookie, Ct);

        Assert.Empty(body.Routes);
        Assert.Equal(DiscordEventTypes.Groups.Select(g => g.Name), body.EventGroups.Select(g => g.Name));

        var moderation = body.EventGroups.First();
        Assert.Equal(DiscordEventTypes.Moderation, moderation.Name);
        Assert.Contains(moderation.Types, t => t.Type == FactType.MemberBanned && t.Label == "Banned");

        var offered = body.EventGroups.SelectMany(g => g.Types).Select(t => t.Type).ToList();
        Assert.DoesNotContain(FactType.Login, offered);
        Assert.DoesNotContain(FactType.ResetLinkCreated, offered);

        Assert.Equal(["grol_mod", "grol_member"], body.VRChatRoles.Select(r => r.Id));
        Assert.Equal("Moderator", body.VRChatRoles[0].Name);
        Assert.NotEmpty(body.ModbotRoles);
    }

    [Fact]
    public async Task ARoute_IsCreatedChangedAndDeleted_AndEachChangeIsRecorded()
    {
        await using var host = await StartAsync(_db);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var role = Guid.NewGuid();

        var created = await ReadAsync<DiscordRouteView>(await host.PostJsonAsync("/api/discord/routes", new
        {
            name = "  Staff log  ",
            channelId = " " + Channel + " ",
            eventTypes = new[] { FactType.MemberKicked, FactType.MemberBanned, FactType.Login, FactType.MemberBanned },
            subjectIds = new[] { "usr_a", " usr_a", "" },
            actorAutomatic = true,
            actorModbotRoleIds = new[] { role },
        }, cookie, Ct));

        Assert.Equal("Staff log", created.Name);
        Assert.Equal(Channel, created.ChannelId);
        Assert.True(created.Enabled);
        Assert.Equal([FactType.MemberBanned, FactType.MemberKicked], created.EventTypes);
        Assert.Equal(["usr_a"], created.SubjectIds);
        Assert.True(created.ActorAutomatic);
        Assert.Equal([role], created.ActorModbotRoleIds);

        // Fields left out stay as they were; a list sent empty clears its filter.
        var changed = await ReadAsync<DiscordRouteView>(await host.PutJsonAsync($"/api/discord/routes/{created.Id}", new
        {
            enabled = false,
            subjectIds = Array.Empty<string>(),
            actorVRChatRoleIds = new[] { "grol_mod" },
        }, cookie, Ct));

        Assert.False(changed.Enabled);
        Assert.Equal("Staff log", changed.Name);
        Assert.Equal([FactType.MemberBanned, FactType.MemberKicked], changed.EventTypes);
        Assert.Empty(changed.SubjectIds);
        Assert.Equal(["grol_mod"], changed.ActorVRChatRoleIds);
        Assert.True(changed.ActorAutomatic);

        var second = await ReadAsync<DiscordRouteView>(await host.PostJsonAsync("/api/discord/routes", new
        {
            channelId = "102",
            eventTypes = new[] { FactType.MemberJoined },
        }, cookie, Ct));

        var listed = await host.GetJsonAsync<DiscordRoutesResponse>("/api/discord/routes", cookie, Ct);
        Assert.Equal([created.Id, second.Id], listed.Routes.Select(r => r.Id));

        var deleted = await host.Client.SendAsync(Request(HttpMethod.Delete, $"/api/discord/routes/{created.Id}", cookie), Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        listed = await host.GetJsonAsync<DiscordRoutesResponse>("/api/discord/routes", cookie, Ct);
        Assert.Equal([second.Id], listed.Routes.Select(r => r.Id));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var facts = await db.Events.AsNoTracking()
            .Where(e => e.Type == FactType.SettingsChanged)
            .OrderBy(e => e.Id)
            .ToListAsync(Ct);

        Assert.Equal(4, facts.Count);
        var actions = facts.Select(f => JsonDocument.Parse(f.Data).RootElement.GetProperty("action").GetString()).ToList();
        Assert.Equal(["create", "change", "create", "delete"], actions);
        Assert.All(facts, f => Assert.Equal(FactPlatform.Modbot, f.ActorPlatform));
    }

    [Theory]
    [InlineData(null, new[] { FactType.MemberBanned }, "Pick a channel.")]
    [InlineData("  ", new[] { FactType.MemberBanned }, "Pick a channel.")]
    [InlineData(Channel, new string[0], "Pick at least one event.")]
    [InlineData(Channel, new[] { FactType.Login, FactType.ResetLinkCreated }, "Pick at least one event.")]
    public async Task ARouteWithoutAChannelOrASendableEvent_IsRefused(string? channel, string[] types, string error)
    {
        await using var host = await StartAsync(_db);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.PostJsonAsync("/api/discord/routes", new { channelId = channel, eventTypes = types }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(error, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingAMissingRoute_IsNotFound()
    {
        await using var host = await StartAsync(_db);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.PutJsonAsync($"/api/discord/routes/{Guid.NewGuid()}", new { enabled = false }, cookie, Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PeopleSearch_FindsStoredProfilesByNameOrId()
    {
        await using var host = await StartAsync(_db);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var at = host.Clock.UtcNow;
            db.VRChatUsers.AddRange(
                new VRChatUser { UserId = "usr_alice", DisplayName = "Alice_Wonder", FirstSeenAt = at, LastSeenAt = at, LastRefreshedAt = at },
                new VRChatUser { UserId = "usr_bob", DisplayName = "Bob", FirstSeenAt = at, LastSeenAt = at, LastRefreshedAt = at },
                new VRChatUser { UserId = "8JoV9XEdpo", DisplayName = "Legacy", FirstSeenAt = at, LastSeenAt = at, LastRefreshedAt = at });
            await db.SaveChangesAsync(Ct);
        }

        var byName = await host.GetJsonAsync<DiscordRoutePeopleResponse>("/api/discord/routes/people?search=alice_", cookie, Ct);
        Assert.Equal(["usr_alice"], byName.People.Select(p => p.Id));

        var byId = await host.GetJsonAsync<DiscordRoutePeopleResponse>("/api/discord/routes/people?search=8JoV9XEdpo", cookie, Ct);
        Assert.Equal("Legacy", Assert.Single(byId.People).Name);

        var nothing = await host.GetJsonAsync<DiscordRoutePeopleResponse>("/api/discord/routes/people", cookie, Ct);
        Assert.Empty(nothing.People);
    }

    [Fact]
    public async Task OnlySomebodyWhoCanChangeSettings_MayReadOrChangeRoutes()
    {
        await using var host = await StartAsync(_db);
        var body = new { channelId = Channel, eventTypes = new[] { FactType.MemberBanned } };

        var viewer = await host.SignedInAsync(ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewMembers | ModbotPermissions.ViewOperationalLog, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/discord/routes", viewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/discord/routes/people?search=a", viewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostJsonAsync("/api/discord/routes", body, viewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PutJsonAsync($"/api/discord/routes/{Guid.NewGuid()}", body, viewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await host.Client.SendAsync(Request(HttpMethod.Delete, $"/api/discord/routes/{Guid.NewGuid()}", viewer), Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/api/discord/routes", Ct)).StatusCode);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            Assert.Empty(await db.DiscordEventRoutes.ToListAsync(Ct));
        }

        var administrator = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync("/api/discord/routes", body, administrator, Ct)).StatusCode);
    }

    [Fact]
    public async Task Health_ListsChannelsThatCannotBePostedIn()
    {
        await using var host = await StartAsync(_db);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, Ct);
        var at = host.Clock.UtcNow;

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

            db.DiscordChannels.AddRange(
                new DiscordChannel
                {
                    ChannelId = "201", GuildId = "1", Name = "mod-log", Type = DiscordChannelTypes.Text,
                    BotCanView = true, BotCanSend = true, BotCanEmbedLinks = true, FirstSeenAt = at, UpdatedAt = at,
                },
                new DiscordChannel
                {
                    ChannelId = "202", GuildId = "1", Name = "no-embeds", Type = DiscordChannelTypes.Text,
                    BotCanView = true, BotCanSend = true, BotCanEmbedLinks = false, FirstSeenAt = at, UpdatedAt = at,
                },
                new DiscordChannel
                {
                    ChannelId = "203", GuildId = "1", Name = "gone", Type = DiscordChannelTypes.Text,
                    BotCanView = true, BotCanSend = true, BotCanEmbedLinks = true, FirstSeenAt = at, UpdatedAt = at, RemovedAt = at,
                },
                new DiscordChannel
                {
                    ChannelId = "205", GuildId = "1", Name = "switched-off", Type = DiscordChannelTypes.Text,
                    FirstSeenAt = at, UpdatedAt = at,
                });

            db.DiscordEventRoutes.AddRange(
                Route("201", 0, true),
                Route("202", 1, true),
                Route("203", 2, true),
                Route("204", 3, true),
                Route("205", 4, false));

            db.DiscordEventChannels.Add(new DiscordEventChannel
            {
                ChannelId = "204", PostedThrough = 10, LastError = "Missing Access", LastErrorAt = at,
            });

            await db.SaveChangesAsync(Ct);
        }

        var health = await host.GetJsonAsync<SyncHealth>("/api/health/sync", cookie, Ct);
        var problems = health.DiscordChannelProblems!;

        Assert.Equal(["202", "203", "204"], problems.Select(p => p.ChannelId));
        Assert.Equal(["Embed Links"], problems[0].Missing);
        Assert.Equal("no-embeds", problems[0].Name);
        Assert.True(problems[1].Removed);
        Assert.Equal("Missing Access", problems[2].LastError);
        Assert.Null(problems[2].Name);
    }

    private static DiscordEventRoute Route(string channel, int position, bool enabled) => new()
    {
        Id = Guid.NewGuid(),
        ChannelId = channel,
        Enabled = enabled,
        EventTypes = [FactType.MemberBanned],
        Position = position,
    };

    private static HttpRequestMessage Request(HttpMethod method, string path, string cookie)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        return request;
    }
}
