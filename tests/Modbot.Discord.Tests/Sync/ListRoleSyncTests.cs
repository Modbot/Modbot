using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Lists;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Sync;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Sync;

/// <summary>
/// Roles from lists: everybody in the list who is in the server gets the role, only a role the
/// pairing gave is ever taken away, a given-row is only forgotten when Modbot saw the person leave,
/// nothing is planned on an out-of-date member list, a VRChat account with no linked Discord is
/// skipped, a staff role is never given, many losses at once stop and wait, and with the switch off
/// nothing happens (roles from lists design).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ListRoleSyncTests
{
    /// <summary>The role a list gives. A community role: View Channels and Send Messages.</summary>
    private const string Regular = "802";

    private const long Community = (1L << 10) | (1L << 11);

    /// <summary>"In the VRChat group": who is in the list is decided by a group member row.</summary>
    private const string InGroup = """{"kind":"allOf","rules":[{"kind":"inGroup"}]}""";

    private readonly PostgresFixture _db;

    public ListRoleSyncTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(TestServices Services, Guid ListId, Guid PairingId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    /// <param name="membersRead">The bot compared the whole member list in its current connection.</param>
    /// <param name="vrchatRead">The VRChat group's member list and audit log were read lately.</param>
    private async Task<Setup> SetUpAsync(
        bool on = true,
        long? permissions = Community,
        string rules = InGroup,
        Action<Settings>? settings = null,
        bool membersRead = true,
        bool vrchatRead = true)
    {
        var services = await SyncSetUp.CreateAsync(_db, s =>
        {
            s.DiscordListRolesOn = on;
            settings?.Invoke(s);
        }, Ct);

        var now = services.Clock.UtcNow;

        if (vrchatRead)
        {
            await services.ConfigureAsync(s =>
            {
                s.MemberSweepCompletedAt = now;
                s.AuditLogPolledAt = now;
            }, Ct);
        }

        await using var db = services.Database.NewContext();

        if (membersRead)
        {
            await db.DiscordServers
                .Where(s => s.GuildId == SyncSetUp.Guild)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.MembersReadAt, now).SetProperty(s => s.SeenThrough, now), Ct);
        }

        db.DiscordRoles.Add(new DiscordRole
        {
            RoleId = Regular,
            GuildId = SyncSetUp.Guild,
            Name = "Regular",
            BotCanAssign = true,
            Permissions = permissions,
            FirstSeenAt = now,
            UpdatedAt = now,
        });

        var list = new SavedList { Id = Guid.CreateVersion7(), Name = "Regulars", Rules = rules, CreatedAt = now, UpdatedAt = now };
        db.SavedLists.Add(list);

        var pairing = new DiscordListRole
        {
            ListId = list.Id,
            DiscordRoleId = Regular,
            DiscordRoleName = "Regular",
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.DiscordListRoles.Add(pairing);

        await db.SaveChangesAsync(Ct);
        return new Setup(services, list.Id, pairing.Id);
    }

    private static async Task<ListRolePass> PassAsync(TestServices services, FakeGateway gateway)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<ListRoleSync>().RunAsync(gateway, Ct);
    }

    private static async Task<ListRolePlan> PlanAsync(TestServices services, Guid pairingId)
    {
        using var scope = services.Scope();
        var provider = scope.ServiceProvider;
        var pairing = await provider.GetRequiredService<Modbot.Core.Data.ModbotContext>().DiscordListRoles
            .AsNoTracking()
            .SingleAsync(p => p.Id == pairingId, Ct);

        return await provider.GetRequiredService<ListRolePlanner>().PlanAsync(pairing, Ct);
    }

    private static async Task<List<DiscordListRoleGiven>> GivenAsync(TestServices services)
    {
        await using var db = services.Database.NewContext();
        return await db.DiscordListRolesGiven.AsNoTracking().ToListAsync(Ct);
    }

    private static async Task<DiscordListRole> PairingAsync(TestServices services, Guid id)
    {
        await using var db = services.Database.NewContext();
        return await db.DiscordListRoles.AsNoTracking().SingleAsync(p => p.Id == id, Ct);
    }

    /// <summary>The roles the stored member row says somebody holds.</summary>
    private static async Task<string[]> StoredRolesAsync(TestServices services, string discordUserId)
    {
        await using var db = services.Database.NewContext();
        var json = await db.DiscordMembers.AsNoTracking()
            .Where(m => m.GuildId == SyncSetUp.Guild && m.UserId == discordUserId)
            .Select(m => m.Roles)
            .SingleAsync(Ct);

        return JsonSerializer.Deserialize<string[]>(json) ?? [];
    }

    /// <summary>Somebody leaves the VRChat group, and so the "in the group" list.</summary>
    private static async Task LeaveGroupAsync(TestServices services, string vrchatUserId)
    {
        await using var db = services.Database.NewContext();
        var at = services.Clock.UtcNow;

        await db.GroupMembers
            .Where(m => m.UserId == vrchatUserId)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.LeftAt, at), Ct);
    }

    /// <summary>Somebody leaves the Discord server, as the member list records a leave Modbot saw.</summary>
    private static async Task LeaveServerAsync(TestServices services, string discordUserId)
    {
        await using var db = services.Database.NewContext();
        var at = services.Clock.UtcNow;

        await db.DiscordMembers
            .Where(m => m.UserId == discordUserId)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.LeftAt, at), Ct);
    }

    private static async Task GivenRowAsync(TestServices services, Guid pairingId, string discordUserId)
    {
        await using var db = services.Database.NewContext();

        db.DiscordListRolesGiven.Add(new DiscordListRoleGiven
        {
            ListRoleId = pairingId,
            DiscordUserId = discordUserId,
            GivenAt = services.Clock.UtcNow,
        });

        await db.SaveChangesAsync(Ct);
    }

    // ── Matching ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SomebodyInTheListIsGivenTheRole_AndItIsWrittenDown()
    {
        await using var setup = await SetUpAsync();
        var services = setup.Services;
        await SyncSetUp.LinkAsync(services, "usr_in", "5001", inGroup: [], inServer: [], ct: Ct);

        var gateway = new FakeGateway();
        var pass = await PassAsync(services, gateway);

        Assert.Equal(1, pass.Given);
        var (added, _, userId, roleId) = Assert.Single(gateway.RoleChanges);
        Assert.True(added);
        Assert.Equal("5001", userId);
        Assert.Equal(Regular, roleId);
        Assert.Contains("Regulars", Assert.Single(gateway.RoleReasons), StringComparison.Ordinal);

        var row = Assert.Single(await GivenAsync(services));
        Assert.Equal(setup.PairingId, row.ListRoleId);
        Assert.Equal("5001", row.DiscordUserId);
        Assert.Equal("usr_in", row.VRChatUserId);

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.ListRoleGiven, Ct));
        Assert.Equal("5001", fact.SubjectId);
    }

    /// <summary>
    /// The stored member row follows the give at once, one role added and nothing else touched, so
    /// a second pass finds them holding it without waiting for Discord's update.
    /// </summary>
    [Fact]
    public async Task AGiveIsWrittenOntoTheMemberRow_SoTheNextPassHasNothingToDo()
    {
        await using var setup = await SetUpAsync();
        var services = setup.Services;
        await SyncSetUp.LinkAsync(services, "usr_in", "5001", inGroup: [], inServer: ["900"], ct: Ct);

        var gateway = new FakeGateway();
        await PassAsync(services, gateway);

        Assert.Equal(new[] { "900", Regular }, await StoredRolesAsync(services, "5001"));

        var second = await PassAsync(services, gateway);

        Assert.Equal(0, second.Given + second.Taken);
        Assert.Single(gateway.RoleChanges);
    }

    [Fact]
    public async Task WithTheSwitchOffNothingHappens()
    {
        await using var setup = await SetUpAsync(on: false);
        var services = setup.Services;
        await SyncSetUp.LinkAsync(services, "usr_in", "5001", inGroup: [], inServer: [], ct: Ct);
        await SyncSetUp.UnlinkedMemberAsync(services, "5009", [Regular], Ct);
        await GivenRowAsync(services, setup.PairingId, "5009");

        var gateway = new FakeGateway();
        var pass = await PassAsync(services, gateway);

        Assert.Equal(ListRolePass.Nothing, pass);
        Assert.Empty(gateway.RoleChanges);
        Assert.Empty(await services.FactsOfTypeAsync(FactType.ListRoleGiven, Ct));
        Assert.Empty(await services.FactsOfTypeAsync(FactType.ListRoleTaken, Ct));
    }

    [Fact]
    public async Task APairingSwitchedOffChangesNothing()
    {
        await using var setup = await SetUpAsync();
        var services = setup.Services;
        await SyncSetUp.LinkAsync(services, "usr_in", "5001", inGroup: [], inServer: [], ct: Ct);

        await using (var db = services.Database.NewContext())
            await db.DiscordListRoles.Where(p => p.Id == setup.PairingId).ExecuteUpdateAsync(u => u.SetProperty(p => p.Enabled, false), Ct);

        var gateway = new FakeGateway();
        await PassAsync(services, gateway);

        Assert.Empty(gateway.RoleChanges);
    }

    /// <summary>A Discord role can only go to a Discord member: a VRChat account needs a linked one.</summary>
    [Fact]
    public async Task AVRChatAccountWithNoLinkedDiscordIsSkipped_AndCounted()
    {
        await using var setup = await SetUpAsync();
        var services = setup.Services;

        await using (var db = services.Database.NewContext())
        {
            db.GroupMembers.Add(new GroupMember
            {
                GroupId = SyncSetUp.Group,
                UserId = "usr_alone",
                Roles = "[]",
                FirstSeenAt = services.Clock.UtcNow,
                LastSeenAt = services.Clock.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        var plan = await PlanAsync(services, setup.PairingId);
        Assert.Equal(1, plan.NoLinkedDiscord);
        Assert.Empty(plan.Changes);

        var gateway = new FakeGateway();
        await PassAsync(services, gateway);
        Assert.Empty(gateway.RoleChanges);
    }

    [Fact]
    public async Task ALinkedAccountNotInTheServerIsSkipped_AndCounted()
    {
        await using var setup = await SetUpAsync();
        var services = setup.Services;
        await SyncSetUp.LinkAsync(services, "usr_away", "5002", inGroup: [], inServer: null, ct: Ct);

        var plan = await PlanAsync(services, setup.PairingId);

        Assert.Equal(1, plan.NotInServer);
        Assert.Empty(plan.Changes);
    }

    // ── Only what it gave ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Design §3: leaving the list takes the role from somebody the pairing gave it to, and never
    /// from somebody who was given it by hand.
    /// </summary>
    [Fact]
    public async Task OnlyARoleThePairingGaveIsTakenAway()
    {
        await using var setup = await SetUpAsync();
        var services = setup.Services;
        await SyncSetUp.LinkAsync(services, "usr_given", "5001", inGroup: [], inServer: [], ct: Ct);
        await SyncSetUp.UnlinkedMemberAsync(services, "5003", [Regular], Ct);

        var gateway = new FakeGateway();
        await PassAsync(services, gateway);

        await LeaveGroupAsync(services, "usr_given");
        gateway.RoleChanges.Clear();
        var pass = await PassAsync(services, gateway);

        Assert.Equal(1, pass.Taken);
        var (added, _, userId, _) = Assert.Single(gateway.RoleChanges);
        Assert.False(added);
        Assert.Equal("5001", userId);

        Assert.Empty(await StoredRolesAsync(services, "5001"));
        Assert.Equal(new[] { Regular }, await StoredRolesAsync(services, "5003"));
        Assert.Empty(await GivenAsync(services));
        Assert.Single(await services.FactsOfTypeAsync(FactType.ListRoleTaken, Ct));
    }

    [Fact]
    public async Task SomebodyWhoAlreadyHasTheRoleIsNotWrittenDown()
    {
        await using var setup = await SetUpAsync();
        var services = setup.Services;
        await SyncSetUp.LinkAsync(services, "usr_has", "5004", inGroup: [], inServer: [Regular], ct: Ct);

        var gateway = new FakeGateway();
        await PassAsync(services, gateway);

        Assert.Empty(gateway.RoleChanges);
        Assert.Empty(await GivenAsync(services));
        Assert.Equal(1, (await PlanAsync(services, setup.PairingId)).AlreadyHave);
    }

    /// <summary>
    /// A moderator who takes the role off by hand is not overruled a minute later. The pass writes
    /// the role onto the member row itself; the moderator's removal arrives as a member update.
    /// </summary>
    [Fact]
    public async Task ARoleTakenOffByHandIsNotGivenAgain()
    {
        await using var setup = await SetUpAsync();
        var services = setup.Services;
        await SyncSetUp.LinkAsync(services, "usr_in", "5001", inGroup: [], inServer: [], ct: Ct);

        var gateway = new FakeGateway();
        await PassAsync(services, gateway);
        Assert.Equal(new[] { Regular }, await StoredRolesAsync(services, "5001"));

        // A moderator takes it off in Discord; the member update records it.
        await SyncSetUp.SetServerRolesAsync(services, "5001", [], Ct);

        gateway.RoleChanges.Clear();
        await PassAsync(services, gateway);

        Assert.Empty(gateway.RoleChanges);
        Assert.Single(await GivenAsync(services));
        Assert.Equal(1, (await PlanAsync(services, setup.PairingId)).TakenByHand);
    }

    /// <summary>
    /// A member row without the role is not proof the role is gone: out of the list, Modbot still
    /// asks Discord to take it away rather than quietly forgetting it gave it. Discord changed
    /// nothing Modbot can tell, so no fact says a role was taken.
    /// </summary>
    [Fact]
    public async Task AMemberRowWithoutTheRoleIsNotTakenAsProof()
    {
        await using var setup = await SetUpAsync();
        var services = setup.Services;
        await SyncSetUp.UnlinkedMemberAsync(services, "5007", [], Ct);
        await GivenRowAsync(services, setup.PairingId, "5007");

        var gateway = new FakeGateway();
        var pass = await PassAsync(services, gateway);

        var (added, _, userId, _) = Assert.Single(gateway.RoleChanges);
        Assert.False(added);
        Assert.Equal("5007", userId);

        Assert.Equal(0, pass.Taken);
        Assert.Empty(await services.FactsOfTypeAsync(FactType.ListRoleTaken, Ct));
        Assert.Empty(await GivenAsync(services));
    }

    [Fact]
    public async Task SomebodySeenLeavingTheServerIsForgotten_WithNothingSent()
    {
        await using var setup = await SetUpAsync();
        var services = setup.Services;
        await SyncSetUp.UnlinkedMemberAsync(services, "5005", [Regular], Ct);
        await GivenRowAsync(services, setup.PairingId, "5005");
        await LeaveServerAsync(services, "5005");

        var gateway = new FakeGateway();
        await PassAsync(services, gateway);

        Assert.Empty(gateway.RoleChanges);
        Assert.Empty(await GivenAsync(services));
    }

    /// <summary>With no member row at all, Modbot never saw them leave, and keeps the row.</summary>
    [Fact]
    public async Task SomebodyNeverSeenLeavingKeepsTheirRow()
    {
        await using var setup = await SetUpAsync();
        var services = setup.Services;
        await GivenRowAsync(services, setup.PairingId, "5008");

        var gateway = new FakeGateway();
        await PassAsync(services, gateway);

        Assert.Empty(gateway.RoleChanges);
        Assert.Single(await GivenAsync(services));
    }

    // ── An out-of-date member list ─────────────────────────────────────────────────────────

    /// <summary>
    /// Until the bot has compared the whole member list in its current connection, nothing is
    /// given, taken or forgotten.
    /// </summary>
    [Fact]
    public async Task NothingHappensUntilTheMemberListIsRead()
    {
        await using var setup = await SetUpAsync(membersRead: false);
        var services = setup.Services;
        await SyncSetUp.LinkAsync(services, "usr_in", "5001", inGroup: [], inServer: [], ct: Ct);
        await SyncSetUp.UnlinkedMemberAsync(services, "5005", [Regular], Ct);
        await GivenRowAsync(services, setup.PairingId, "5005");
        await LeaveServerAsync(services, "5005");

        var gateway = new FakeGateway();
        var pass = await PassAsync(services, gateway);

        Assert.Empty(gateway.RoleChanges);
        Assert.Single(await GivenAsync(services));
        Assert.Equal("Waiting for the member list.", pass.Problem);
        Assert.Equal("Waiting for the member list.", (await PairingAsync(services, setup.PairingId)).Problem);
    }

    /// <summary>A member list read long ago, with the bot no longer noting it is listening, is out of date too.</summary>
    [Fact]
    public async Task AMemberListTheBotStoppedListeningToIsOutOfDate()
    {
        await using var setup = await SetUpAsync();
        var services = setup.Services;
        await SyncSetUp.LinkAsync(services, "usr_in", "5001", inGroup: [], inServer: [], ct: Ct);

        services.Clock.Advance(ListRolePlanner.MembersFreshFor + TimeSpan.FromMinutes(1));

        var gateway = new FakeGateway();
        await PassAsync(services, gateway);

        Assert.Empty(gateway.RoleChanges);
    }

    /// <summary>
    /// A list asking about VRChat, over a VRChat member list not read lately, takes nobody's role:
    /// they may only look gone. Gives still go ahead.
    /// </summary>
    [Fact]
    public async Task AnOldVRChatMemberListTakesNothing_ButStillGives()
    {
        await using var setup = await SetUpAsync(vrchatRead: false);
        var services = setup.Services;
        await SyncSetUp.LinkAsync(services, "usr_in", "5001", inGroup: [], inServer: [], ct: Ct);
        await SyncSetUp.UnlinkedMemberAsync(services, "5003", [Regular], Ct);
        await GivenRowAsync(services, setup.PairingId, "5003");

        var plan = await PlanAsync(services, setup.PairingId);
        Assert.Equal(1, plan.TakesHeld);
        Assert.NotNull(plan.HeldBecause);

        var gateway = new FakeGateway();
        var pass = await PassAsync(services, gateway);

        Assert.Equal(1, pass.Given);
        Assert.Equal(0, pass.Taken);
        var (added, _, userId, _) = Assert.Single(gateway.RoleChanges);
        Assert.True(added);
        Assert.Equal("5001", userId);
    }

    // ── Which lists and roles ──────────────────────────────────────────────────────────────

    /// <summary>A role that gained a staff permission after it was paired stops being given.</summary>
    [Fact]
    public async Task ARoleWithAStaffPermissionIsNeverGiven()
    {
        await using var setup = await SetUpAsync(permissions: Community | (1L << 2));
        var services = setup.Services;
        await SyncSetUp.LinkAsync(services, "usr_in", "5001", inGroup: [], inServer: [], ct: Ct);

        var gateway = new FakeGateway();
        var pass = await PassAsync(services, gateway);

        Assert.Empty(gateway.RoleChanges);
        Assert.Contains("Ban Members", pass.Problem, StringComparison.Ordinal);
        Assert.Contains("Ban Members", (await PairingAsync(services, setup.PairingId)).Problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// A list that cannot be answered is never read as "nobody", which would take the role from
    /// everybody it was given to.
    /// </summary>
    [Fact]
    public async Task AListThatCannotBeAnsweredChangesNothing()
    {
        await using var setup = await SetUpAsync(
            rules: """{"kind":"allOf","rules":[{"kind":"notSeenWithinDays","amount":30}]}""",
            settings: s => s.PresenceFactRetentionDays = 90);
        var services = setup.Services;
        await SyncSetUp.UnlinkedMemberAsync(services, "5006", [Regular], Ct);
        await GivenRowAsync(services, setup.PairingId, "5006");

        var gateway = new FakeGateway();
        var pass = await PassAsync(services, gateway);

        Assert.Empty(gateway.RoleChanges);
        Assert.NotNull(pass.Problem);
        Assert.Single(await GivenAsync(services));
    }

    /// <summary>
    /// Rules that let everybody in, and rules that cannot be read, are never read as "everybody":
    /// nothing is given.
    /// </summary>
    [Theory]
    [InlineData("""{"kind":"allOf","rules":[]}""")]
    [InlineData("""{"kind":"anyOf","rules":[{"kind":"allOf","rules":[]}]}""")]
    [InlineData("""{"kind":"noSuchRule"}""")]
    public async Task ListsThatLetEverybodyInOrCannotBeReadGiveNothing(string rules)
    {
        await using var setup = await SetUpAsync(rules: rules);
        var services = setup.Services;
        await SyncSetUp.LinkAsync(services, "usr_in", "5001", inGroup: [], inServer: [], ct: Ct);
        await SyncSetUp.UnlinkedMemberAsync(services, "5002", [], Ct);

        var gateway = new FakeGateway();
        var pass = await PassAsync(services, gateway);

        Assert.Empty(gateway.RoleChanges);
        Assert.NotNull(pass.Problem);
    }

    /// <summary>
    /// "In list B" where B lets everybody in is a list that lets everybody in, found once B is
    /// written out.
    /// </summary>
    [Fact]
    public async Task AListThatOnlyNamesAnEmptyListGivesNothing()
    {
        var empty = Guid.CreateVersion7();
        await using var setup = await SetUpAsync(
            rules: $$"""{"kind":"allOf","rules":[{"kind":"inList","id":"{{empty}}"}]}""");
        var services = setup.Services;

        await using (var db = services.Database.NewContext())
        {
            db.SavedLists.Add(new SavedList
            {
                Id = empty,
                Name = "Nothing",
                Rules = """{"kind":"allOf","rules":[]}""",
                CreatedAt = services.Clock.UtcNow,
                UpdatedAt = services.Clock.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        await SyncSetUp.UnlinkedMemberAsync(services, "5002", [], Ct);

        var gateway = new FakeGateway();
        var pass = await PassAsync(services, gateway);

        Assert.Empty(gateway.RoleChanges);
        Assert.Equal("That list lets everybody in. Give it rules first.", pass.Problem);
    }

    // ── The brake ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The give brake: a list that would hand the role to more than 100 people and most of the
    /// server at once stops and waits, with nothing sent.
    /// </summary>
    [Fact]
    public async Task GivingTheRoleToMostOfTheServerAtOnceStops()
    {
        await using var setup = await SetUpAsync(rules: """{"kind":"noneOf","rules":[{"kind":"inGroup"}]}""");
        var services = setup.Services;

        await using (var db = services.Database.NewContext())
        {
            for (var i = 0; i < 101; i++)
            {
                db.DiscordMembers.Add(new DiscordMember
                {
                    GuildId = SyncSetUp.Guild,
                    UserId = $"6{i:000}",
                    Username = $"member6{i:000}",
                    DisplayName = $"Member 6{i:000}",
                    Roles = "[]",
                    FirstSeenAt = services.Clock.UtcNow,
                    UpdatedAt = services.Clock.UtcNow,
                });
            }

            await db.SaveChangesAsync(Ct);
        }

        var plan = await PlanAsync(services, setup.PairingId);
        Assert.True(plan.GiveStops);

        var gateway = new FakeGateway();
        var pass = await PassAsync(services, gateway);

        Assert.Equal(1, pass.Stopped);
        Assert.Empty(gateway.RoleChanges);

        // What Apply writes for the gives it allowed: then they go, at the pass's pace.
        await using (var db = services.Database.NewContext())
            await db.DiscordListRoles.Where(p => p.Id == setup.PairingId).ExecuteUpdateAsync(u => u.SetProperty(p => p.GivesAllowed, 101), Ct);

        var applied = await PassAsync(services, gateway);

        Assert.Equal(0, applied.Stopped);
        Assert.Equal(ListRoleSync.MaxChangesPerPass, applied.Given);
        Assert.Equal(101 - ListRoleSync.MaxChangesPerPass, (await PairingAsync(services, setup.PairingId)).GivesAllowed);
    }

    /// <summary>
    /// Design §5: taking the role from most of its holders at once stops the pairing, says so once,
    /// and waits; Apply allows that many, the next pass carries on, and the allowance is used up.
    /// </summary>
    [Fact]
    public async Task ManyRemovalsAtOnceStop_UntilApplyAllowsThem()
    {
        await using var setup = await SetUpAsync();
        var services = setup.Services;
        var pairingId = setup.PairingId;

        foreach (var id in new[] { "5101", "5102", "5103", "5104" })
        {
            await SyncSetUp.UnlinkedMemberAsync(services, id, [Regular], Ct);
            await GivenRowAsync(services, pairingId, id);
        }

        // Somebody new in the list is not given the role while the pairing is stopped either.
        await SyncSetUp.LinkAsync(services, "usr_new", "5001", inGroup: [], inServer: [], ct: Ct);

        var gateway = new FakeGateway();
        var first = await PassAsync(services, gateway);
        var second = await PassAsync(services, gateway);

        Assert.Equal(1, first.Stopped);
        Assert.Equal(5, first.Left);
        Assert.Equal(1, second.Stopped);
        Assert.Empty(gateway.RoleChanges);
        Assert.Single(await services.FactsOfTypeAsync(FactType.ListRoleStopped, Ct));

        var stopped = await PairingAsync(services, pairingId);
        Assert.NotNull(stopped.StoppedAt);
        Assert.Equal(4, stopped.StoppedTaking);

        // What Apply writes: the removals somebody looked at.
        await using (var db = services.Database.NewContext())
            await db.DiscordListRoles.Where(p => p.Id == pairingId).ExecuteUpdateAsync(u => u.SetProperty(p => p.RemovalsAllowed, 4), Ct);

        var applied = await PassAsync(services, gateway);

        Assert.Equal(4, applied.Taken);
        Assert.Equal(1, applied.Given);
        Assert.Equal(0, applied.Stopped);

        var after = await PairingAsync(services, pairingId);
        Assert.Null(after.StoppedAt);
        Assert.Equal(0, after.RemovalsAllowed);
    }

    [Fact]
    public async Task MoreRemovalsThanApplyAllowedStopAgain()
    {
        await using var setup = await SetUpAsync();
        var services = setup.Services;

        foreach (var id in new[] { "5101", "5102", "5103", "5104" })
        {
            await SyncSetUp.UnlinkedMemberAsync(services, id, [Regular], Ct);
            await GivenRowAsync(services, setup.PairingId, id);
        }

        await using (var db = services.Database.NewContext())
            await db.DiscordListRoles.Where(p => p.Id == setup.PairingId).ExecuteUpdateAsync(u => u.SetProperty(p => p.RemovalsAllowed, 3), Ct);

        var gateway = new FakeGateway();
        var pass = await PassAsync(services, gateway);

        Assert.Equal(1, pass.Stopped);
        Assert.Empty(gateway.RoleChanges);
    }

    /// <summary>
    /// A member list that suddenly shows everybody gone stops the pairing before a single given-row
    /// is forgotten, so Modbot can still take the roles back later.
    /// </summary>
    [Fact]
    public async Task ManyPeopleSeenLeavingAtOnceStopBeforeAnythingIsForgotten()
    {
        await using var setup = await SetUpAsync();
        var services = setup.Services;

        foreach (var id in new[] { "5101", "5102", "5103", "5104" })
        {
            await SyncSetUp.UnlinkedMemberAsync(services, id, [Regular], Ct);
            await GivenRowAsync(services, setup.PairingId, id);
            await LeaveServerAsync(services, id);
        }

        var gateway = new FakeGateway();
        var pass = await PassAsync(services, gateway);

        Assert.Equal(1, pass.Stopped);
        Assert.Equal(4, (await GivenAsync(services)).Count);
    }

    /// <summary>A few people drifting out of a big role is normal and is not braked.</summary>
    [Fact]
    public async Task AFewRemovalsFromABigRoleGoThrough()
    {
        await using var setup = await SetUpAsync();
        var services = setup.Services;

        for (var i = 0; i < 10; i++)
            await SyncSetUp.LinkAsync(services, $"usr_stay{i}", $"52{i:00}", inGroup: [], inServer: [Regular], ct: Ct);

        await SyncSetUp.UnlinkedMemberAsync(services, "5301", [Regular], Ct);
        await GivenRowAsync(services, setup.PairingId, "5301");

        var gateway = new FakeGateway();
        var pass = await PassAsync(services, gateway);

        Assert.Equal(0, pass.Stopped);
        Assert.Equal(1, pass.Taken);
    }

    /// <summary>A change Discord refuses stops the pairing for this pass, and the rest count as left.</summary>
    [Fact]
    public async Task AFailedChangeLeavesTheRestForLater()
    {
        await using var setup = await SetUpAsync();
        var services = setup.Services;

        foreach (var (vrchat, discord) in new[] { ("usr_a", "5001"), ("usr_b", "5002"), ("usr_c", "5003") })
            await SyncSetUp.LinkAsync(services, vrchat, discord, inGroup: [], inServer: [], ct: Ct);

        var gateway = new FakeGateway { RoleError = "Discord said no." };
        var pass = await PassAsync(services, gateway);

        Assert.Equal(0, pass.Given);
        Assert.Equal(3, pass.Left);
        Assert.Equal("Discord said no.", pass.Problem);
        Assert.Empty(await GivenAsync(services));
    }

    // ── The preview ────────────────────────────────────────────────────────────────────────

    /// <summary>The preview is the pass's own plan: what it lists is what the pass does.</summary>
    [Fact]
    public async Task ThePreviewListsWhatThePassDoes()
    {
        await using var setup = await SetUpAsync();
        var services = setup.Services;
        await SyncSetUp.LinkAsync(services, "usr_a", "5001", inGroup: [], inServer: [], ct: Ct);
        await SyncSetUp.LinkAsync(services, "usr_b", "5002", inGroup: [], inServer: [], ct: Ct);
        await SyncSetUp.UnlinkedMemberAsync(services, "5003", [Regular], Ct);
        await GivenRowAsync(services, setup.PairingId, "5003");

        var plan = await PlanAsync(services, setup.PairingId);

        // Nothing was sent or written by looking.
        Assert.Empty(await services.FactsOfTypeAsync(FactType.ListRoleGiven, Ct));
        Assert.Single(await GivenAsync(services));

        var gateway = new FakeGateway();
        var pass = await PassAsync(services, gateway);

        Assert.Equal(plan.Giving, pass.Given);
        Assert.Equal(plan.Taking, pass.Taken);
        Assert.Equal(
            plan.Changes.Select(c => (c.What == ListRoleChangeKinds.Give, c.DiscordUserId)).Order(),
            gateway.RoleChanges.Select(c => (c.Added, c.UserId)).Order());
    }

    /// <summary>A pairing not saved yet has given nothing, so its preview only gives.</summary>
    [Fact]
    public async Task ANewPairingsPreviewOnlyGives()
    {
        await using var setup = await SetUpAsync();
        var services = setup.Services;
        await SyncSetUp.LinkAsync(services, "usr_a", "5001", inGroup: [], inServer: [], ct: Ct);

        await using (var db = services.Database.NewContext())
            await db.DiscordListRoles.Where(p => p.Id == setup.PairingId).ExecuteDeleteAsync(Ct);

        using var scope = services.Scope();
        var plan = await scope.ServiceProvider.GetRequiredService<ListRolePlanner>().PlanNewAsync(setup.ListId, Regular, Ct);

        Assert.Null(plan.Problem);
        Assert.Equal(1, plan.Giving);
        Assert.Equal(0, plan.Taking);
    }
}
