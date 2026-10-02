using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Users;
using Modbot.Discord.Gateway;
using Modbot.Discord.Sync;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Sync;

/// <summary>
/// Staff roles from Discord (design 2026-10-02): a Discord role gives and takes a Modbot role, only
/// for proven, enabled, non-administrator accounts; hand-given roles follow Discord too; the brake
/// stops a large take-away; and a both-ways mapping copies whichever side changed without bouncing.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class StaffRoleSyncTests
{
    private const string Member = "5001";
    private const string SecondRole = "802";

    private static readonly Guid Moderator = BuiltInRoles.ModeratorId;

    private readonly PostgresFixture _db;

    public StaffRoleSyncTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<TestServices> OnAsync(PostgresFixture db, bool on = true)
        => SyncSetUp.CreateAsync(db, s => s.DiscordStaffRolesOn = on, Ct);

    // ── Helpers ────────────────────────────────────────────────────────────────────────────

    private static async Task<Guid> MapAsync(
        TestServices services, string discordRoleId, Guid roleId, string direction = StaffRoleDirections.Discord)
    {
        await using var db = services.Database.NewContext();
        var mapping = new DiscordStaffRole
        {
            DiscordRoleId = discordRoleId,
            DiscordRoleName = "Staff",
            RoleId = roleId,
            Direction = direction,
            CreatedAt = services.Clock.UtcNow,
            UpdatedAt = services.Clock.UtcNow,
        };
        db.DiscordStaffRoles.Add(mapping);
        await db.SaveChangesAsync(Ct);
        return mapping.Id;
    }

    private static async Task<ModbotUser> AccountAsync(
        TestServices services, string discordUserId, bool proven = true, bool disabled = false,
        ModbotPermissions permissions = ModbotPermissions.None)
        => await services.LinkedAccountAsync(discordUserId, permissions, disabled, Ct, proven);

    private static async Task GiveByHandAsync(TestServices services, Guid userId, Guid roleId)
    {
        await using var db = services.Database.NewContext();
        db.UserRoles.Add(new ModbotUserRole { UserId = userId, RoleId = roleId });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task TakeByHandAsync(TestServices services, Guid userId, Guid roleId)
    {
        await using var db = services.Database.NewContext();
        await db.UserRoles.Where(r => r.UserId == userId && r.RoleId == roleId).ExecuteDeleteAsync(Ct);
    }

    private static async Task<ModbotUserRole?> HeldAsync(TestServices services, Guid userId, Guid roleId)
    {
        await using var db = services.Database.NewContext();
        return await db.UserRoles.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == userId && r.RoleId == roleId, Ct);
    }

    private static async Task InServerAsync(TestServices services, string discordUserId, params string[] roles)
        => await SyncSetUp.UnlinkedMemberAsync(services, discordUserId, roles, Ct);

    /// <summary>What Discord now says, as a member update would leave the row: roles and the time it changed.</summary>
    private static async Task DiscordSaysAsync(TestServices services, string discordUserId, params string[] roles)
    {
        await using var db = services.Database.NewContext();
        var json = JsonSerializer.Serialize(roles);
        var at = services.Clock.UtcNow;

        await db.DiscordMembers
            .Where(m => m.UserId == discordUserId)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Roles, json).SetProperty(m => m.UpdatedAt, at), Ct);
    }

    private static async Task<StaffRolePass> PassAsync(
        TestServices services, IDiscordGateway? gateway = null, bool membersRead = true, bool pastBrake = false)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<StaffRoleSync>()
            .RunAsync(gateway ?? new FakeGateway(), membersRead, pastBrake, Ct);
    }

    // ── Discord decides ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AHeldDiscordRoleGivesTheModbotRole()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator);
        var account = await AccountAsync(services, Member);
        await InServerAsync(services, Member, SyncSetUp.DiscordRole);

        var pass = await PassAsync(services);

        Assert.Equal(1, pass.Given);
        var held = await HeldAsync(services, account.Id, Moderator);
        Assert.NotNull(held);
        Assert.True(held.FromDiscord);

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.UserRolesChanged, Ct));
        Assert.Null(fact.ActorId);
        using var data = JsonDocument.Parse(fact.Data!);
        Assert.Equal("discord-role", data.RootElement.GetProperty("why").GetString());
    }

    [Fact]
    public async Task LosingTheDiscordRoleTakesTheModbotRoleAwayAndLeavesTheAccount()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator);
        var account = await AccountAsync(services, Member);
        await InServerAsync(services, Member, SyncSetUp.DiscordRole);
        await PassAsync(services);

        await DiscordSaysAsync(services, Member);
        var pass = await PassAsync(services);

        Assert.Equal(1, pass.Taken);
        Assert.Null(await HeldAsync(services, account.Id, Moderator));

        await using var db = services.Database.NewContext();
        Assert.False((await db.Users.AsNoTracking().FirstAsync(u => u.Id == account.Id, Ct)).IsDisabled);
    }

    /// <summary>Decided 2026-10-02: Discord fully decides, so a role given by hand goes too.</summary>
    [Fact]
    public async Task AHandGivenRoleWithoutTheDiscordRoleIsTakenAway()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator);
        var account = await AccountAsync(services, Member);
        await GiveByHandAsync(services, account.Id, Moderator);
        await InServerAsync(services, Member);

        var pass = await PassAsync(services);

        Assert.Equal(1, pass.Taken);
        Assert.Null(await HeldAsync(services, account.Id, Moderator));
    }

    [Fact]
    public async Task AHandGivenRoleTheDiscordRoleAlsoGivesIsMarkedAsFromDiscord()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator);
        var account = await AccountAsync(services, Member);
        await GiveByHandAsync(services, account.Id, Moderator);
        await InServerAsync(services, Member, SyncSetUp.DiscordRole);

        var pass = await PassAsync(services);

        Assert.Equal(0, pass.Given + pass.Taken);
        Assert.True((await HeldAsync(services, account.Id, Moderator))!.FromDiscord);
        Assert.Empty(await services.FactsOfTypeAsync(FactType.UserRolesChanged, Ct));
    }

    [Fact]
    public async Task LeavingTheServerTakesTheRoleAway()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator);
        var account = await AccountAsync(services, Member);
        await InServerAsync(services, Member, SyncSetUp.DiscordRole);
        await PassAsync(services);

        await using (var db = services.Database.NewContext())
        {
            var at = services.Clock.UtcNow;
            await db.DiscordMembers.Where(m => m.UserId == Member).ExecuteUpdateAsync(u => u.SetProperty(m => m.LeftAt, at), Ct);
        }

        await PassAsync(services);

        Assert.Null(await HeldAsync(services, account.Id, Moderator));
    }

    [Fact]
    public async Task TakingTheDiscordAccountOffTakesWhatTheMappingGaveButNotWhatWasGivenByHand()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator);
        var account = await AccountAsync(services, Member);
        await InServerAsync(services, Member, SyncSetUp.DiscordRole);
        await PassAsync(services);

        var other = await AccountAsync(services, "5002", proven: false);
        await GiveByHandAsync(services, other.Id, Moderator);

        await using (var db = services.Database.NewContext())
        {
            await db.Users.Where(u => u.Id == account.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.DiscordUserId, (string?)null).SetProperty(x => x.DiscordVerifiedAt, (DateTimeOffset?)null), Ct);
        }

        await PassAsync(services);

        Assert.Null(await HeldAsync(services, account.Id, Moderator));
        Assert.NotNull(await HeldAsync(services, other.Id, Moderator));
    }

    [Fact]
    public async Task ATypedInDiscordAccountIsNeverGivenAnything()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator);
        var account = await AccountAsync(services, Member, proven: false);
        await InServerAsync(services, Member, SyncSetUp.DiscordRole);

        await PassAsync(services);

        Assert.Null(await HeldAsync(services, account.Id, Moderator));
    }

    [Fact]
    public async Task AdministratorsAreNeverGivenOrTakenAnything()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator);

        var given = await AccountAsync(services, Member, permissions: ModbotPermissions.Administrator);
        await InServerAsync(services, Member, SyncSetUp.DiscordRole);

        var kept = await AccountAsync(services, "5002", permissions: ModbotPermissions.Administrator);
        await GiveByHandAsync(services, kept.Id, Moderator);
        await InServerAsync(services, "5002");

        var pass = await PassAsync(services);

        Assert.Equal(0, pass.Given + pass.Taken);
        Assert.Null(await HeldAsync(services, given.Id, Moderator));
        Assert.NotNull(await HeldAsync(services, kept.Id, Moderator));
    }

    [Fact]
    public async Task DisabledAccountsAreLeftAlone()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator);
        var account = await AccountAsync(services, Member, disabled: true);
        await InServerAsync(services, Member, SyncSetUp.DiscordRole);

        await PassAsync(services);

        Assert.Null(await HeldAsync(services, account.Id, Moderator));
    }

    [Fact]
    public async Task AnyOfTwoDiscordRolesKeepsTheModbotRole()
    {
        await using var services = await OnAsync(_db);

        await using (var db = services.Database.NewContext())
        {
            db.DiscordRoles.Add(new DiscordRole
            {
                RoleId = SecondRole, GuildId = SyncSetUp.Guild, Name = "Events", BotCanAssign = true,
                FirstSeenAt = services.Clock.UtcNow, UpdatedAt = services.Clock.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        await MapAsync(services, SyncSetUp.DiscordRole, Moderator);
        await MapAsync(services, SecondRole, Moderator);
        var account = await AccountAsync(services, Member);
        await InServerAsync(services, Member, SyncSetUp.DiscordRole, SecondRole);
        await PassAsync(services);

        await DiscordSaysAsync(services, Member, SecondRole);
        await PassAsync(services);
        Assert.NotNull(await HeldAsync(services, account.Id, Moderator));

        await DiscordSaysAsync(services, Member);
        await PassAsync(services);
        Assert.Null(await HeldAsync(services, account.Id, Moderator));
    }

    [Fact]
    public async Task AMappedRoleThatCarriesAdministratorIsNeverGiven()
    {
        await using var services = await OnAsync(_db);

        Guid adminLike;
        await using (var db = services.Database.NewContext())
        {
            adminLike = await TestAccounts.RoleForAsync(db, ModbotPermissions.Administrator | ModbotPermissions.ViewMembers, Ct);
        }

        await MapAsync(services, SyncSetUp.DiscordRole, adminLike);
        var account = await AccountAsync(services, Member);
        await InServerAsync(services, Member, SyncSetUp.DiscordRole);

        var pass = await PassAsync(services);

        Assert.Null(await HeldAsync(services, account.Id, adminLike));
        Assert.NotNull(pass.Problem);
    }

    // ── The switch, the wait and the brake ─────────────────────────────────────────────────

    [Fact]
    public async Task WithTheSwitchOffNothingChangesButThePreviewStillAnswers()
    {
        await using var services = await OnAsync(_db, on: false);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator);
        var account = await AccountAsync(services, Member);
        await InServerAsync(services, Member, SyncSetUp.DiscordRole);

        await PassAsync(services);
        Assert.Null(await HeldAsync(services, account.Id, Moderator));

        await using var db = services.Database.NewContext();
        var plan = await StaffRoles.PlanAsync(db, null, withNotes: true, Ct);
        var change = Assert.Single(plan.Changes);
        Assert.Equal(StaffRoleChangeKinds.Give, change.What);
        Assert.Empty(await services.FactsOfTypeAsync(FactType.UserRolesChanged, Ct));
    }

    [Fact]
    public async Task WithNoMappingsNothingIsTouched()
    {
        await using var services = await OnAsync(_db);
        var account = await AccountAsync(services, Member);
        await GiveByHandAsync(services, account.Id, Moderator);
        await InServerAsync(services, Member);

        var pass = await PassAsync(services);

        Assert.Equal(0, pass.Given + pass.Taken);
        Assert.NotNull(await HeldAsync(services, account.Id, Moderator));
    }

    [Fact]
    public async Task NothingHappensBeforeThisConnectionHasReadTheMemberList()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator);
        var account = await AccountAsync(services, Member);
        await InServerAsync(services, Member, SyncSetUp.DiscordRole);

        await PassAsync(services, membersRead: false);

        Assert.Null(await HeldAsync(services, account.Id, Moderator));
    }

    [Fact]
    public async Task TheBrakeStopsALargeTakeAwayAndApplyCarriesItOut()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator);

        var accounts = new List<ModbotUser>();
        for (var i = 0; i < 6; i++)
        {
            var id = (6000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var account = await AccountAsync(services, id);
            await GiveByHandAsync(services, account.Id, Moderator);
            await InServerAsync(services, id);
            accounts.Add(account);
        }

        var held = await PassAsync(services);
        await PassAsync(services);

        Assert.True(held.Held);
        Assert.Equal(6, held.Losing);
        foreach (var a in accounts)
            Assert.NotNull(await HeldAsync(services, a.Id, Moderator));

        Assert.Single(await services.FactsOfTypeAsync(FactType.StaffRolesHeld, Ct));

        var applied = await PassAsync(services, pastBrake: true);

        Assert.Equal(6, applied.Taken);
        foreach (var a in accounts)
            Assert.Null(await HeldAsync(services, a.Id, Moderator));
    }

    [Fact]
    public async Task TheBrakeNeverStopsGiving()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator);

        for (var i = 0; i < 8; i++)
        {
            var id = (7000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await AccountAsync(services, id);
            await InServerAsync(services, id, SyncSetUp.DiscordRole);
        }

        var pass = await PassAsync(services);

        Assert.False(pass.Held);
        Assert.Equal(8, pass.Given);
    }

    // ── Both ways ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BothWaysTheFirstPassGivesTheDiscordRoleToSomebodyWhoHoldsTheModbotRole()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator, StaffRoleDirections.Both);
        var account = await AccountAsync(services, Member);
        await GiveByHandAsync(services, account.Id, Moderator);
        await InServerAsync(services, Member);

        var gateway = new FakeGateway();
        var pass = await PassAsync(services, gateway);

        Assert.Equal(1, pass.Given);
        var (added, _, userId, roleId) = Assert.Single(gateway.RoleChanges);
        Assert.True(added);
        Assert.Equal(Member, userId);
        Assert.Equal(SyncSetUp.DiscordRole, roleId);
        Assert.Single(await services.FactsOfTypeAsync(FactType.CopiedRoleGiven, Ct));
    }

    /// <summary>
    /// The bot's own change must not come back: until the member update arrives the row still says
    /// the role is not held, and after it arrives the row agrees.
    /// </summary>
    [Fact]
    public async Task BothWaysAChangeModbotMadeDoesNotBounceBack()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator, StaffRoleDirections.Both);
        var account = await AccountAsync(services, Member);
        await InServerAsync(services, Member);
        await PassAsync(services); // Both say no: agreed.

        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await GiveByHandAsync(services, account.Id, Moderator);
        var gateway = new FakeGateway();
        await PassAsync(services, gateway);
        Assert.Single(gateway.RoleChanges);

        // Before Discord's update arrives: the row is older than the agreement.
        services.Clock.Advance(TimeSpan.FromMinutes(1));
        var stale = await PassAsync(services, gateway);
        Assert.Equal(0, stale.Given + stale.Taken);
        Assert.NotNull(await HeldAsync(services, account.Id, Moderator));

        // Discord's update arrives.
        await DiscordSaysAsync(services, Member, SyncSetUp.DiscordRole);
        services.Clock.Advance(TimeSpan.FromMinutes(1));
        var after = await PassAsync(services, gateway);
        Assert.Equal(0, after.Given + after.Taken);
        Assert.Single(gateway.RoleChanges);
    }

    [Fact]
    public async Task BothWaysTakingTheRoleInModbotTakesItInDiscord()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator, StaffRoleDirections.Both);
        var account = await AccountAsync(services, Member);
        await InServerAsync(services, Member, SyncSetUp.DiscordRole);
        await PassAsync(services); // Discord holds, Modbot gives: agreed held.

        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await TakeByHandAsync(services, account.Id, Moderator);
        var gateway = new FakeGateway();
        await PassAsync(services, gateway);

        var (added, _, _, _) = Assert.Single(gateway.RoleChanges);
        Assert.False(added);
    }

    [Fact]
    public async Task BothWaysTakingTheRoleInDiscordTakesItInModbot()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator, StaffRoleDirections.Both);
        var account = await AccountAsync(services, Member);
        await InServerAsync(services, Member, SyncSetUp.DiscordRole);
        await PassAsync(services);

        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await DiscordSaysAsync(services, Member);
        var gateway = new FakeGateway();
        await PassAsync(services, gateway);

        Assert.Empty(gateway.RoleChanges);
        Assert.Null(await HeldAsync(services, account.Id, Moderator));
    }

    [Fact]
    public async Task BothWaysTheBotThatCannotGiveTheRoleWorksAsDiscordDecidesAndSaysNotSetUp()
    {
        await using var services = await OnAsync(_db);

        await using (var db = services.Database.NewContext())
        {
            await db.DiscordRoles.Where(r => r.RoleId == SyncSetUp.DiscordRole)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.BotCanAssign, false), Ct);
        }

        await MapAsync(services, SyncSetUp.DiscordRole, Moderator, StaffRoleDirections.Both);
        var holds = await AccountAsync(services, Member);
        await InServerAsync(services, Member, SyncSetUp.DiscordRole);

        var handGiven = await AccountAsync(services, "5002");
        await GiveByHandAsync(services, handGiven.Id, Moderator);
        await InServerAsync(services, "5002");

        var gateway = new FakeGateway();
        var pass = await PassAsync(services, gateway);

        Assert.Empty(gateway.RoleChanges);
        Assert.NotNull(await HeldAsync(services, holds.Id, Moderator));
        Assert.Null(await HeldAsync(services, handGiven.Id, Moderator));
        Assert.StartsWith("Not set up", pass.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BothWaysADiscordRefusalIsReportedAndNothingIsAgreed()
    {
        await using var services = await OnAsync(_db);
        var mappingId = await MapAsync(services, SyncSetUp.DiscordRole, Moderator, StaffRoleDirections.Both);
        var account = await AccountAsync(services, Member);
        await GiveByHandAsync(services, account.Id, Moderator);
        await InServerAsync(services, Member);

        var gateway = new FakeGateway { RoleError = "Missing Permissions" };
        var pass = await PassAsync(services, gateway);

        Assert.StartsWith("Not set up", pass.Problem, StringComparison.Ordinal);
        Assert.Single(await services.FactsOfTypeAsync(FactType.CopyFailed, Ct));

        await using var db = services.Database.NewContext();
        Assert.False(await db.DiscordStaffRoleStates.AnyAsync(s => s.MappingId == mappingId, Ct));
        Assert.NotNull((await db.DiscordStaffRoles.AsNoTracking().FirstAsync(m => m.Id == mappingId, Ct)).Problem);
    }

    [Fact]
    public async Task BothWaysNeverGivesAnAdministratorTheDiscordRole()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator, StaffRoleDirections.Both);
        var account = await AccountAsync(services, Member, permissions: ModbotPermissions.Administrator);
        await GiveByHandAsync(services, account.Id, Moderator);
        await InServerAsync(services, Member);

        var gateway = new FakeGateway();
        await PassAsync(services, gateway);

        Assert.Empty(gateway.RoleChanges);
    }

    // ── The preview's notes ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePreviewNamesMembersWithNoAccountAndAccountsNotProven()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator);
        await InServerAsync(services, "5100", SyncSetUp.DiscordRole);
        await AccountAsync(services, "5101", proven: false);
        await InServerAsync(services, "5101", SyncSetUp.DiscordRole);

        await using var db = services.Database.NewContext();
        var plan = await StaffRoles.PlanAsync(db, null, withNotes: true, Ct);

        Assert.Contains(plan.Notes, n => n.What == StaffRoleChangeKinds.NoAccount && n.DiscordUserId == "5100");
        Assert.Contains(plan.Notes, n => n.What == StaffRoleChangeKinds.NotProven && n.DiscordUserId == "5101");
        Assert.Empty(plan.Changes);
    }
}
