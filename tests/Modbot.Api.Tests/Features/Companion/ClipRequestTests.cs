using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Companion.Alerts;
using Modbot.Api.Features.Companion.ClipRequests;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Evidence.Options;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Companion;

/// <summary>
/// A clip with every action: which actions ask for one, from whose computer, and how the wait ends.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ClipRequestTests
{
    private const string Person = "usr_person";
    private const string World = "wrld_cats";
    private const string Instance = "39911";

    private static readonly DateTimeOffset Noon = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _db;

    public ClipRequestTests(PostgresFixture db) => _db = db;

    /// <summary>A moderator with one paired computer, the settings, and the cursor placed after everything already there.</summary>
    private sealed class Scene
    {
        public required FakeClock Clock { get; init; }
        public required DeviceLocations Locations { get; init; }
        public required ModbotUser Moderator { get; init; }
        public required Guid Device { get; init; }
        public required string ModeratorVRChatId { get; init; }
    }

    private async Task<Scene> StartAsync(bool bans, bool kicks, bool warnings, bool evidence, CancellationToken ct)
    {
        await using var db = _db.NewContext();
        var clock = new FakeClock(Noon);

        await new EventPartitionMaintainer(db, clock).EnsureAsync(ct);
        await db.ClipRequests.ExecuteDeleteAsync(ct);
        await db.ClipRequestCursors.ExecuteDeleteAsync(ct);

        var settings = await db.GetSettingsAsync(ct);
        settings.ClipOnBans = bans;
        settings.ClipOnKicks = kicks;
        settings.ClipOnWarnings = warnings;
        settings.EvidenceBackend = (short)(evidence ? EvidenceBackend.Filesystem : EvidenceBackend.None);
        settings.VRChatAccountUserId = "usr_modbot_service";

        var moderator = await TestAccounts.CreateAsync(
            db, $"clipmod_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.PairCompanion, linked: true, ct);

        var device = Guid.NewGuid();
        db.CompanionDevices.Add(new CompanionDeviceRecord
        {
            Id = device,
            TokenHash = Guid.NewGuid().ToString("N"),
            IssuedToUserId = moderator.Id,
            IssuedAt = Noon.AddDays(-1),
            LastSeenAt = Noon,
        });
        await db.SaveChangesAsync(ct);

        // The first pass only places the cursor after every fact already in the log.
        await IntakeAsync(clock, new DeviceLocations(), ct);

        return new Scene
        {
            Clock = clock,
            Locations = new DeviceLocations(),
            Moderator = moderator,
            Device = device,
            ModeratorVRChatId = moderator.VRChatUserId!,
        };
    }

    private async Task IntakeAsync(FakeClock clock, DeviceLocations locations, CancellationToken ct)
    {
        await using var db = _db.NewContext();
        var intake = new ClipRequestIntake(db, clock, locations, new ClipRequestStore(db));
        await intake.RunOnceAsync(ct);
    }

    private async Task<long> AddFactAsync(
        string type, FactPlatform actorPlatform, string actorId, DateTimeOffset at, CancellationToken ct,
        string? instance = null)
    {
        await using var db = _db.NewContext();
        var fact = new ModbotEvent
        {
            Type = type,
            OccurredAt = at,
            ObservedAt = at.AddMinutes(-5),
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = Person,
            ActorPlatform = actorPlatform,
            ActorId = actorId,
            WorldId = instance is null ? null : World,
            InstanceId = instance,
            Source = FactSource.Manual,
        };
        db.Events.Add(fact);
        await db.SaveChangesAsync(ct);
        return fact.Id;
    }

    private async Task<ClipRequest?> RequestFor(long factId, CancellationToken ct)
    {
        await using var db = _db.NewContext();
        return await db.ClipRequests.AsNoTracking().FirstOrDefaultAsync(r => r.FactId == factId, ct);
    }

    [Fact]
    public async Task ABanMadeInModbotAsksTheModeratorsComputerThatWasInTheInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await StartAsync(bans: true, kicks: false, warnings: false, evidence: true, ct);

        var at = Noon.AddSeconds(-30);
        await AddFactAsync(FactType.InstanceJoined, FactPlatform.VRChat, Person, Noon.AddMinutes(-20), ct, Instance);
        scene.Locations.Record(scene.Device, Instance, Noon.AddMinutes(-1), World);
        var ban = await AddFactAsync(FactType.ActionBan, FactPlatform.Modbot, scene.Moderator.Id.ToString(), at, ct);

        await IntakeAsync(scene.Clock, scene.Locations, ct);

        var request = await RequestFor(ban, ct);
        Assert.NotNull(request);
        Assert.Equal(ClipStatus.Asked, request.Status);
        Assert.Equal(scene.Device, request.DeviceId);
        Assert.Equal(scene.Moderator.Id, request.ModeratorUserId);
        Assert.Equal(ClipAction.Ban, request.Action);
        Assert.Equal(Instance, request.InstanceId);
        Assert.Equal(at, request.ActionAt);
        Assert.Equal(Noon, request.RequestedAt);
    }

    [Fact]
    public async Task NoComputerInTheInstanceAsksNobodyAndShowsNotInTheInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await StartAsync(bans: true, kicks: false, warnings: false, evidence: true, ct);

        await AddFactAsync(FactType.InstanceJoined, FactPlatform.VRChat, Person, Noon.AddMinutes(-20), ct, Instance);

        // The moderator's computer last said it was somewhere else.
        scene.Locations.Record(scene.Device, "00000", Noon.AddMinutes(-1), World);
        var ban = await AddFactAsync(FactType.ActionBan, FactPlatform.Modbot, scene.Moderator.Id.ToString(), Noon.AddSeconds(-30), ct);

        await IntakeAsync(scene.Clock, scene.Locations, ct);

        var request = await RequestFor(ban, ct);
        Assert.NotNull(request);
        Assert.Equal(ClipStatus.NotInInstance, request.Status);
        Assert.Null(request.DeviceId);
        Assert.Null(request.RequestedAt);
        Assert.False(ClipStatusLabels.IsMissed(request.Status));
    }

    [Fact]
    public async Task AComputerLastSeenInTheInstanceMoreThanTenMinutesAgoDoesNotCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await StartAsync(bans: true, kicks: false, warnings: false, evidence: true, ct);

        await AddFactAsync(FactType.InstanceJoined, FactPlatform.VRChat, Person, Noon.AddMinutes(-40), ct, Instance);
        scene.Locations.Record(scene.Device, Instance, Noon.AddMinutes(-11), World);
        var ban = await AddFactAsync(FactType.ActionBan, FactPlatform.Modbot, scene.Moderator.Id.ToString(), Noon.AddSeconds(-30), ct);

        await IntakeAsync(scene.Clock, scene.Locations, ct);

        Assert.Equal(ClipStatus.NotInInstance, (await RequestFor(ban, ct))!.Status);
    }

    [Fact]
    public async Task AnActionTheGroupDidNotTickAsksNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await StartAsync(bans: false, kicks: true, warnings: false, evidence: true, ct);

        await AddFactAsync(FactType.InstanceJoined, FactPlatform.VRChat, Person, Noon.AddMinutes(-20), ct, Instance);
        scene.Locations.Record(scene.Device, Instance, Noon.AddMinutes(-1), World);
        var ban = await AddFactAsync(FactType.ActionBan, FactPlatform.Modbot, scene.Moderator.Id.ToString(), Noon.AddSeconds(-30), ct);

        await IntakeAsync(scene.Clock, scene.Locations, ct);

        Assert.Null(await RequestFor(ban, ct));
    }

    [Fact]
    public async Task WithoutEvidenceStorageNothingIsAsked()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await StartAsync(bans: true, kicks: true, warnings: true, evidence: false, ct);

        await AddFactAsync(FactType.InstanceJoined, FactPlatform.VRChat, Person, Noon.AddMinutes(-20), ct, Instance);
        scene.Locations.Record(scene.Device, Instance, Noon.AddMinutes(-1), World);
        var ban = await AddFactAsync(FactType.ActionBan, FactPlatform.Modbot, scene.Moderator.Id.ToString(), Noon.AddSeconds(-30), ct);

        await IntakeAsync(scene.Clock, scene.Locations, ct);

        Assert.Null(await RequestFor(ban, ct));
    }

    [Fact]
    public async Task AWarningInVRChatsOwnMenuAsksTheModeratorItsActorIsLinkedTo()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await StartAsync(bans: false, kicks: false, warnings: true, evidence: true, ct);

        scene.Locations.Record(scene.Device, Instance, Noon.AddMinutes(-1), World);
        var warning = await AddFactAsync(
            FactType.GroupInstanceWarn, FactPlatform.VRChat, scene.ModeratorVRChatId, Noon.AddSeconds(-20), ct, Instance);

        await IntakeAsync(scene.Clock, scene.Locations, ct);

        var request = await RequestFor(warning, ct);
        Assert.NotNull(request);
        Assert.Equal(ClipAction.Warning, request.Action);
        Assert.Equal(ClipStatus.Asked, request.Status);
        Assert.Equal(scene.Moderator.Id, request.ModeratorUserId);
    }

    [Fact]
    public async Task VRChatsRecordOfAnActionMadeInModbotIsNotAskedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await StartAsync(bans: true, kicks: true, warnings: false, evidence: true, ct);

        scene.Locations.Record(scene.Device, Instance, Noon.AddMinutes(-1), World);

        // VRChat attributes a ban made in Modbot to Modbot's own account; its instance kick likewise.
        var banned = await AddFactAsync(FactType.MemberBanned, FactPlatform.VRChat, "usr_modbot_service", Noon.AddSeconds(-20), ct);
        var kick = await AddFactAsync(FactType.GroupInstanceKick, FactPlatform.VRChat, "usr_modbot_service", Noon.AddSeconds(-20), ct, Instance);

        await IntakeAsync(scene.Clock, scene.Locations, ct);

        Assert.Null(await RequestFor(banned, ct));
        Assert.Null(await RequestFor(kick, ct));
    }

    [Fact]
    public async Task ABanInVRChatsMenuIsOneRequestAndTheInstanceKickBesideItIsNotAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await StartAsync(bans: true, kicks: true, warnings: false, evidence: true, ct);

        scene.Locations.Record(scene.Device, Instance, Noon.AddMinutes(-1), World);
        var banned = await AddFactAsync(FactType.MemberBanned, FactPlatform.VRChat, scene.ModeratorVRChatId, Noon.AddSeconds(-20), ct);
        var kick = await AddFactAsync(FactType.GroupInstanceKick, FactPlatform.VRChat, scene.ModeratorVRChatId, Noon.AddSeconds(-20), ct, Instance);

        await IntakeAsync(scene.Clock, scene.Locations, ct);

        var request = await RequestFor(banned, ct);
        Assert.NotNull(request);
        Assert.Equal(ClipStatus.Asked, request.Status);

        // The ban found the person's instance from the kick beside it.
        Assert.Equal(Instance, request.InstanceId);
        Assert.Null(await RequestFor(kick, ct));
    }

    [Fact]
    public async Task ABanInVRChatsMenuWithBansNotTickedStillAsksForTheInstanceKickWhenKicksAre()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await StartAsync(bans: false, kicks: true, warnings: false, evidence: true, ct);

        scene.Locations.Record(scene.Device, Instance, Noon.AddMinutes(-1), World);
        var banned = await AddFactAsync(FactType.MemberBanned, FactPlatform.VRChat, scene.ModeratorVRChatId, Noon.AddSeconds(-20), ct);
        var kick = await AddFactAsync(FactType.GroupInstanceKick, FactPlatform.VRChat, scene.ModeratorVRChatId, Noon.AddSeconds(-20), ct, Instance);

        await IntakeAsync(scene.Clock, scene.Locations, ct);

        Assert.Null(await RequestFor(banned, ct));
        var request = await RequestFor(kick, ct);
        Assert.NotNull(request);
        Assert.Equal(ClipAction.Kick, request.Action);
        Assert.Equal(ClipStatus.Asked, request.Status);
    }

    [Fact]
    public async Task AFactReadTwiceAsksOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await StartAsync(bans: true, kicks: false, warnings: false, evidence: true, ct);

        await AddFactAsync(FactType.InstanceJoined, FactPlatform.VRChat, Person, Noon.AddMinutes(-20), ct, Instance);
        scene.Locations.Record(scene.Device, Instance, Noon.AddMinutes(-1), World);
        await AddFactAsync(FactType.ActionBan, FactPlatform.Modbot, scene.Moderator.Id.ToString(), Noon.AddSeconds(-30), ct);

        await IntakeAsync(scene.Clock, scene.Locations, ct);
        await IntakeAsync(scene.Clock, scene.Locations, ct);

        await using var db = _db.NewContext();
        Assert.Equal(1, await db.ClipRequests.CountAsync(ct));
    }

    // ── The answer ──────────────────────────────────────────────────────────────────────────

    private async Task<(Scene Scene, Guid RequestId)> AskedAsync(CancellationToken ct)
    {
        var scene = await StartAsync(bans: true, kicks: false, warnings: false, evidence: true, ct);

        await AddFactAsync(FactType.InstanceJoined, FactPlatform.VRChat, Person, Noon.AddMinutes(-20), ct, Instance);
        scene.Locations.Record(scene.Device, Instance, Noon.AddMinutes(-1), World);
        var ban = await AddFactAsync(FactType.ActionBan, FactPlatform.Modbot, scene.Moderator.Id.ToString(), Noon.AddSeconds(-30), ct);
        await IntakeAsync(scene.Clock, scene.Locations, ct);

        return (scene, (await RequestFor(ban, ct))!.Id);
    }

    [Fact]
    public async Task TheRightDeviceCanAnswerOnceAndOnlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var (scene, id) = await AskedAsync(ct);
        var hash = new string('a', 64);

        await using var db = _db.NewContext();
        var store = new ClipRequestStore(db);

        Assert.True(await store.AnswerAsync(id, scene.Device, ClipStatus.Attached, hash, Noon.AddMinutes(1), ct));
        Assert.False(await store.AnswerAsync(id, scene.Device, ClipStatus.Attached, hash, Noon.AddMinutes(1), ct));

        var row = await db.ClipRequests.AsNoTracking().SingleAsync(r => r.Id == id, ct);
        Assert.Equal(ClipStatus.Attached, row.Status);
        Assert.Equal(hash, row.BlobHash);
        Assert.Empty(await store.PendingForAsync(scene.Device, Noon.AddMinutes(1), ct));
    }

    [Fact]
    public async Task AnotherDeviceOrAnotherIdIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (scene, id) = await AskedAsync(ct);

        await using var db = _db.NewContext();
        var store = new ClipRequestStore(db);

        Assert.False(await store.AnswerAsync(id, Guid.NewGuid(), ClipStatus.ClipsOff, null, Noon.AddMinutes(1), ct));
        Assert.False(await store.AnswerAsync(Guid.NewGuid(), scene.Device, ClipStatus.ClipsOff, null, Noon.AddMinutes(1), ct));
        Assert.Null(await store.OpenAsync(id, Guid.NewGuid(), Noon.AddMinutes(1), ct));
        Assert.NotNull(await store.OpenAsync(id, scene.Device, Noon.AddMinutes(1), ct));
    }

    [Fact]
    public async Task AnAnswerAfterFifteenMinutesIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (scene, id) = await AskedAsync(ct);

        await using var db = _db.NewContext();
        var store = new ClipRequestStore(db);
        var late = Noon.AddMinutes(15).AddSeconds(1);

        Assert.False(await store.AnswerAsync(id, scene.Device, ClipStatus.Attached, new string('b', 64), late, ct));
        Assert.Null(await store.OpenAsync(id, scene.Device, late, ct));
        Assert.True(await store.AnswerAsync(id, scene.Device, ClipStatus.ClipsOff, null, Noon.AddMinutes(15), ct));
    }

    [Fact]
    public async Task NoAnswerInFifteenMinutesIsCompanionDidNotAnswerAndCountsAsMissed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (scene, id) = await AskedAsync(ct);

        await using var db = _db.NewContext();
        var store = new ClipRequestStore(db);

        Assert.Equal(0, await store.ExpireAsync(Noon.AddMinutes(15), ct));
        Assert.Equal(0, await store.MissedCountAsync(scene.Moderator.Id, Noon.AddDays(-1), ct));

        Assert.Equal(1, await store.ExpireAsync(Noon.AddMinutes(15).AddSeconds(1), ct));

        var row = await db.ClipRequests.AsNoTracking().SingleAsync(r => r.Id == id, ct);
        Assert.Equal(ClipStatus.DidNotAnswer, row.Status);
        Assert.Equal("Companion didn't answer", ClipStatusLabels.Of(row.Status));
        Assert.Equal(1, await store.MissedCountAsync(scene.Moderator.Id, Noon.AddDays(-1), ct));
        Assert.Equal(0, await store.MissedCountAsync(scene.Moderator.Id, Noon.AddDays(1), ct));
    }
}
