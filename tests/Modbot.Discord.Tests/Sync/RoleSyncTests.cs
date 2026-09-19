using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Sync;

/// <summary>
/// Role sync: the deciding side wins, a pair nobody decides only reports, unlinked members are
/// untouched, and a dry run changes nothing (M5 §3).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class RoleSyncTests
{
    private const string Person = "usr_person";
    private const string Discord = "5001";

    private readonly PostgresFixture _db;

    public RoleSyncTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<TestServices> OnAsync(PostgresFixture db)
        => SyncSetUp.CreateAsync(db, s => s.DiscordRoleSyncOn = true, Ct);

    [Fact]
    public async Task WhenVRChatDecidesTheDiscordRoleFollows()
    {
        await using var services = await OnAsync(_db);
        await SyncSetUp.PairAsync(services, RoleSyncDecides.VRChat, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, inGroup: [SyncSetUp.GroupRole], inServer: [], ct: Ct);

        var gateway = new FakeGateway();
        var pass = await SyncSetUp.RolePassAsync(services, gateway, apply: true, Ct);

        Assert.Equal(1, pass.Given);
        var (added, _, userId, roleId) = Assert.Single(gateway.RoleChanges);
        Assert.True(added);
        Assert.Equal(Discord, userId);
        Assert.Equal(SyncSetUp.DiscordRole, roleId);

        Assert.Empty(services.VRChat.Actions);
        Assert.Single(await services.FactsOfTypeAsync(FactType.CopiedRoleGiven, Ct));
    }

    [Fact]
    public async Task WhenDiscordDecidesTheGroupRoleFollows()
    {
        await using var services = await OnAsync(_db);
        await SyncSetUp.PairAsync(services, RoleSyncDecides.Discord, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, inGroup: [], inServer: [SyncSetUp.DiscordRole], ct: Ct);

        var gateway = new FakeGateway();
        var pass = await SyncSetUp.RolePassAsync(services, gateway, apply: true, Ct);

        Assert.Equal(1, pass.Given);
        var (what, userId, roleId) = Assert.Single(services.VRChat.Actions);
        Assert.Equal("role-given", what);
        Assert.Equal(Person, userId);
        Assert.Equal(SyncSetUp.GroupRole, roleId);

        Assert.Empty(gateway.RoleChanges);
    }

    /// <summary>
    /// The deciding side does not have it, so the mirror loses it. This is the revert §3.1 asks
    /// for, and the fact is how it is reported.
    /// </summary>
    [Fact]
    public async Task ARoleTheDecidingSideDoesNotHaveIsTakenAway()
    {
        await using var services = await OnAsync(_db);
        await SyncSetUp.PairAsync(services, RoleSyncDecides.VRChat, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, inGroup: [], inServer: [SyncSetUp.DiscordRole], ct: Ct);

        var gateway = new FakeGateway();
        var pass = await SyncSetUp.RolePassAsync(services, gateway, apply: true, Ct);

        Assert.Equal(1, pass.Taken);
        var (added, _, _, _) = Assert.Single(gateway.RoleChanges);
        Assert.False(added);
        Assert.Single(await services.FactsOfTypeAsync(FactType.CopiedRoleTaken, Ct));
    }

    [Fact]
    public async Task WhenNobodyDecidesNothingChangesAndTheDisagreementIsRecorded()
    {
        await using var services = await OnAsync(_db);
        await SyncSetUp.PairAsync(services, RoleSyncDecides.Nobody, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, inGroup: [SyncSetUp.GroupRole], inServer: [], ct: Ct);

        var gateway = new FakeGateway();
        var pass = await SyncSetUp.RolePassAsync(services, gateway, apply: true, Ct);

        Assert.Equal(1, pass.Disagreed);
        Assert.Equal(0, pass.Given);
        Assert.Equal(0, pass.Taken);
        Assert.Empty(gateway.RoleChanges);
        Assert.Empty(services.VRChat.Actions);
        Assert.Single(await services.FactsOfTypeAsync(FactType.RolesDisagree, Ct));
    }

    /// <summary>A pair nobody decides says so once, not once a minute forever.</summary>
    [Fact]
    public async Task ADisagreementIsNotRepeatedOnEveryPass()
    {
        await using var services = await OnAsync(_db);
        await SyncSetUp.PairAsync(services, RoleSyncDecides.Nobody, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, inGroup: [SyncSetUp.GroupRole], inServer: [], ct: Ct);

        var gateway = new FakeGateway();
        await SyncSetUp.RolePassAsync(services, gateway, apply: true, Ct);
        await SyncSetUp.RolePassAsync(services, gateway, apply: true, Ct);

        Assert.Single(await services.FactsOfTypeAsync(FactType.RolesDisagree, Ct));
    }

    [Fact]
    public async Task AMemberWithNoLinkIsNeverTouched()
    {
        await using var services = await OnAsync(_db);
        await SyncSetUp.PairAsync(services, RoleSyncDecides.VRChat, Ct);

        // They hold the paired Discord role and have no VRChat account tied to them at all. The
        // deciding side says nothing about them, and nothing may be taken away.
        await SyncSetUp.UnlinkedMemberAsync(services, "6001", [SyncSetUp.DiscordRole], Ct);

        var gateway = new FakeGateway();
        var pass = await SyncSetUp.RolePassAsync(services, gateway, apply: true, Ct);

        Assert.Equal(0, pass.Found);
        Assert.Empty(gateway.RoleChanges);
        Assert.Empty(services.VRChat.Actions);
    }

    /// <summary>A role nobody paired is not Modbot's business, whoever holds it.</summary>
    [Fact]
    public async Task AnUnpairedRoleIsNeverTouched()
    {
        await using var services = await OnAsync(_db);
        await SyncSetUp.PairAsync(services, RoleSyncDecides.VRChat, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, inGroup: [], inServer: ["9999"], ct: Ct);

        var gateway = new FakeGateway();
        var pass = await SyncSetUp.RolePassAsync(services, gateway, apply: true, Ct);

        Assert.Equal(0, pass.Found);
        Assert.Empty(gateway.RoleChanges);
    }

    [Fact]
    public async Task ADryRunListsTheChangesAndMakesNone()
    {
        // The switch is off, which is exactly when somebody wants to see what turning it on would
        // do. A dry run answers anyway; only applying is gated on the switch.
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        await SyncSetUp.PairAsync(services, RoleSyncDecides.VRChat, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, inGroup: [SyncSetUp.GroupRole], inServer: [], ct: Ct);

        var gateway = new FakeGateway();
        var pass = await SyncSetUp.RolePassAsync(services, gateway, apply: false, Ct);

        Assert.Equal(1, pass.Found);
        Assert.Single(pass.Changes);
        Assert.Equal(0, pass.Given);
        Assert.Empty(gateway.RoleChanges);
        Assert.Empty(services.VRChat.Actions);
        Assert.Empty(await SyncSetUp.CopiesAsync(services, Ct));
        Assert.Empty(await services.FactsOfTypeAsync(FactType.CopiedRoleGiven, Ct));
    }

    [Fact]
    public async Task WithTheSwitchOffNothingIsApplied()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        await SyncSetUp.PairAsync(services, RoleSyncDecides.VRChat, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, inGroup: [SyncSetUp.GroupRole], inServer: [], ct: Ct);

        var gateway = new FakeGateway();
        var pass = await SyncSetUp.RolePassAsync(services, gateway, apply: true, Ct);

        Assert.Equal(0, pass.Found);
        Assert.Empty(gateway.RoleChanges);
    }

    /// <summary>
    /// A role the bot cannot assign is a setup problem named on the pair, not one refusal per
    /// member per pass (M5 §3.3).
    /// </summary>
    [Fact]
    public async Task ARoleTheBotCannotAssignIsReportedOnThePair()
    {
        await using var services = await OnAsync(_db);
        await SyncSetUp.PairAsync(services, RoleSyncDecides.VRChat, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, inGroup: [SyncSetUp.GroupRole], inServer: [], ct: Ct);

        await using (var db = services.Database.NewContext())
        {
            await db.DiscordRoles
                .Where(r => r.RoleId == SyncSetUp.DiscordRole)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.BotCanAssign, false), Ct);
        }

        var gateway = new FakeGateway();
        var pass = await SyncSetUp.RolePassAsync(services, gateway, apply: true, Ct);

        Assert.Equal(0, pass.Given);
        Assert.NotNull(pass.Problem);
        Assert.Empty(gateway.RoleChanges);

        await using var check = services.Database.NewContext();
        var pair = await check.DiscordRolePairs.AsNoTracking().SingleAsync(Ct);
        Assert.NotNull(pair.Problem);
    }

    /// <summary>
    /// Somebody linked but no longer in the group cannot have a group role mirrored onto them, so
    /// they are skipped rather than refused once a minute forever.
    /// </summary>
    [Fact]
    public async Task SomebodyNoLongerInTheGroupIsSkipped()
    {
        await using var services = await OnAsync(_db);
        await SyncSetUp.PairAsync(services, RoleSyncDecides.Discord, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, inGroup: null, inServer: [SyncSetUp.DiscordRole], ct: Ct);

        var gateway = new FakeGateway();
        var pass = await SyncSetUp.RolePassAsync(services, gateway, apply: true, Ct);

        Assert.Equal(0, pass.Found);
        Assert.Empty(services.VRChat.Actions);
    }

    /// <summary>A second pass over a settled deployment does nothing and asks nothing of anybody.</summary>
    [Fact]
    public async Task OnceBothSidesAgreeThePassDoesNothing()
    {
        await using var services = await OnAsync(_db);
        await SyncSetUp.PairAsync(services, RoleSyncDecides.VRChat, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, inGroup: [SyncSetUp.GroupRole], inServer: [SyncSetUp.DiscordRole], ct: Ct);

        var gateway = new FakeGateway();
        var pass = await SyncSetUp.RolePassAsync(services, gateway, apply: true, Ct);

        Assert.Equal(0, pass.Found);
        Assert.Empty(gateway.RoleChanges);
    }
}
