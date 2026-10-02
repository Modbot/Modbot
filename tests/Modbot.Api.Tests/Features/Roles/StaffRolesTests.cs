using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Roles;

/// <summary>
/// Staff roles from Discord, the API (design 2026-10-02 §2, §8, §9): both Manage roles and Manage
/// users; never an Administrator role, a role at or above you, @everyone or a bot's role; one
/// Discord role per mapping and one Discord role for a both-ways role; the preview changes nothing;
/// and a role that follows Discord cannot be changed by hand on the users page.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class StaffRolesTests
{
    private const string Path = "/api/staff-roles";
    private const string Guild = "700";
    private const string Staff = "801";
    private const string Managed = "802";
    private const string Events = "803";
    private const string Member = "5001";

    /// <summary>Enough to map Moderator: both permissions, and everything Moderator allows.</summary>
    private const ModbotPermissions Mapper =
        ModbotPermissions.ManageRoles | ModbotPermissions.ManageUsers | BuiltInRoles.ModeratorPermissions;

    private readonly PostgresFixture _db;

    public StaffRolesTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Mapping(string discordRoleId, Guid roleId, string direction = "discord")
        => new { discordRoleId, roleId, direction };

    private static async Task ServerAsync(ApiTestHost host, bool on = false)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var settings = await db.GetSettingsAsync(Ct);
        settings.DiscordGuildId = Guild;
        settings.DiscordStaffRolesOn = on;

        db.DiscordServers.Add(new DiscordServer { GuildId = Guild, Name = "The server", BotCanManageRoles = true });
        db.DiscordRoles.Add(new DiscordRole { RoleId = Guild, GuildId = Guild, Name = "@everyone", Everyone = true });
        db.DiscordRoles.Add(new DiscordRole { RoleId = Staff, GuildId = Guild, Name = "Staff", BotCanAssign = true });
        db.DiscordRoles.Add(new DiscordRole { RoleId = Managed, GuildId = Guild, Name = "Booster", Managed = true });
        db.DiscordRoles.Add(new DiscordRole { RoleId = Events, GuildId = Guild, Name = "Events", BotCanAssign = true });

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>A staff account that proved this Discord account, and is in the server holding these roles.</summary>
    private static async Task<ModbotUser> StaffMemberAsync(ApiTestHost host, string discordUserId, params string[] roles)
    {
        var user = await host.CreateUserAsync($"s_{Guid.NewGuid():N}"[..20], TestAccounts.Password, ModbotPermissions.None, Ct);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(
            u => u.SetProperty(x => x.DiscordUserId, discordUserId).SetProperty(x => x.DiscordVerifiedAt, DateTimeOffset.UnixEpoch),
            Ct);

        await db.DiscordMembers.Where(m => m.UserId == discordUserId).ExecuteDeleteAsync(Ct);
        db.DiscordMembers.Add(new DiscordMember
        {
            GuildId = Guild,
            UserId = discordUserId,
            Username = "member" + discordUserId,
            DisplayName = "Member " + discordUserId,
            Roles = JsonSerializer.Serialize(roles),
        });

        await db.SaveChangesAsync(Ct);
        return user;
    }

    [Fact]
    public async Task ItNeedsBothManageRolesAndManageUsers()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);

        var (_, roles) = await host.SignedInAsync(ModbotPermissions.ManageRoles, Ct);
        var (_, users) = await host.SignedInAsync(ModbotPermissions.ManageUsers, Ct);
        var (_, both) = await host.SignedInAsync(ModbotPermissions.ManageRoles | ModbotPermissions.ManageUsers, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Path, null, roles, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Path, null, users, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Get, Path, null, both, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheSwitchIsOffToBeginWith()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(Mapper, Ct);

        var view = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct);

        Assert.False(view.GetProperty("on").GetBoolean());
        Assert.Equal(0, view.GetProperty("mappings").GetArrayLength());
    }

    [Fact]
    public async Task AnAdministratorRoleCanNeverBeMapped()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        await ServerAsync(host);

        var response = await host.SendJsonAsync(HttpMethod.Post, Path, Mapping(Staff, BuiltInRoles.AdministratorId), cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Administrator", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARoleAtYourOwnRankCannotBeMapped()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(Mapper, Ct);
        await ServerAsync(host);

        Guid own;
        using (var scope = host.Services.CreateScope())
            own = await TestAccounts.RoleForAsync(scope.ServiceProvider.GetRequiredService<ModbotContext>(), Mapper, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, Path, Mapping(Staff, own), cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ARoleAllowingWhatYouLackCannotBeMapped()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageRoles | ModbotPermissions.ManageUsers, Ct);
        await ServerAsync(host);

        var response = await host.SendJsonAsync(HttpMethod.Post, Path, Mapping(Staff, BuiltInRoles.ModeratorId), cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(Guild)]
    [InlineData(Managed)]
    [InlineData("999")]
    public async Task EveryoneABotsRoleAndAnUnknownRoleCannotBeMapped(string discordRoleId)
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(Mapper, Ct);
        await ServerAsync(host);

        var response = await host.SendJsonAsync(HttpMethod.Post, Path, Mapping(discordRoleId, BuiltInRoles.ModeratorId), cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ADiscordRoleGivesOnlyOneModbotRole()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(Mapper, Ct);
        await ServerAsync(host);

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Post, Path, Mapping(Staff, BuiltInRoles.ModeratorId), cookie, Ct)).StatusCode);

        var again = await host.SendJsonAsync(HttpMethod.Post, Path, Mapping(Staff, BuiltInRoles.ViewerId), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

        // Two Discord roles may give the same Modbot role.
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Post, Path, Mapping(Events, BuiltInRoles.ModeratorId), cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task ABothWaysRoleHasOnlyOneDiscordRole()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(Mapper, Ct);
        await ServerAsync(host);

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Post, Path, Mapping(Staff, BuiltInRoles.ModeratorId, "both"), cookie, Ct)).StatusCode);

        var second = await host.SendJsonAsync(HttpMethod.Post, Path, Mapping(Events, BuiltInRoles.ModeratorId), cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task ThePreviewListsWhoWouldChangeAndChangesNothing()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(Mapper, Ct);
        await ServerAsync(host);
        var staff = await StaffMemberAsync(host, Member, Staff);

        var response = await host.SendJsonAsync(
            HttpMethod.Post,
            Path + "/preview",
            new { mappings = new[] { new { id = (Guid?)null, discordRoleId = Staff, roleId = BuiltInRoles.ModeratorId, direction = "discord" } } },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await ApiTestHost.BodyOf(response, Ct);
        var change = Assert.Single(preview.GetProperty("changes").EnumerateArray());
        Assert.Equal("give", change.GetProperty("what").GetString());
        Assert.Equal(staff.Id, change.GetProperty("userId").GetGuid());

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        Assert.False(await db.DiscordStaffRoles.AnyAsync(Ct));
        Assert.False(await db.UserRoles.AnyAsync(r => r.UserId == staff.Id, Ct));
        Assert.Empty(await host.FactsAsync(FactType.UserRolesChanged, staff.Id.ToString(), Ct));
    }

    [Fact]
    public async Task SavingAndRemovingAMappingAreRecordedAndRemovingTakesNothingAway()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (me, cookie) = await host.SignedInAsync(Mapper, Ct);
        await ServerAsync(host);
        var staff = await StaffMemberAsync(host, Member, Staff);

        var saved = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Post, Path, Mapping(Staff, BuiltInRoles.ModeratorId), cookie, Ct), Ct);
        var id = saved.GetProperty("mappings")[0].GetProperty("id").GetGuid();

        var mapped = Assert.Single(await host.FactsAsync(FactType.StaffRoleMapped, BuiltInRoles.ModeratorId.ToString(), Ct));
        Assert.Equal(me.Id.ToString(), mapped.ActorId);

        // As the pass would have left it.
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.UserRoles.Add(new ModbotUserRole { UserId = staff.Id, RoleId = BuiltInRoles.ModeratorId, FromDiscord = true });
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Delete, $"{Path}/{id}", null, cookie, Ct)).StatusCode);
        Assert.Single(await host.FactsAsync(FactType.StaffRoleUnmapped, BuiltInRoles.ModeratorId.ToString(), Ct));

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var held = await db.UserRoles.AsNoTracking().SingleAsync(r => r.UserId == staff.Id && r.RoleId == BuiltInRoles.ModeratorId, Ct);
            Assert.False(held.FromDiscord);
        }
    }

    [Fact]
    public async Task ARoleThatFollowsDiscordCannotBeChangedByHand()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(Mapper, Ct);
        await ServerAsync(host, on: true);
        var staff = await StaffMemberAsync(host, Member);

        await host.SendJsonAsync(HttpMethod.Post, Path, Mapping(Staff, BuiltInRoles.ModeratorId), cookie, Ct);

        var list = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/users", null, cookie, Ct), Ct);
        var row = list.EnumerateArray().Single(u => u.GetProperty("id").GetGuid() == staff.Id);
        Assert.Contains(row.GetProperty("rolesFromDiscord").EnumerateArray(), r => r.GetGuid() == BuiltInRoles.ModeratorId);

        var response = await host.SendJsonAsync(
            HttpMethod.Put, $"/api/users/{staff.Id}/roles", new { roleIds = new[] { BuiltInRoles.ModeratorId } }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("follows a Discord role", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // Viewer is not mapped, so it is still the users page's to give.
        Assert.Equal(
            HttpStatusCode.OK,
            (await host.SendJsonAsync(HttpMethod.Put, $"/api/users/{staff.Id}/roles", new { roleIds = new[] { BuiltInRoles.ViewerId } }, cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task ABothWaysRoleTheBotCanGiveCanBeChangedByHand()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(Mapper, Ct);
        await ServerAsync(host, on: true);
        var staff = await StaffMemberAsync(host, Member);

        await host.SendJsonAsync(HttpMethod.Post, Path, Mapping(Staff, BuiltInRoles.ModeratorId, "both"), cookie, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Put, $"/api/users/{staff.Id}/roles", new { roleIds = new[] { BuiltInRoles.ModeratorId } }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TurningTheSwitchOnIsRecorded()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(Mapper, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Put, Path + "/on", new { on = true }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ApiTestHost.BodyOf(response, Ct)).GetProperty("on").GetBoolean());

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        Assert.True(await db.Events.AnyAsync(e => e.Type == FactType.SettingsChanged, Ct));
    }

    [Fact]
    public async Task ApplyWithNoBotSaysSo()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(Mapper, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, Path + "/apply", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((await ApiTestHost.BodyOf(response, Ct)).GetProperty("problem").GetString()));
    }
}
