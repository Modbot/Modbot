using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.AutomaticRoles;
using Modbot.Api.Features.Companion.Alerts;
using Modbot.Api.Features.Companion.ClipRequests;
using Modbot.Api.Features.Companion.Live;
using Modbot.Core.AutomaticRoles;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Moderation;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.AutomaticRoles;

/// <summary>
/// Giving and taking the VRChat role of a rule that holds while nobody has banned today ("daily ban
/// cap" of 0), with the group's stored members as the only view of who holds what.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AutomaticRoleGiverTests
{
    private const string Group = "grp_automatic_roles_test";

    private static readonly DateTimeOffset Noon = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _db;

    public AutomaticRoleGiverTests(PostgresFixture db) => _db = db;

    /// <summary>Remembers every VRChat call, including the ones it fails.</summary>
    private sealed class FakeVRChat : IVRChatModerationActions
    {
        public List<(string What, string UserId, string RoleId)> Attempts { get; } = [];

        public bool Fails { get; set; }

        public Task<VRChatActionOutcome> BanFromGroupAsync(string userId, string reason, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<VRChatActionOutcome> RemoveFromGroupAsync(string userId, string reason, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<VRChatActionOutcome> UnbanFromGroupAsync(string userId, string reason, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<VRChatActionOutcome> GiveGroupRoleAsync(string userId, string roleId, CancellationToken ct = default)
            => Do("give", userId, roleId);

        public Task<VRChatActionOutcome> TakeGroupRoleAsync(string userId, string roleId, CancellationToken ct = default)
            => Do("take", userId, roleId);

        private Task<VRChatActionOutcome> Do(string what, string userId, string roleId)
        {
            Attempts.Add((what, userId, roleId));
            return Task.FromResult(Fails ? VRChatActionOutcome.Failed("VRChat said no") : VRChatActionOutcome.Ok);
        }
    }

    private sealed record Scene(FakeClock Clock, FakeVRChat VRChat, AutomaticRole Rule, ModbotUser Moderator, string Role);

    /// <summary>A clean slate: no rules, grants or group members, and one moderator in the group who holds nothing.</summary>
    private async Task<Scene> StartAsync(CancellationToken ct, bool onTakeBackList = false)
    {
        await using var db = _db.NewContext();
        var clock = new FakeClock(Noon);

        await new EventPartitionMaintainer(db, clock).EnsureAsync(ct);
        await db.AutomaticRoleGrants.ExecuteDeleteAsync(ct);
        await db.AutomaticRoles.ExecuteDeleteAsync(ct);
        await db.GroupMembers.Where(m => m.GroupId == Group).ExecuteDeleteAsync(ct);

        var settings = await db.GetSettingsAsync(ct);
        settings.ManagedGroupId = Group;
        await db.SaveChangesAsync(ct);

        var moderator = await TestAccounts.CreateAsync(
            db, $"giver_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.Ban, linked: true, ct);

        var role = $"grol_{Guid.NewGuid():N}";
        var rule = new AutomaticRole
        {
            Name = "Quiet day",
            VRChatRoleId = role,
            WholeTeam = true,
            DailyBanCap = true,
            DailyBanCapMax = 0,
            TakeBackUserIds = onTakeBackList ? [moderator.Id] : [],
            CreatedAt = Noon,
            UpdatedAt = Noon,
        };
        db.AutomaticRoles.Add(rule);
        await db.SaveChangesAsync(ct);

        return new Scene(clock, new FakeVRChat(), rule, moderator, role);
    }

    private async Task SetRolesAsync(Scene scene, bool holds, CancellationToken ct)
    {
        await using var db = _db.NewContext();
        var member = await db.GroupMembers.SingleOrDefaultAsync(m => m.GroupId == Group && m.UserId == scene.Moderator.VRChatUserId, ct);
        if (member is null)
        {
            member = new GroupMember { GroupId = Group, UserId = scene.Moderator.VRChatUserId!, FirstSeenAt = Noon, LastSeenAt = Noon };
            db.GroupMembers.Add(member);
        }

        member.Roles = holds ? $"[\"{scene.Role}\"]" : "[]";
        await db.SaveChangesAsync(ct);
    }

    private async Task BanAsync(Scene scene, CancellationToken ct)
    {
        await using var db = _db.NewContext();
        db.Events.Add(new ModbotEvent
        {
            Type = FactType.ActionBan,
            OccurredAt = scene.Clock.UtcNow.AddHours(-1),
            ObservedAt = scene.Clock.UtcNow.AddHours(-1),
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = $"usr_banned_{Guid.NewGuid():N}",
            ActorPlatform = FactPlatform.Modbot,
            ActorId = scene.Moderator.Id.ToString(),
            Source = FactSource.Manual,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task OpenGrantAsync(Scene scene, DateTimeOffset givenAt, CancellationToken ct)
    {
        await using var db = _db.NewContext();
        await new AutomaticRoleGrantStore(db).OpenAsync(
            scene.Rule.Id, scene.Moderator.Id, AutomaticRoleKind.VRChat, scene.Role, givenAt, ct);
    }

    private async Task RunAsync(Scene scene, DateTimeOffset startedAt, CancellationToken ct)
    {
        await using var db = _db.NewContext();
        var giver = new AutomaticRoleGiver(
            db,
            scene.Clock,
            new ModeratorFactsGatherer(
                db, scene.Clock, new DeviceLocations(), new CompanionConnections(), new ClipSettingsReports(), new ClipRequestStore(db)),
            new AutomaticRoleGrantStore(db),
            scene.VRChat,
            new NoDiscordAutomaticRoleActions(),
            new FactWriter(db, scene.Clock),
            new EventPartitionMaintainer(db, scene.Clock));

        await giver.RunOnceAsync(startedAt, ct);
    }

    private async Task<AutomaticRoleGrant> GrantAsync(Scene scene, CancellationToken ct)
    {
        await using var db = _db.NewContext();
        return await db.AutomaticRoleGrants.AsNoTracking().SingleAsync(g => g.ModbotUserId == scene.Moderator.Id, ct);
    }

    private async Task<int> FactsAsync(Scene scene, string type, CancellationToken ct)
    {
        await using var db = _db.NewContext();
        return await db.Events.CountAsync(e => e.Type == type && e.SubjectId == scene.Moderator.Id.ToString(), ct);
    }

    private static readonly DateTimeOffset LongAgo = Noon.AddHours(-3);

    [Fact]
    public async Task GivesTheRoleWhenTheRuleHolds()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await StartAsync(ct);
        await SetRolesAsync(scene, holds: false, ct);

        await RunAsync(scene, LongAgo, ct);

        Assert.Equal([("give", scene.Moderator.VRChatUserId!, scene.Role)], scene.VRChat.Attempts);
        var grant = await GrantAsync(scene, ct);
        Assert.Equal(scene.Rule.Id, grant.RuleId);
        Assert.Null(grant.TakenAt);
        Assert.Equal(1, await FactsAsync(scene, FactType.AutomaticRoleGiven, ct));
    }

    [Fact]
    public async Task ARoleJustGivenIsNotGivenAgainWhileTheStoredViewLags()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await StartAsync(ct);
        await SetRolesAsync(scene, holds: false, ct);

        await RunAsync(scene, LongAgo, ct);
        scene.Clock.Advance(TimeSpan.FromMinutes(1));
        await RunAsync(scene, LongAgo, ct);

        Assert.Single(scene.VRChat.Attempts);
    }

    [Fact]
    public async Task TakesTheRoleModbotGaveAndRecordsWhy()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await StartAsync(ct);
        await SetRolesAsync(scene, holds: true, ct);
        await OpenGrantAsync(scene, Noon.AddHours(-2), ct);
        await BanAsync(scene, ct);

        await RunAsync(scene, LongAgo, ct);

        Assert.Equal([("take", scene.Moderator.VRChatUserId!, scene.Role)], scene.VRChat.Attempts);
        var grant = await GrantAsync(scene, ct);
        Assert.Equal(Noon, grant.TakenAt);
        Assert.Equal(AutomaticRoleReasons.DailyBanCapReached, grant.TakeReason);
        Assert.Equal(1, await FactsAsync(scene, FactType.AutomaticRoleTaken, ct));
    }

    [Fact]
    public async Task AFailedVRChatCallIsNotRetriedAndStopsVRChatForThePass()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await StartAsync(ct);
        await SetRolesAsync(scene, holds: false, ct);

        // A second moderator who also needs the role: the pass must not even try for them.
        await using (var db = _db.NewContext())
        {
            var other = await TestAccounts.CreateAsync(
                db, $"giver_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.Ban, linked: true, ct);
            db.GroupMembers.Add(new GroupMember { GroupId = Group, UserId = other.VRChatUserId!, FirstSeenAt = Noon, LastSeenAt = Noon });
            await db.SaveChangesAsync(ct);
        }

        scene.VRChat.Fails = true;

        await RunAsync(scene, LongAgo, ct);

        Assert.Single(scene.VRChat.Attempts);
        await using var check = _db.NewContext();
        Assert.Empty(await check.AutomaticRoleGrants.ToListAsync(ct));
        Assert.Equal(0, await FactsAsync(scene, FactType.AutomaticRoleGiven, ct));
    }

    [Fact]
    public async Task NothingIsTakenInTheFirstTwoMinutesAfterModbotStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await StartAsync(ct);
        await SetRolesAsync(scene, holds: true, ct);
        await OpenGrantAsync(scene, Noon.AddHours(-2), ct);
        await BanAsync(scene, ct);

        await RunAsync(scene, Noon.AddMinutes(-1), ct);

        Assert.Empty(scene.VRChat.Attempts);
        Assert.Null((await GrantAsync(scene, ct)).TakenAt);
    }

    [Fact]
    public async Task ARoleGivenByHandIsLeftAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await StartAsync(ct);
        await SetRolesAsync(scene, holds: true, ct);
        await BanAsync(scene, ct);

        await RunAsync(scene, LongAgo, ct);

        Assert.Empty(scene.VRChat.Attempts);
    }

    [Fact]
    public async Task ARoleGivenByHandIsTakenWhenThePersonIsOnTheTakeBackList()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await StartAsync(ct, onTakeBackList: true);
        await SetRolesAsync(scene, holds: true, ct);
        await BanAsync(scene, ct);

        await RunAsync(scene, LongAgo, ct);

        Assert.Equal([("take", scene.Moderator.VRChatUserId!, scene.Role)], scene.VRChat.Attempts);
        var grant = await GrantAsync(scene, ct);
        Assert.Equal(Noon, grant.TakenAt);
        Assert.Equal(AutomaticRoleReasons.DailyBanCapReached, grant.TakeReason);
    }

    [Fact]
    public async Task TheRoleIsGivenBackOnlyAMinuteAfterItWasTaken()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await StartAsync(ct);
        await SetRolesAsync(scene, holds: false, ct);
        await using (var db = _db.NewContext())
        {
            await new AutomaticRoleGrantStore(db).RecordTakeAsync(
                scene.Rule.Id, scene.Moderator.Id, AutomaticRoleKind.VRChat, scene.Role, Noon.AddSeconds(-30), "Test", ct);
        }

        await RunAsync(scene, LongAgo, ct);
        Assert.Empty(scene.VRChat.Attempts);

        scene.Clock.Advance(TimeSpan.FromSeconds(40));
        await RunAsync(scene, LongAgo, ct);

        Assert.Equal([("give", scene.Moderator.VRChatUserId!, scene.Role)], scene.VRChat.Attempts);
    }

    [Fact]
    public async Task NoCallIsMadeWhenNothingChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await StartAsync(ct);
        await SetRolesAsync(scene, holds: true, ct);
        await OpenGrantAsync(scene, Noon.AddHours(-2), ct);

        await RunAsync(scene, LongAgo, ct);

        Assert.Empty(scene.VRChat.Attempts);
    }

    [Fact]
    public async Task TakesTheRoleFromAnAccountThatWasTurnedOff()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await StartAsync(ct);
        await SetRolesAsync(scene, holds: true, ct);
        await OpenGrantAsync(scene, Noon.AddHours(-2), ct);
        await using (var db = _db.NewContext())
        {
            await db.Users.Where(u => u.Id == scene.Moderator.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDisabled, true), ct);
        }

        await RunAsync(scene, LongAgo, ct);

        Assert.Equal([("take", scene.Moderator.VRChatUserId!, scene.Role)], scene.VRChat.Attempts);
        Assert.Equal(AutomaticRoleReasons.AccountTurnedOff, (await GrantAsync(scene, ct)).TakeReason);
    }
}
