using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
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
    private const string Bans = "804";
    private const string Member = "5001";

    /// <summary>Enough to map Moderator: both permissions, and everything Moderator allows.</summary>
    private const ModbotPermissions Mapper =
        ModbotPermissions.ManageRoles | ModbotPermissions.ManageUsers | BuiltInRoles.ModeratorPermissions;

    private readonly PostgresFixture _db;

    public StaffRolesTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Mapping(string discordRoleId, Guid roleId, string direction = "discord")
        => new { discordRoleId, roleId, direction };

    /// <param name="memberUpdates">Whether the staff role pass last found member updates arriving.</param>
    private static async Task ServerAsync(ApiTestHost host, bool on = false, bool memberUpdates = true)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var settings = await db.GetSettingsAsync(Ct);
        settings.DiscordGuildId = Guild;
        settings.DiscordStaffRolesOn = on;

        if (memberUpdates)
            db.DiscordSyncState.Add(new DiscordSyncState { Id = 1, StaffRolesMembersCurrentAt = host.Clock.UtcNow });

        db.DiscordServers.Add(new DiscordServer { GuildId = Guild, Name = "The server", BotCanManageRoles = true });
        db.DiscordRoles.Add(new DiscordRole { RoleId = Guild, GuildId = Guild, Name = "@everyone", Everyone = true });
        db.DiscordRoles.Add(new DiscordRole { RoleId = Staff, GuildId = Guild, Name = "Staff", BotCanAssign = true, Permissions = 0 });
        db.DiscordRoles.Add(new DiscordRole { RoleId = Managed, GuildId = Guild, Name = "Booster", Managed = true });
        db.DiscordRoles.Add(new DiscordRole { RoleId = Events, GuildId = Guild, Name = "Events", BotCanAssign = true, Permissions = 0 });
        db.DiscordRoles.Add(new DiscordRole { RoleId = Bans, GuildId = Guild, Name = "Bans", BotCanAssign = true, Permissions = 1L << 2 });

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
    public async Task ADiscordRoleWithPowerOverTheServerCannotWorkBothWays()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(Mapper, Ct);
        await ServerAsync(host);

        var both = await host.SendJsonAsync(HttpMethod.Post, Path, Mapping(Bans, BuiltInRoles.ModeratorId, "both"), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);

        // Discord decides never changes anything in Discord, so it may follow such a role.
        var oneWay = await host.SendJsonAsync(HttpMethod.Post, Path, Mapping(Bans, BuiltInRoles.ModeratorId), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, oneWay.StatusCode);
    }

    [Fact]
    public async Task ADiscordRoleInAGroupRolePairCannotWorkBothWays()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(Mapper, Ct);
        await ServerAsync(host);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.DiscordRolePairs.Add(new DiscordRolePair { VRChatRoleId = "grol_staff", DiscordRoleId = Staff, Decides = RoleSyncDecides.VRChat });
            await db.SaveChangesAsync(Ct);
        }

        var response = await host.SendJsonAsync(HttpMethod.Post, Path, Mapping(Staff, BuiltInRoles.ModeratorId, "both"), cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AGroupRolePairCannotUseADiscordRoleLinkedBothWays()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(Mapper | ModbotPermissions.ManageDiscordSync, Ct);
        await ServerAsync(host);

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Post, Path, Mapping(Staff, BuiltInRoles.ModeratorId, "both"), cookie, Ct)).StatusCode);

        var pair = await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/discord-sync/pairs",
            new { vrchatRoleId = "grol_staff", discordRoleId = Staff, decides = "vrchat", enabled = true },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, pair.StatusCode);
    }

    [Fact]
    public async Task TwoBothWaysRowsForOneRoleAreRefusedByTheDatabaseToo()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        await ServerAsync(host);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        db.DiscordStaffRoles.Add(new DiscordStaffRole { DiscordRoleId = Staff, RoleId = BuiltInRoles.ModeratorId, Direction = StaffRoleDirections.Both });
        db.DiscordStaffRoles.Add(new DiscordStaffRole { DiscordRoleId = Events, RoleId = BuiltInRoles.ModeratorId, Direction = StaffRoleDirections.Both });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task ApplyIsRecordedWithWhoPressedIt()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (me, cookie) = await host.SignedInAsync(Mapper, Ct);

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Post, Path + "/apply", null, cookie, Ct)).StatusCode);

        var fact = Assert.Single(await host.FactsAsync(FactType.StaffRolesApplied, "staff-roles", Ct));
        Assert.Equal(me.Id.ToString(), fact.ActorId);
    }

    /// <summary>Apply takes away what the brake held back: only somebody who could take each of those roles by hand may press it.</summary>
    [Fact]
    public async Task ApplyIsRefusedToSomebodyWhoCouldNotTakeTheRolesAwayByHand()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        await ServerAsync(host, on: true);

        // A role above the presser, mapped by an administrator, held by hand by somebody who lacks
        // its Discord role: the pass would take it away.
        Guid high;
        using (var scope = host.Services.CreateScope())
        {
            high = await TestAccounts.RoleForAsync(
                scope.ServiceProvider.GetRequiredService<ModbotContext>(),
                Mapper | ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings,
                Ct);
        }

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Post, Path, Mapping(Staff, high), admin, Ct)).StatusCode);

        var staff = await StaffMemberAsync(host, Member);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.UserRoles.Add(new ModbotUserRole { UserId = staff.Id, RoleId = high });
            await db.SaveChangesAsync(Ct);
        }

        var (_, cookie) = await host.SignedInAsync(Mapper, Ct);
        var response = await host.SendJsonAsync(HttpMethod.Post, Path + "/apply", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await host.FactsAsync(FactType.StaffRolesApplied, "staff-roles", Ct));
    }

    /// <summary>
    /// The role Apply would take is below the presser, but the account losing it is not: an
    /// account at or above you is not yours to change, by hand or by Apply.
    /// </summary>
    [Fact]
    public async Task ApplyIsRefusedWhenAnAffectedAccountIsAboveThePresser()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(Mapper, Ct);
        await ServerAsync(host, on: true);

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Post, Path, Mapping(Staff, BuiltInRoles.ModeratorId), cookie, Ct)).StatusCode);

        var staff = await StaffMemberAsync(host, Member);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var high = await TestAccounts.RoleForAsync(db, Mapper | ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);
            db.UserRoles.Add(new ModbotUserRole { UserId = staff.Id, RoleId = BuiltInRoles.ModeratorId });
            db.UserRoles.Add(new ModbotUserRole { UserId = staff.Id, RoleId = high });
            await db.SaveChangesAsync(Ct);
        }

        var response = await host.SendJsonAsync(HttpMethod.Post, Path + "/apply", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Apply gives as well as takes: a role allowing what the presser cannot do is refused.</summary>
    [Fact]
    public async Task ApplyIsRefusedWhenItWouldGiveAPermissionThePresserLacks()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        await ServerAsync(host, on: true);

        // Below the presser (fewer permissions), but allowing Change settings, which they lack.
        Guid settingsRole;
        using (var scope = host.Services.CreateScope())
        {
            settingsRole = await TestAccounts.RoleForAsync(
                scope.ServiceProvider.GetRequiredService<ModbotContext>(), ModbotPermissions.ManageSettings | ModbotPermissions.ViewMembers, Ct);
        }

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Post, Path, Mapping(Staff, settingsRole), admin, Ct)).StatusCode);
        await StaffMemberAsync(host, Member, Staff);

        var (_, cookie) = await host.SignedInAsync(Mapper, Ct);
        var response = await host.SendJsonAsync(HttpMethod.Post, Path + "/apply", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Without member updates every link is Not set up, says why, and follows Discord on the users page.</summary>
    [Fact]
    public async Task WithoutMemberUpdatesEveryLinkIsNotSetUp()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(Mapper, Ct);
        await ServerAsync(host, on: true, memberUpdates: false);
        var staff = await StaffMemberAsync(host, Member);

        var saved = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Post, Path, Mapping(Staff, BuiltInRoles.ModeratorId, "both"), cookie, Ct), Ct);

        Assert.True(saved.GetProperty("mappings")[0].GetProperty("notSetUp").GetBoolean());
        Assert.Equal("Modbot isn't receiving member updates from Discord.", saved.GetProperty("problem").GetString());

        // A both-ways role that works could be changed by hand; while updates are missing it cannot.
        var response = await host.SendJsonAsync(
            HttpMethod.Put, $"/api/users/{staff.Id}/roles", new { roleIds = new[] { BuiltInRoles.ModeratorId } }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed class StartedBot : IDiscordBotStatus
    {
        public DateTimeOffset? StartedAt { get; set; }

        public DiscordBotSnapshot Snapshot() => new(DiscordBotState.Connecting, null, null, null, 0, false, null, 0);
    }

    private static async Task MarkAsync(ApiTestHost host, DateTimeOffset currentAt, DateTimeOffset? offAt = null)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        await db.DiscordSyncState.Where(s => s.Id == 1).ExecuteDeleteAsync(Ct);
        db.DiscordSyncState.Add(new DiscordSyncState { Id = 1, StaffRolesMembersCurrentAt = currentAt, StaffRolesMembersOffAt = offAt });
        await db.SaveChangesAsync(Ct);
    }

    private static string? ProblemOf(JsonElement view)
        => view.TryGetProperty("problem", out var problem) ? problem.GetString() : null;

    /// <summary>
    /// Just after a restart that followed a long stop, the card gives the bot the same wait to read
    /// the member list as the pass does: an old mark is not called missing within it, and is past
    /// it. A mark the pass had already found missing is called missing at once.
    /// </summary>
    [Fact]
    public async Task JustAfterARestartTheCardWaitsAsThePassDoes()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var bot = new StartedBot();
        await using var host = await ApiTestHost.StartAsync(_db, configure: s => s.AddSingleton<IDiscordBotStatus>(bot));
        var (_, cookie) = await host.SignedInAsync(Mapper, Ct);
        await ServerAsync(host, memberUpdates: false);

        // Stopped for two hours, started a minute ago.
        bot.StartedAt = host.Clock.UtcNow.AddMinutes(-1);
        await MarkAsync(host, host.Clock.UtcNow.AddHours(-2));

        async Task<JsonElement> ViewAsync()
            => await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct);

        Assert.Null(ProblemOf(await ViewAsync()));

        // Past the wait, still not current: missing.
        host.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal("Modbot isn't receiving member updates from Discord.", ProblemOf(await ViewAsync()));

        // Already found missing before the restart: missing at once.
        bot.StartedAt = host.Clock.UtcNow;
        await MarkAsync(host, host.Clock.UtcNow.AddHours(-2), offAt: host.Clock.UtcNow.AddHours(-1));
        Assert.Equal("Modbot isn't receiving member updates from Discord.", ProblemOf(await ViewAsync()));
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
        // Somebody has to stay able to administer Modbot, or any change of roles is refused.
        await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
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

        // Viewer is not mapped, so it is still the users page's to give, and the answer says
        // which roles still follow Discord on the account.
        var viewer = await host.SendJsonAsync(
            HttpMethod.Put, $"/api/users/{staff.Id}/roles", new { roleIds = new[] { BuiltInRoles.ViewerId } }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, viewer.StatusCode);
        Assert.Contains(
            (await ApiTestHost.BodyOf(viewer, Ct)).GetProperty("rolesFromDiscord").EnumerateArray(),
            r => r.GetGuid() == BuiltInRoles.ModeratorId);
    }

    [Fact]
    public async Task ABothWaysRoleTheBotCanGiveCanBeChangedByHand()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(Mapper, Ct);
        // Somebody has to stay able to administer Modbot, or any change of roles is refused.
        await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
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
