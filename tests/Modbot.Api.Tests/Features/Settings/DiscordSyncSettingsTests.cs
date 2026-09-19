using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Settings → Discord → Role and ban sync: a permission the bot does not hold is refused here with
/// the permission named, a role cannot be in two pairs, and switching a ban direction on starts
/// from now rather than replaying the whole log (Discord sync design §4.2, §7).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordSyncSettingsTests
{
    private const string Path = "/api/discord-sync";
    private const string Guild = "700";

    private readonly PostgresFixture _db;

    public DiscordSyncSettingsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Switches(
        bool roleSyncOn = false,
        bool banSyncToDiscord = false,
        bool banSyncToVRChat = false,
        string banCopyAction = "ban")
        => new { roleSyncOn, banSyncToDiscord, banSyncToVRChat, banCopyAction };

    /// <summary>A server the bot is in, with exactly the permissions asked for.</summary>
    private static async Task ServerAsync(
        ApiTestHost host, bool manageRoles, bool banMembers, bool removeMembers, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        (await db.GetSettingsAsync(ct)).DiscordGuildId = Guild;

        db.DiscordServers.Add(new DiscordServer
        {
            GuildId = Guild,
            Name = "The server",
            BotCanManageRoles = manageRoles,
            BotCanBanMembers = banMembers,
            BotCanRemoveMembers = removeMembers,
        });

        db.DiscordRoles.Add(new DiscordRole { RoleId = "801", GuildId = Guild, Name = "Staff", BotCanAssign = true });

        await db.SaveChangesAsync(ct);
    }

    [Fact]
    public async Task WithoutManageDiscordSync_ItIsRefused()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Put, Path, Switches(), cookie, Ct)).StatusCode);
    }

    /// <summary>Running the sync is a second decision, and a second permission.</summary>
    [Fact]
    public async Task ManagingTheSyncDoesNotLetSomebodyRunIt()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageDiscordSync, Ct);

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Post, Path + "/preview", null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Post, Path + "/run", null, cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task ABotWithoutBanMembersCannotHaveBanSyncSwitchedOn()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageDiscordSync, Ct);

        await ServerAsync(host, manageRoles: true, banMembers: false, removeMembers: true, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Put, Path, Switches(banSyncToDiscord: true), cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Ban Members", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // The same switch with removal chosen needs Kick Members, which the bot does hold.
        Assert.Equal(
            HttpStatusCode.OK,
            (await host.SendJsonAsync(HttpMethod.Put, Path, Switches(banSyncToDiscord: true, banCopyAction: "remove"), cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task ABotWithoutManageRolesCannotHaveRoleSyncSwitchedOn()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageDiscordSync, Ct);

        await ServerAsync(host, manageRoles: false, banMembers: true, removeMembers: true, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Put, Path, Switches(roleSyncOn: true), cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Manage Roles", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    /// <summary>
    /// A bot that has never connected is not second-guessed: nothing is known about what it may do,
    /// so the switch is allowed and the pass reports whatever Discord says.
    /// </summary>
    [Fact]
    public async Task ABotThatHasNeverConnectedIsNotSecondGuessed()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageDiscordSync, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Put, Path, Switches(roleSyncOn: true, banSyncToDiscord: true), cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ARoleCanOnlyBeInOnePair()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageDiscordSync, Ct);

        await ServerAsync(host, manageRoles: true, banMembers: true, removeMembers: true, Ct);

        var pair = new { vrchatRoleId = "grol_staff", discordRoleId = "801", decides = "vrchat", enabled = true };

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Post, Path + "/pairs", pair, cookie, Ct)).StatusCode);

        var again = await host.SendJsonAsync(
            HttpMethod.Post,
            Path + "/pairs",
            new { vrchatRoleId = "grol_other", discordRoleId = "801", decides = "vrchat", enabled = true },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task APairNeedsAKnownSideToDecideIt()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageDiscordSync, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Post,
            Path + "/pairs",
            new { vrchatRoleId = "grol_staff", discordRoleId = "801", decides = "whoever", enabled = true },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Switching a ban direction on moves the marker to the newest fact, so the whole log is not
    /// replayed as a flood of bans.
    /// </summary>
    [Fact]
    public async Task SwitchingBanSyncOnStartsFromNow()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageDiscordSync, Ct);

        await ServerAsync(host, manageRoles: true, banMembers: true, removeMembers: true, Ct);

        long newest;

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            newest = await db.Events.AsNoTracking().MaxAsync(e => (long?)e.Id, Ct) ?? 0;
        }

        Assert.Equal(
            HttpStatusCode.OK,
            (await host.SendJsonAsync(HttpMethod.Put, Path, Switches(banSyncToDiscord: true), cookie, Ct)).StatusCode);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var state = await db.DiscordSyncState.AsNoTracking().SingleAsync(Ct);
            Assert.Equal(newest, state.BansReadThrough);
        }
    }
}
