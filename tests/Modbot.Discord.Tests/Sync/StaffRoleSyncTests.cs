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
        TestServices services, IDiscordGateway? gateway = null, bool memberUpdatesCurrent = true, bool pastBrake = false,
        DateTimeOffset? startedAt = null)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<StaffRoleSync>()
            .RunAsync(gateway ?? new FakeGateway(), memberUpdatesCurrent, pastBrake, Ct, startedAt: startedAt);
    }

    private static async Task<DiscordSyncState?> SyncStateAsync(TestServices services)
    {
        await using var db = services.Database.NewContext();
        return await db.DiscordSyncState.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, Ct);
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
        var plan = await StaffRoles.PlanAsync(db, null, withNotes: true, services.Clock.UtcNow, Ct);
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

        await PassAsync(services, memberUpdatesCurrent: false);

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

    /// <summary>A held pass still gives: only taking waits for Apply.</summary>
    [Fact]
    public async Task AHeldPassStillGivesAndOnlyTakingWaitsForApply()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator);

        var losing = new List<ModbotUser>();
        for (var i = 0; i < 6; i++)
        {
            var id = (6100 + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var account = await AccountAsync(services, id);
            await GiveByHandAsync(services, account.Id, Moderator);
            await InServerAsync(services, id);
            losing.Add(account);
        }

        var newcomer = await AccountAsync(services, "6200");
        await InServerAsync(services, "6200", SyncSetUp.DiscordRole);

        var held = await PassAsync(services);

        Assert.True(held.Held);
        Assert.Equal(1, held.Given);
        Assert.Equal(0, held.Taken);
        Assert.NotNull(await HeldAsync(services, newcomer.Id, Moderator));
        foreach (var a in losing)
            Assert.NotNull(await HeldAsync(services, a.Id, Moderator));

        var applied = await PassAsync(services, pastBrake: true);

        Assert.False(applied.Held);
        Assert.Equal(6, applied.Taken);
        foreach (var a in losing)
            Assert.Null(await HeldAsync(services, a.Id, Moderator));
        Assert.NotNull(await HeldAsync(services, newcomer.Id, Moderator));
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

        // The pass wrote its own change into the stored member row as it made it.
        await using (var db = services.Database.NewContext())
        {
            var row = await db.DiscordMembers.AsNoTracking().FirstAsync(m => m.UserId == Member, Ct);
            Assert.Contains(SyncSetUp.DiscordRole, JsonSerializer.Deserialize<string[]>(row.Roles)!);
        }

        // Discord's update arrives.
        await DiscordSaysAsync(services, Member, SyncSetUp.DiscordRole);
        services.Clock.Advance(TimeSpan.FromMinutes(1));
        var after = await PassAsync(services, gateway);
        Assert.Equal(0, after.Given + after.Taken);
        Assert.Single(gateway.RoleChanges);
    }

    /// <summary>
    /// The race: Discord gave the role between the plan's read and the agreement's write, so the
    /// agreement says not held and is stamped after the member row last changed. Held roles are
    /// compared, not times, so the next pass still sees Discord's change and follows it.
    /// </summary>
    [Fact]
    public async Task BothWaysADiscordChangeBetweenReadAndWriteIsStillSeen()
    {
        await using var services = await OnAsync(_db);
        var mappingId = await MapAsync(services, SyncSetUp.DiscordRole, Moderator, StaffRoleDirections.Both);
        var account = await AccountAsync(services, Member);
        await InServerAsync(services, Member, SyncSetUp.DiscordRole);

        await using (var db = services.Database.NewContext())
        {
            db.DiscordStaffRoleStates.Add(new DiscordStaffRoleState
            {
                MappingId = mappingId,
                UserId = account.Id,
                DiscordUserId = Member,
                Held = false,
                AgreedAt = services.Clock.UtcNow.AddHours(1),
            });
            await db.SaveChangesAsync(Ct);
        }

        var gateway = new FakeGateway();
        await PassAsync(services, gateway);

        Assert.Empty(gateway.RoleChanges);
        Assert.NotNull(await HeldAsync(services, account.Id, Moderator));
    }

    /// <summary>
    /// Agreed not held, then they left the server, and somebody gave the Modbot role by hand: not
    /// in the server is holding no Discord role, whatever was agreed, so the role goes and nothing
    /// is sent to Discord.
    /// </summary>
    [Fact]
    public async Task BothWaysSomebodyWhoLeftTheServerIsNeverGivenTheDiscordRole()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator, StaffRoleDirections.Both);
        var account = await AccountAsync(services, Member);
        await InServerAsync(services, Member);
        await PassAsync(services); // Neither holds it: agreed not held.

        await using (var db = services.Database.NewContext())
        {
            var at = services.Clock.UtcNow;
            await db.DiscordMembers.Where(m => m.UserId == Member).ExecuteUpdateAsync(u => u.SetProperty(m => m.LeftAt, at), Ct);
        }

        await GiveByHandAsync(services, account.Id, Moderator);

        var gateway = new FakeGateway();
        await PassAsync(services, gateway);

        Assert.Empty(gateway.RoleChanges);
        Assert.Null(await HeldAsync(services, account.Id, Moderator));
    }

    [Fact]
    public async Task BothWaysADiscordRoleThatBecamePowerfulIsNeverGiven()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator, StaffRoleDirections.Both);
        var account = await AccountAsync(services, Member);
        await GiveByHandAsync(services, account.Id, Moderator);
        await InServerAsync(services, Member);

        await using (var db = services.Database.NewContext())
        {
            await db.DiscordRoles.Where(r => r.RoleId == SyncSetUp.DiscordRole)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.Permissions, 1L << 2), Ct);
        }

        var gateway = new FakeGateway();
        var pass = await PassAsync(services, gateway);

        // Not set up, so Discord decides: no Discord give, and the hand-given role goes.
        Assert.Empty(gateway.RoleChanges);
        Assert.Null(await HeldAsync(services, account.Id, Moderator));
        Assert.StartsWith("Not set up", pass.Problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// A role given by hand in Modbot and agreed both ways goes with the link: taking the Discord
    /// account off takes it, the same as a role Discord gave.
    /// </summary>
    [Fact]
    public async Task BothWaysTakingTheDiscordAccountOffTakesAHandGivenAgreedRole()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator, StaffRoleDirections.Both);
        var account = await AccountAsync(services, Member);
        await GiveByHandAsync(services, account.Id, Moderator);
        await InServerAsync(services, Member);
        await PassAsync(services); // Modbot holds it: the bot gives the Discord role, both agree.

        Assert.True((await HeldAsync(services, account.Id, Moderator))!.FromDiscord);

        await using (var db = services.Database.NewContext())
        {
            await db.Users.Where(u => u.Id == account.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.DiscordUserId, (string?)null).SetProperty(x => x.DiscordVerifiedAt, (DateTimeOffset?)null), Ct);
        }

        await PassAsync(services);

        Assert.Null(await HeldAsync(services, account.Id, Moderator));
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
        var mapping = await db.DiscordStaffRoles.AsNoTracking().FirstAsync(m => m.Id == mappingId, Ct);
        Assert.NotNull(mapping.Problem);
        Assert.NotNull(mapping.RefusedAt);
    }

    /// <summary>
    /// After a refusal the mapping is Not set up everywhere: the bot stops asking (one failed fact,
    /// not one per person per minute), the role follows Discord, and a hand change is refused. A day
    /// later it asks once more.
    /// </summary>
    [Fact]
    public async Task BothWaysARefusalMakesTheMappingNotSetUpUntilADayHasPassed()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator, StaffRoleDirections.Both);

        var first = await AccountAsync(services, Member);
        await GiveByHandAsync(services, first.Id, Moderator);
        await InServerAsync(services, Member);

        var second = await AccountAsync(services, "5002");
        await GiveByHandAsync(services, second.Id, Moderator);
        await InServerAsync(services, "5002");

        var gateway = new FakeGateway { RoleError = "Missing Permissions" };
        await PassAsync(services, gateway);

        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await PassAsync(services, gateway);

        Assert.Single(gateway.RoleReasons);
        Assert.Single(await services.FactsOfTypeAsync(FactType.CopyFailed, Ct));

        // Working as Discord decides: neither holds the Discord role, so both hand-given roles go.
        Assert.Null(await HeldAsync(services, first.Id, Moderator));
        Assert.Null(await HeldAsync(services, second.Id, Moderator));

        await using (var db = services.Database.NewContext())
        {
            var user = await db.Users.Include(u => u.Roles).ThenInclude(r => r.Role).FirstAsync(u => u.Id == first.Id, Ct);
            var locked = await StaffRoles.FollowingDiscordAsync(db, [user], services.Clock.UtcNow, Ct);
            Assert.Contains(Moderator, locked[first.Id]);
        }

        // A day on, the bot asks again.
        services.Clock.Advance(TimeSpan.FromDays(1));
        await GiveByHandAsync(services, first.Id, Moderator);
        gateway.RoleError = null;
        await PassAsync(services, gateway);

        var (added, _, userId, _) = Assert.Single(gateway.RoleChanges);
        Assert.True(added);
        Assert.Equal(Member, userId);
    }

    [Fact]
    public async Task BothWaysSomebodyNotInTheServerLosesTheModbotRoleTheFirstTime()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator, StaffRoleDirections.Both);
        var account = await AccountAsync(services, Member);
        await GiveByHandAsync(services, account.Id, Moderator);

        var gateway = new FakeGateway();
        await PassAsync(services, gateway);

        Assert.Empty(gateway.RoleChanges);
        Assert.Null(await HeldAsync(services, account.Id, Moderator));
    }

    /// <summary>
    /// Re-proving a different Discord account that lacks the role: the agreement was about the old
    /// one, so it does not count, and Discord decides. The Modbot role goes; the new account is not
    /// handed the Discord role.
    /// </summary>
    [Fact]
    public async Task BothWaysProvingADifferentDiscordAccountWithoutTheRoleTakesTheModbotRole()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator, StaffRoleDirections.Both);
        var account = await AccountAsync(services, Member);
        await InServerAsync(services, Member, SyncSetUp.DiscordRole);
        await PassAsync(services); // Discord holds it: Modbot gives, both agree.
        Assert.NotNull(await HeldAsync(services, account.Id, Moderator));

        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await InServerAsync(services, "5009");
        await using (var db = services.Database.NewContext())
        {
            var at = services.Clock.UtcNow;
            await db.Users.Where(u => u.Id == account.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.DiscordUserId, "5009").SetProperty(x => x.DiscordVerifiedAt, at), Ct);
        }

        var gateway = new FakeGateway();
        await PassAsync(services, gateway);

        Assert.Empty(gateway.RoleChanges);
        Assert.Null(await HeldAsync(services, account.Id, Moderator));
    }

    /// <summary>
    /// Unlinking and connecting the same Discord account again: the unlink took the Modbot role the
    /// mapping gave and cleared the agreement, and the Discord role was left. Connecting again gives
    /// the Modbot role back and never takes the Discord role.
    /// </summary>
    [Fact]
    public async Task BothWaysConnectingTheSameDiscordAccountAgainDoesNotTakeTheDiscordRole()
    {
        await using var services = await OnAsync(_db);
        var mappingId = await MapAsync(services, SyncSetUp.DiscordRole, Moderator, StaffRoleDirections.Both);
        var account = await AccountAsync(services, Member);
        await InServerAsync(services, Member, SyncSetUp.DiscordRole);
        await PassAsync(services);

        // Unlinked: the next pass takes what the mapping gave and forgets the agreement.
        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await using (var db = services.Database.NewContext())
        {
            await db.Users.Where(u => u.Id == account.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.DiscordUserId, (string?)null).SetProperty(x => x.DiscordVerifiedAt, (DateTimeOffset?)null), Ct);
        }

        await PassAsync(services);
        Assert.Null(await HeldAsync(services, account.Id, Moderator));

        await using (var db = services.Database.NewContext())
            Assert.False(await db.DiscordStaffRoleStates.AnyAsync(st => st.MappingId == mappingId, Ct));

        // Connected again, the same Discord account, still holding the Discord role.
        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await using (var db = services.Database.NewContext())
        {
            var at = services.Clock.UtcNow;
            await db.Users.Where(u => u.Id == account.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.DiscordUserId, Member).SetProperty(x => x.DiscordVerifiedAt, at), Ct);
        }

        var gateway = new FakeGateway();
        await PassAsync(services, gateway);

        Assert.Empty(gateway.RoleChanges);
        Assert.NotNull(await HeldAsync(services, account.Id, Moderator));
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

    // ── Member updates ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Without member updates the stored roles go stale, and somebody taken off staff in Discord
    /// would keep their Modbot role. So nothing is given or taken; within the wait it is quiet (a
    /// reconnect), past it every link is Not set up and one fact says why; and when updates come
    /// back the pass carries on.
    /// </summary>
    [Fact]
    public async Task WithoutMemberUpdatesNothingIsGivenOrTakenAndItSaysSoOnce()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator);

        var gains = await AccountAsync(services, Member);
        await InServerAsync(services, Member, SyncSetUp.DiscordRole);

        var loses = await AccountAsync(services, "5002");
        await GiveByHandAsync(services, loses.Id, Moderator);
        await InServerAsync(services, "5002");

        // Current once, then the updates stop.
        await PassAsync(services, memberUpdatesCurrent: true);
        await TakeByHandAsync(services, gains.Id, Moderator);
        await GiveByHandAsync(services, loses.Id, Moderator);

        // Within the wait: nothing, quietly.
        services.Clock.Advance(TimeSpan.FromMinutes(5));
        var quiet = await PassAsync(services, memberUpdatesCurrent: false);
        Assert.Equal(0, quiet.Given + quiet.Taken);
        Assert.Null(quiet.Problem);

        // Past it: nothing, Not set up, one fact however many passes.
        services.Clock.Advance(TimeSpan.FromMinutes(10));
        var stopped = await PassAsync(services, memberUpdatesCurrent: false);
        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await PassAsync(services, memberUpdatesCurrent: false);

        Assert.Equal(StaffRoles.NoMemberUpdates, stopped.Problem);
        Assert.Null(await HeldAsync(services, gains.Id, Moderator));
        Assert.NotNull(await HeldAsync(services, loses.Id, Moderator));
        Assert.Single(await services.FactsOfTypeAsync(FactType.StaffRolesNoMemberUpdates, Ct));

        await using (var db = services.Database.NewContext())
        {
            var plan = await StaffRoles.PlanAsync(db, null, withNotes: false, services.Clock.UtcNow, Ct);
            Assert.Contains(plan.Problems, p => p.Contains(StaffRoles.NoMemberUpdates, StringComparison.Ordinal));

            var user = await db.Users.Include(u => u.Roles).ThenInclude(r => r.Role).FirstAsync(u => u.Id == loses.Id, Ct);
            var locked = await StaffRoles.FollowingDiscordAsync(db, [user], services.Clock.UtcNow, Ct);
            Assert.Contains(Moderator, locked[loses.Id]);
        }

        // Updates back: the pass carries on.
        var back = await PassAsync(services, memberUpdatesCurrent: true);
        Assert.Equal(1, back.Given);
        Assert.Equal(1, back.Taken);
    }

    /// <summary>
    /// With no link yet, a pass still notes that member updates are current, so the card is right
    /// before the first link is added; and while they are not, it records nothing, since there is no
    /// link for them to stop.
    /// </summary>
    [Fact]
    public async Task APassWithNoLinkStillNotesMemberUpdates()
    {
        await using var services = await OnAsync(_db, on: false);

        var pass = await PassAsync(services, memberUpdatesCurrent: true);
        Assert.Equal(StaffRolePass.Nothing, pass);

        var state = await SyncStateAsync(services);
        Assert.Equal(services.Clock.UtcNow, state?.StaffRolesMembersCurrentAt);
        Assert.False(StaffRoles.MemberUpdatesMissing(state, services.Clock.UtcNow));

        // Half an hour on, a pass finds them current again: the mark moves with it.
        services.Clock.Advance(TimeSpan.FromMinutes(30));
        await PassAsync(services, memberUpdatesCurrent: true);
        state = await SyncStateAsync(services);
        Assert.Equal(services.Clock.UtcNow, state?.StaffRolesMembersCurrentAt);
        Assert.False(StaffRoles.MemberUpdatesMissing(state, services.Clock.UtcNow));

        // Then they stop: nothing recorded while no link exists.
        services.Clock.Advance(TimeSpan.FromMinutes(15));
        var stopped = await PassAsync(services, memberUpdatesCurrent: false);
        Assert.Null(stopped.Problem);
        Assert.Null((await SyncStateAsync(services))?.StaffRolesMembersOffAt);
        Assert.Empty(await services.FactsOfTypeAsync(FactType.StaffRolesNoMemberUpdates, Ct));
    }

    /// <summary>
    /// After a stop longer than the wait, the bot gets the same minutes to connect as after a
    /// reconnect, counted from start-up: nothing is recorded in them. If updates are still not
    /// current past them, the pass says so once, as before.
    /// </summary>
    [Fact]
    public async Task ARestartGetsTheSameWaitAsAReconnect()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator);
        await PassAsync(services, memberUpdatesCurrent: true);

        // Modbot was stopped for two hours, and has just started.
        services.Clock.Advance(TimeSpan.FromHours(2));
        var startedAt = services.Clock.UtcNow;

        var connecting = await PassAsync(services, memberUpdatesCurrent: false, startedAt: startedAt);
        Assert.Null(connecting.Problem);
        Assert.Null((await SyncStateAsync(services))?.StaffRolesMembersOffAt);
        Assert.Empty(await services.FactsOfTypeAsync(FactType.StaffRolesNoMemberUpdates, Ct));

        services.Clock.Advance(TimeSpan.FromMinutes(11));
        var stopped = await PassAsync(services, memberUpdatesCurrent: false, startedAt: startedAt);
        Assert.Equal(StaffRoles.NoMemberUpdates, stopped.Problem);
        Assert.Single(await services.FactsOfTypeAsync(FactType.StaffRolesNoMemberUpdates, Ct));
    }

    // ── Deleted Discord roles and the member row ──────────────────────────────────────────

    /// <summary>
    /// A deleted Discord role gives nobody anything: the member rows still carry its id until each
    /// member's next update, but the server index has marked it gone.
    /// </summary>
    [Fact]
    public async Task DeletingALinkedDiscordRoleTakesTheModbotRoleFromItsHolders()
    {
        await using var services = await OnAsync(_db);
        await MapAsync(services, SyncSetUp.DiscordRole, Moderator);
        var account = await AccountAsync(services, Member);
        await InServerAsync(services, Member, SyncSetUp.DiscordRole);
        await PassAsync(services);
        Assert.NotNull(await HeldAsync(services, account.Id, Moderator));

        await using (var db = services.Database.NewContext())
        {
            var at = services.Clock.UtcNow;
            await db.DiscordRoles.Where(r => r.RoleId == SyncSetUp.DiscordRole)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.RemovedAt, at), Ct);
        }

        await PassAsync(services);

        Assert.Null(await HeldAsync(services, account.Id, Moderator));
    }

    /// <summary>
    /// The write-back adds or removes the one role inside the database: a roles update the
    /// recorder made after the pass read the row, and before the write-back, survives it.
    /// </summary>
    [Fact]
    public async Task TheWriteBackKeepsARecorderUpdateMadeInBetween()
    {
        await using var services = await OnAsync(_db);
        await InServerAsync(services, Member, "A");

        // The pass has read ["A"]; the recorder now writes ["A", "B"].
        await DiscordSaysAsync(services, Member, "A", "B");

        await using (var db = services.Database.NewContext())
            await StaffRoleSync.WriteBackAsync(db, SyncSetUp.Guild, Member, "C", give: true, Ct);

        Assert.Equal(new[] { "A", "B", "C" }, (await RolesOfAsync(services, Member)).Order(StringComparer.Ordinal));

        // And taking one away: the recorder adds "D" in between; only "C" goes.
        await DiscordSaysAsync(services, Member, "A", "B", "C", "D");

        await using (var db = services.Database.NewContext())
        {
            await StaffRoleSync.WriteBackAsync(db, SyncSetUp.Guild, Member, "C", give: false, Ct);

            // Giving one the row already holds adds no second copy.
            await StaffRoleSync.WriteBackAsync(db, SyncSetUp.Guild, Member, "A", give: true, Ct);
        }

        Assert.Equal(new[] { "A", "B", "D" }, (await RolesOfAsync(services, Member)).Order(StringComparer.Ordinal));
    }

    private static async Task<string[]> RolesOfAsync(TestServices services, string discordUserId)
    {
        await using var db = services.Database.NewContext();
        var row = await db.DiscordMembers.AsNoTracking().FirstAsync(m => m.UserId == discordUserId, Ct);
        return JsonSerializer.Deserialize<string[]>(row.Roles)!;
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
        var plan = await StaffRoles.PlanAsync(db, null, withNotes: true, services.Clock.UtcNow, Ct);

        Assert.Contains(plan.Notes, n => n.What == StaffRoleChangeKinds.NoAccount && n.DiscordUserId == "5100");
        Assert.Contains(plan.Notes, n => n.What == StaffRoleChangeKinds.NotProven && n.DiscordUserId == "5101");
        Assert.Empty(plan.Changes);
    }
}
