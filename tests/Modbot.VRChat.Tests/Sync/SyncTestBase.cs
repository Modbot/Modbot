using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;
using Modbot.VRChat.Tests.Fakes;
using Modbot.VRChat.Users;
using VRChat.API.Model;
using GroupMember = VRChat.API.Model.GroupMember;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// A real database, a real gate, a real rate limiter, and a scripted VRChat behind them.
/// </summary>
/// <remarks>
/// <para>
/// The producers are wired up the way the host wires them, rather than against substitutes for
/// the gate and the writer. Two of the things most worth proving -- that a 429 cold-stops one
/// producer and not the other, and that a restart mid-sync loses nothing -- are properties of the
/// limiter and the fact log, and a test that stubbed either would assert only that the producer
/// calls the method it calls.
/// </para>
/// <para>
/// One <see cref="FakeClock"/> throughout. The gate, the limiter and the producers must agree
/// about what time it is, and the entire reason <c>IModbotClock</c> exists is that a system with
/// two clocks produces plausible-looking wrong answers (spec 4.4).
/// </para>
/// </remarks>
public abstract class SyncTestBase : IAsyncLifetime
{
    /// <summary>
    /// Fixed rather than "now", like the analytics suites': the fact log is partitioned by month,
    /// and a test that quietly depends on today's date starts failing on the first of a month for
    /// reasons nobody will connect to this file.
    /// </summary>
    protected static readonly DateTimeOffset Now = new(2026, 6, 15, 14, 0, 0, TimeSpan.Zero);

    protected const string GroupId = "grp_test";

    protected SyncTestBase(PostgresFixture fixture) => Fixture = fixture;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected PostgresFixture Fixture { get; }

    protected IsolatedDatabase Database { get; private set; } = null!;

    protected FakeClock Clock { get; } = new(Now);

    protected FakeVRChat VRChat { get; private set; } = null!;

    protected LimiterHarness Limiter { get; private set; } = null!;

    protected SyncDiagnostics Diagnostics { get; private set; } = null!;

    /// <summary>One queue per test, like one process: the API and the producer would share it.</summary>
    protected UserRefreshQueue Queue { get; } = new();

    /// <summary>The profile sync's options for this test. Unclamped defaults unless a test says otherwise.</summary>
    protected UserProfileSyncOptions ProfileOptions { get; set; } = new();

    private VRChatGate _gate = null!;

    /// <summary>The gate the producers use, for producers a test builds itself.</summary>
    protected IVRChatGate Gate => _gate;

    public async ValueTask InitializeAsync()
    {
        Database = await IsolatedDatabase.CreateAsync(Fixture, Ct);

        VRChat = new FakeVRChat().AlwaysSignedInAs();
        Diagnostics = new SyncDiagnostics(Clock);

        // Unpaced: these tests are about what the producers do with what they read, and pacing
        // would make every one of them wait out spec 4.2's eight seconds for nothing. The cold
        // stop still works -- it is triggered by a reported 429, not by the token bucket.
        Limiter = new LimiterHarness(LimiterHarness.Unpaced(), Clock);

        _gate = new VRChatGate(
            new FakeClientFactory(VRChat.Client),
            new FakeConnectionStore(),
            Limiter.Limiter,
            Clock,
            new FakeMonotonicClock());

        await using var context = Database.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        settings.ManagedGroupId = GroupId;
        await context.SaveChangesAsync(Ct);

        await new EventPartitionMaintainer(context, Clock).EnsureAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        await Database.DisposeAsync();
    }

    /// <summary>
    /// One audit-log pass, in its own scope, the way the hosted service runs it.
    /// </summary>
    /// <remarks>
    /// A fresh <see cref="ModbotContext"/> per pass because that is what a scoped producer gets.
    /// Reusing one would let a test pass on entities the change tracker still remembered, which
    /// is precisely the state a restart destroys.
    /// </remarks>
    protected async Task<AuditLogRunResult> RunAuditLogAsync(AuditLogSyncOptions? options = null)
    {
        await using var context = Database.NewContext();

        var sync = new GroupAuditLogSync(
            _gate,
            new FactWriter(context, Clock),
            new EventPartitionMaintainer(context, Clock),
            context,
            Clock,
            Diagnostics,
            options ?? NoCatchUp());

        return await sync.RunOnceAsync(Ct);
    }

    protected async Task<GroupInfoRunResult> RunGroupInfoAsync()
    {
        await using var context = Database.NewContext();

        var sync = new GroupInfoSync(
            _gate,
            new FactWriter(context, Clock),
            new EventPartitionMaintainer(context, Clock),
            context,
            Clock);

        return await sync.RunOnceAsync(Ct);
    }

    /// <summary>
    /// One profile-sync pass, in its own scope, the way the hosted service runs it.
    /// </summary>
    /// <param name="housekeeping">
    /// On by default, so a test that seeds rows straight into the table and expects them refreshed
    /// gets the top-up that finds them. Off for tests about the cheap pass.
    /// </param>
    protected async Task<UserProfileRunResult> RunUserProfileAsync(bool housekeeping = true)
    {
        await using var context = Database.NewContext();

        var sync = new UserProfileSync(
            _gate,
            context,
            ProfilesFor(context),
            Queue,
            Clock,
            Diagnostics,
            ProfileOptions);

        return await sync.RunOnceAsync(housekeeping, Ct);
    }

    /// <summary>One poll of the group's instance list, in its own scope.</summary>
    protected async Task<GroupInstanceRunResult> RunGroupInstancesAsync()
    {
        await using var context = Database.NewContext();
        return await new GroupInstanceSync(_gate, new PlaceStore(context, Clock), context, Clock).RunOnceAsync(Ct);
    }

    /// <summary>One pass of the instance head count read, in its own scope.</summary>
    protected async Task<InstanceHeadCountRunResult> RunInstanceHeadCountsAsync()
    {
        await using var context = Database.NewContext();
        return await new InstanceHeadCountSync(_gate, context, Clock).RunOnceAsync(Ct);
    }

    /// <summary>The member sweep's options for this test. Unpaced page overlap and size as shipped unless a test says otherwise.</summary>
    protected GroupMemberSyncOptions MemberOptions { get; set; } = new();

    protected GroupBanSyncOptions BanOptions { get; set; } = new();

    /// <summary>One pass of the member sweep -- one page, or the end of a sweep -- in its own scope.</summary>
    protected async Task<SweepRunResult> RunMemberSweepAsync()
    {
        await using var context = Database.NewContext();

        var sync = new GroupMemberSync(
            _gate,
            new FactWriter(context, Clock),
            new EventPartitionMaintainer(context, Clock),
            context,
            ProfilesFor(context),
            Clock,
            MemberOptions);

        return await sync.RunOnceAsync(Ct);
    }

    protected async Task<SweepRunResult> RunBanSweepAsync()
    {
        await using var context = Database.NewContext();

        var sync = new GroupBanSync(
            _gate,
            new FactWriter(context, Clock),
            new EventPartitionMaintainer(context, Clock),
            context,
            ProfilesFor(context),
            Clock,
            BanOptions);

        return await sync.RunOnceAsync(Ct);
    }

    /// <summary>
    /// A whole member sweep, page by page, the clock moving on by the page delay between pages
    /// the way the service would move it. Returns the pass that finished it.
    /// </summary>
    protected async Task<SweepRunResult> SweepMembersAsync()
    {
        for (var pages = 0; pages < 200; pages++)
        {
            var result = await RunMemberSweepAsync();

            if (result.SweepComplete || result.Outcome is SyncOutcome.RateLimited or SyncOutcome.Failed or SyncOutcome.NotConfigured || result.RestUntil is not null)
                return result;

            Clock.Advance(MemberOptions.Clamped().PageDelay);
        }

        throw new InvalidOperationException("The member sweep did not finish in 200 pages.");
    }

    protected async Task<SweepRunResult> SweepBansAsync()
    {
        for (var pages = 0; pages < 200; pages++)
        {
            var result = await RunBanSweepAsync();

            if (result.SweepComplete || result.Outcome is SyncOutcome.RateLimited or SyncOutcome.Failed or SyncOutcome.NotConfigured || result.RestUntil is not null)
                return result;

            Clock.Advance(BanOptions.Clamped().PageDelay);
        }

        throw new InvalidOperationException("The ban sweep did not finish in 200 pages.");
    }

    /// <summary>Moves the clock past the rest between member sweeps, so the next pass starts a new one.</summary>
    protected void RestMembers() => Clock.Advance(MemberOptions.Clamped().RestBetweenSweeps + TimeSpan.FromSeconds(1));

    protected void RestBans() => Clock.Advance(BanOptions.Clamped().RestBetweenSweeps + TimeSpan.FromSeconds(1));

    /// <summary>A member list entry, with everything a row needs and nothing a test has to repeat.</summary>
    protected static GroupMember Member(string userId, DateTimeOffset? joinedAt = null, params string[] roles) => new()
    {
        Id = $"gmem_{userId}",
        GroupId = GroupId,
        UserId = userId,
        JoinedAt = (joinedAt ?? Now.AddDays(-30)).UtcDateTime,
        RoleIds = [.. roles],
        MRoleIds = [],
        MembershipStatus = GroupMemberStatus.Member,
        Visibility = "visible",
        IsRepresenting = false,
    };

    /// <summary>A ban list entry.</summary>
    protected static GroupMember Ban(string userId, DateTimeOffset? bannedAt = null) => new()
    {
        Id = $"gmem_{userId}",
        GroupId = GroupId,
        UserId = userId,
        BannedAt = (bannedAt ?? Now.AddDays(-10)).UtcDateTime,
        RoleIds = [],
        MRoleIds = [],
        MembershipStatus = GroupMemberStatus.Banned,
        Visibility = "hidden",
        IsRepresenting = false,
    };

    protected async Task<Core.Data.Entities.GroupMember?> MemberRowAsync(string userId)
    {
        await using var context = Database.NewContext();
        return await context.GroupMembers.AsNoTracking().FirstOrDefaultAsync(m => m.GroupId == GroupId && m.UserId == userId, Ct);
    }

    protected async Task<Core.Data.Entities.GroupBan?> BanRowAsync(string userId)
    {
        await using var context = Database.NewContext();
        return await context.GroupBans.AsNoTracking().FirstOrDefaultAsync(b => b.GroupId == GroupId && b.UserId == userId, Ct);
    }

    protected async Task<IReadOnlyList<ModbotEvent>> FactsOfTypeAsync(string type) =>
        (await FactsAsync()).Where(f => f.Type == type).ToList();

    /// <summary>The user-record writer over a context, the way the API and the sync both get it.</summary>
    protected VRChatUserProfiles ProfilesFor(ModbotContext context) => new(
        context,
        new FactWriter(context, Clock),
        new EventPartitionMaintainer(context, Clock),
        Clock,
        Queue,
        ProfileOptions);

    protected async Task<VRChatUser?> UserRowAsync(string userId)
    {
        await using var context = Database.NewContext();
        return await context.VRChatUsers.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId, Ct);
    }

    /// <summary>
    /// The default for tests about the tail poll: the catch-up is a separate concern with its own
    /// tests, and leaving it on would make every other test's first pass a catch-up page.
    /// </summary>
    protected static AuditLogSyncOptions NoCatchUp() => new() { CatchUp = false };

    protected async Task<IReadOnlyList<ModbotEvent>> FactsAsync()
    {
        await using var context = Database.NewContext();

        return await context.Events
            .AsNoTracking()
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Id)
            .ToListAsync(Ct);
    }

    protected async Task<Settings> SettingsAsync()
    {
        await using var context = Database.NewContext();
        return await context.GetSettingsAsync(Ct);
    }

    /// <summary>An audit entry, with everything a fact needs and nothing a test has to repeat.</summary>
    protected static GroupAuditLogEntry Entry(
        string id,
        DateTimeOffset at,
        string eventType = GroupAuditLogEvents.UserBan,
        string target = "usr_target",
        string actor = "usr_moderator") => new()
        {
            Id = id,
            GroupId = GroupId,
            EventType = eventType,
            ActorId = actor,
            ActorDisplayName = "Moderator",
            TargetId = target,
            CreatedAt = at.UtcDateTime,
            Description = eventType,
        };
}
