using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.DiscordRoleMenus;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.DiscordRoleMenus;

/// <summary>
/// Role menus in settings: saved only by somebody who can change settings, refusing roles a menu may
/// not give (dangerous, bot-owned, @everyone, unread, missing), duplicates and more than 25 roles,
/// keeping a posted menu in its channel, and not forgetting a menu whose message could not be removed.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordRoleMenuTests
{
    private const string Guild = "700";
    private const string Channel = "900";
    private const string Red = "811";
    private const string Admin = "812";
    private const string Bot = "813";
    private const string Unread = "814";

    private const long Community = (1L << 10) | (1L << 11);
    private const long Administrator = 1L << 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _db;

    public DiscordRoleMenuTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeRoleMenuActions : IDiscordRoleMenuActions
    {
        public RoleMenuSiteOutcome PostResult { get; set; } = RoleMenuSiteOutcome.Ok("5555");

        public RoleMenuSiteOutcome DeleteResult { get; set; } = RoleMenuSiteOutcome.Ok();

        public List<string> Deleted { get; } = [];

        public Task<RoleMenuSiteOutcome> PostAsync(DiscordRoleMenu menu, CancellationToken ct = default)
            => Task.FromResult(PostResult);

        public Task<RoleMenuSiteOutcome> DeleteAsync(string channelId, string messageId, CancellationToken ct = default)
        {
            if (DeleteResult.Done)
                Deleted.Add(messageId);

            return Task.FromResult(DeleteResult);
        }
    }

    private static async Task<(ReadSurfaceTestHost Host, FakeRoleMenuActions Discord, string Cookie)> StartAsync(PostgresFixture db)
    {
        var discord = new FakeRoleMenuActions();
        var host = await ReadSurfaceTestHost.StartAsync(db, configure: s => s.AddSingleton<IDiscordRoleMenuActions>(discord));
        await host.ResetAsync(Ct);

        using (var scope = host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var settings = await context.GetSettingsAsync(Ct);
            settings.DiscordGuildId = Guild;

            var at = host.Clock.UtcNow;

            context.DiscordRoles.AddRange(
                Role(Red, "Red", Community, at),
                Role(Admin, "Boss", Community | Administrator, at),
                Role(Bot, "Some bot", Community, at, managed: true),
                Role(Unread, "New", null, at),
                Role(Guild, "@everyone", 0, at, everyone: true));

            for (var i = 0; i < 26; i++)
                context.DiscordRoles.Add(Role((2000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), $"Colour {i}", Community, at));

            await context.SaveChangesAsync(Ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        return (host, discord, cookie);
    }

    private static DiscordRole Role(string id, string name, long? permissions, DateTimeOffset at, bool managed = false, bool everyone = false) => new()
    {
        RoleId = id,
        GuildId = Guild,
        Name = name,
        Permissions = permissions,
        Managed = managed,
        Everyone = everyone,
        BotCanAssign = !managed && !everyone,
        FirstSeenAt = at,
        UpdatedAt = at,
    };

    private static object Body(IEnumerable<object>? roles = null, string channel = Channel, string style = "buttons") => new
    {
        name = "Colours",
        channelId = channel,
        text = "Pick a colour",
        style,
        singleChoice = false,
        roles = (roles ?? [new { roleId = Red, label = "Red", emoji = (string?)null }]).ToArray(),
    };

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("error").GetString() ?? string.Empty;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string cookie)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        return request;
    }

    [Fact]
    public async Task AMenu_IsSavedListedAndNotPostedUntilAsked()
    {
        var (host, _, cookie) = await StartAsync(_db);
        await using var _ = host;

        var created = await ReadAsync<DiscordRoleMenuView>(await host.PostJsonAsync("/api/settings/discord-role-menus", Body(), cookie, Ct));

        Assert.Equal("Colours", created.Name);
        Assert.Null(created.MessageId);
        Assert.Null(created.PostedAt);
        Assert.Equal([Red], created.Roles.Select(r => r.RoleId));

        var list = await host.GetJsonAsync<DiscordRoleMenusResponse>("/api/settings/discord-role-menus", cookie, Ct);
        Assert.Equal([created.Id], list.Menus.Select(m => m.Id));
    }

    [Fact]
    public async Task ARole_WithADangerousPermission_ABotOwnedRole_Everyone_AnUnreadRole_AndAMissingRole_AreRefused()
    {
        var (host, _, cookie) = await StartAsync(_db);
        await using var _ = host;

        foreach (var role in new[] { Admin, Bot, Guild, Unread, "99999" })
        {
            var response = await host.PostJsonAsync(
                "/api/settings/discord-role-menus",
                Body([new { roleId = role, label = "Role", emoji = (string?)null }]),
                cookie,
                Ct);

            Assert.False(string.IsNullOrWhiteSpace(await ErrorAsync(response)), role);
        }

        var dangerous = await ErrorAsync(await host.PostJsonAsync(
            "/api/settings/discord-role-menus",
            Body([new { roleId = Admin, label = "Boss", emoji = (string?)null }]),
            cookie,
            Ct));
        Assert.Contains("Boss", dangerous);

        Assert.Empty((await host.GetJsonAsync<DiscordRoleMenusResponse>("/api/settings/discord-role-menus", cookie, Ct)).Menus);
    }

    [Fact]
    public async Task ARoleTwice_AndMoreThanTwentyFiveRoles_AreRefused()
    {
        var (host, _, cookie) = await StartAsync(_db);
        await using var _ = host;

        var twice = await host.PostJsonAsync(
            "/api/settings/discord-role-menus",
            Body([new { roleId = Red, label = "Red", emoji = (string?)null }, new { roleId = Red, label = "Red again", emoji = (string?)null }]),
            cookie,
            Ct);
        await ErrorAsync(twice);

        var tooMany = Enumerable.Range(0, 26)
            .Select(i => (object)new { roleId = (2000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), label = $"Colour {i}", emoji = (string?)null })
            .ToList();
        var response = await host.PostJsonAsync("/api/settings/discord-role-menus", Body(tooMany), cookie, Ct);
        Assert.Contains("25", await ErrorAsync(response));

        var exactly25 = await host.PostJsonAsync("/api/settings/discord-role-menus", Body(tooMany.Take(25).ToList()), cookie, Ct);
        Assert.True(exactly25.IsSuccessStatusCode);
    }

    [Fact]
    public async Task AMenu_ThatIsPosted_CannotMoveToAnotherChannel_ButCanChangeItsRoles()
    {
        var (host, _, cookie) = await StartAsync(_db);
        await using var _ = host;

        var created = await ReadAsync<DiscordRoleMenuView>(await host.PostJsonAsync("/api/settings/discord-role-menus", Body(), cookie, Ct));
        var posted = await ReadAsync<DiscordRoleMenuView>(await host.PostJsonAsync($"/api/settings/discord-role-menus/{created.Id}/post", null, cookie, Ct));
        Assert.Equal("5555", posted.MessageId);
        Assert.NotNull(posted.PostedAt);

        var moved = await host.PutJsonAsync($"/api/settings/discord-role-menus/{created.Id}", Body(channel: "901"), cookie, Ct);
        Assert.Contains("channel", await ErrorAsync(moved), StringComparison.OrdinalIgnoreCase);

        host.Clock.Advance(TimeSpan.FromMinutes(5));
        var changed = await ReadAsync<DiscordRoleMenuView>(await host.PutJsonAsync(
            $"/api/settings/discord-role-menus/{created.Id}",
            Body([new { roleId = "2000", label = "Colour 0", emoji = (string?)null }]),
            cookie,
            Ct));
        Assert.Equal(["2000"], changed.Roles.Select(r => r.RoleId));
        Assert.True(changed.ChangedSincePosted);
    }

    [Fact]
    public async Task PostingAMenu_WhoseRoleGainedADangerousPermission_IsRefused()
    {
        var (host, discord, cookie) = await StartAsync(_db);
        await using var _ = host;

        var created = await ReadAsync<DiscordRoleMenuView>(await host.PostJsonAsync("/api/settings/discord-role-menus", Body(), cookie, Ct));

        using (var scope = host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            await context.DiscordRoles.Where(r => r.RoleId == Red).ExecuteUpdateAsync(u => u.SetProperty(r => r.Permissions, Community | Administrator), Ct);
        }

        var response = await host.PostJsonAsync($"/api/settings/discord-role-menus/{created.Id}/post", null, cookie, Ct);

        Assert.Contains("Red", await ErrorAsync(response));
        Assert.Empty(discord.Deleted);
    }

    [Fact]
    public async Task PostingWhileTheBotIsOffline_Answers503_AndSavesNothingAsPosted()
    {
        var (host, discord, cookie) = await StartAsync(_db);
        await using var _ = host;
        discord.PostResult = RoleMenuSiteOutcome.Offline;

        var created = await ReadAsync<DiscordRoleMenuView>(await host.PostJsonAsync("/api/settings/discord-role-menus", Body(), cookie, Ct));
        var response = await host.PostJsonAsync($"/api/settings/discord-role-menus/{created.Id}/post", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var list = await host.GetJsonAsync<DiscordRoleMenusResponse>("/api/settings/discord-role-menus", cookie, Ct);
        Assert.Null(Assert.Single(list.Menus).MessageId);
    }

    [Fact]
    public async Task DeletingAPostedMenu_WhileTheBotIsOffline_IsRefused_AndTheMenuStays()
    {
        var (host, discord, cookie) = await StartAsync(_db);
        await using var _ = host;

        var created = await ReadAsync<DiscordRoleMenuView>(await host.PostJsonAsync("/api/settings/discord-role-menus", Body(), cookie, Ct));
        await ReadAsync<DiscordRoleMenuView>(await host.PostJsonAsync($"/api/settings/discord-role-menus/{created.Id}/post", null, cookie, Ct));

        discord.DeleteResult = RoleMenuSiteOutcome.Offline;
        var refused = await host.Client.SendAsync(Request(HttpMethod.Delete, $"/api/settings/discord-role-menus/{created.Id}", cookie), Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Single((await host.GetJsonAsync<DiscordRoleMenusResponse>("/api/settings/discord-role-menus", cookie, Ct)).Menus);

        discord.DeleteResult = RoleMenuSiteOutcome.Ok();
        var deleted = await host.Client.SendAsync(Request(HttpMethod.Delete, $"/api/settings/discord-role-menus/{created.Id}", cookie), Ct);
        Assert.True(deleted.IsSuccessStatusCode);
        Assert.Equal(["5555"], discord.Deleted);
        Assert.Empty((await host.GetJsonAsync<DiscordRoleMenusResponse>("/api/settings/discord-role-menus", cookie, Ct)).Menus);
    }

    [Fact]
    public async Task SomebodyWithoutTheSettingsPermission_IsTurnedAway()
    {
        var (host, _, _) = await StartAsync(_db);
        await using var _ = host;
        var viewer = await host.SignedInAsync(ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewMembers, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/settings/discord-role-menus", viewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostJsonAsync("/api/settings/discord-role-menus", Body(), viewer, Ct)).StatusCode);
    }
}
