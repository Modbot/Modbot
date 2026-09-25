using System.Text.Json.Nodes;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Live;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;
using VRChat.API.Model;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// How long somebody had been in the instance, written onto the kick as it is recorded.
/// </summary>
/// <remarks>
/// Both kinds of kick end up here. A moderator pressing Kick in VRChat and a moderator pressing it
/// in Modbot both leave VRChat writing <c>group.instance.kick</c> into the group's audit log, and
/// this producer is what reads it. Modbot's own <c>modbot.action.kick</c> is a group removal with
/// no instance in it and carries no duration of its own.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class KickTimingTests(PostgresFixture fixture) : SyncTestBase(fixture)
{
    private const string World = "wrld_blackcat";
    private const string Number = "39047";
    private const string Location = $"{World}:{Number}~group({GroupId})~groupAccessType(members)";

    private const string Kicked = "usr_target";
    private const string Moderator = "usr_ada";

    /// <summary>A kick entry, with the location VRChat puts in <c>auditData</c> for one.</summary>
    private static GroupAuditLogEntry KickEntry(string id, DateTimeOffset at) => new()
    {
        Id = id,
        GroupId = GroupId,
        EventType = GroupAuditLogEvents.InstanceKick,
        ActorId = "usr_moderator",
        ActorDisplayName = "Moderator",
        TargetId = Kicked,
        CreatedAt = at.UtcDateTime,
        Description = "Moderator has issued an instance kick for Target.",
        Data = new JsonObject { ["location"] = Location }.ToJsonString(),
    };

    /// <summary>A moderator with a paired companion, the way pairing leaves the tables.</summary>
    private async Task<Guid> PairedCompanionAsync()
    {
        await using var context = Database.NewContext();

        var user = new ModbotUser
        {
            Username = "ada",
            UsernameNormalized = "ada",
            PasswordHash = "x",
            VRChatUserId = Moderator,
            VRChatLinkedAt = Now.AddDays(-1),
            CreatedAt = Now.AddDays(-1),
        };

        var device = Guid.NewGuid();

        context.Users.Add(user);
        context.CompanionDevices.Add(new CompanionDeviceRecord
        {
            Id = device,
            TokenHash = Guid.NewGuid().ToString("n"),
            CompanionVersion = "2026.9.0",
            Platform = "windows",
            IssuedToUserId = user.Id,
            IssuedAt = Now.AddDays(-1),
        });

        await context.SaveChangesAsync(Ct);
        return device;
    }

    /// <summary>One presence fact, as a companion's report reaches the fact log.</summary>
    private async Task ReportAsync(string type, string subject, DateTimeOffset at, Guid device)
    {
        await using var context = Database.NewContext();

        await new FactWriter(context, Clock).WriteAsync(
            new FactRecord
            {
                Type = type,
                OccurredAt = at,
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = subject,
                WorldId = World,
                InstanceId = Number,
                Source = FactSource.Companion,
                Data = new JsonObject { [ClientReport.DeviceIdKey] = device.ToString() },
            },
            Ct);

        await context.SaveChangesAsync(Ct);
    }

    private async Task<ModbotEvent> TheKickAsync()
        => Assert.Single(await FactsOfTypeAsync(FactType.GroupInstanceKick));

    private static (long? Seconds, bool? SeenArriving) Timing(ModbotEvent fact)
    {
        var data = JsonNode.Parse(fact.Data ?? "{}") as JsonObject;

        return (
            data?[TimeInInstance.SecondsKey]?.GetValue<long>(),
            data?[TimeInInstance.SeenArrivingKey]?.GetValue<bool>());
    }

    /// <summary>
    /// The companion watched them walk in forty minutes earlier, so the kick records exactly that.
    /// </summary>
    [Fact]
    public async Task AKickFromVRChatsAuditLogRecordsHowLongTheyHadBeenThere()
    {
        var device = await PairedCompanionAsync();

        await ReportAsync(FactType.InstanceJoined, Moderator, Now.AddMinutes(-50), device);
        await ReportAsync(FactType.InstanceJoined, Kicked, Now.AddMinutes(-45), device);

        VRChat.Groups.Add(KickEntry("gaud_kick", Now.AddMinutes(-5)));

        await RunAuditLogAsync();

        var (seconds, seenArriving) = Timing(await TheKickAsync());

        Assert.Equal(40 * 60, seconds);
        Assert.True(seenArriving);
    }

    /// <summary>
    /// The one that matters more than the feature: with nobody's companion reporting, the kick
    /// says nothing rather than zero.
    /// </summary>
    [Fact]
    public async Task AKickWithNoPresenceDataRecordsNothing()
    {
        VRChat.Groups.Add(KickEntry("gaud_kick", Now.AddMinutes(-5)));

        await RunAuditLogAsync();

        var (seconds, seenArriving) = Timing(await TheKickAsync());

        Assert.Null(seconds);
        Assert.Null(seenArriving);
    }

    /// <summary>
    /// Presence facts nobody's paired companion reported cannot start a watch, so the roster is
    /// not believed and the kick still says nothing.
    /// </summary>
    [Fact]
    public async Task PresenceFromNoPairedCompanionRecordsNothing()
    {
        await ReportAsync(FactType.InstanceJoined, Kicked, Now.AddMinutes(-45), Guid.NewGuid());

        VRChat.Groups.Add(KickEntry("gaud_kick", Now.AddMinutes(-5)));

        await RunAuditLogAsync();

        var (seconds, _) = Timing(await TheKickAsync());

        Assert.Null(seconds);
    }

    /// <summary>
    /// A companion that only ever found them already present knows a floor, not a measurement, and
    /// the kick records which it is.
    /// </summary>
    [Fact]
    public async Task OnlySeenAlreadyThere_IsRecordedAsALowerBound()
    {
        var device = await PairedCompanionAsync();

        await ReportAsync(FactType.InstancePresenceObserved, Kicked, Now.AddMinutes(-20), device);
        await ReportAsync(FactType.InstanceJoined, Moderator, Now.AddMinutes(-20), device);

        VRChat.Groups.Add(KickEntry("gaud_kick", Now.AddMinutes(-5)));

        await RunAuditLogAsync();

        var (seconds, seenArriving) = Timing(await TheKickAsync());

        Assert.Equal(15 * 60, seconds);
        Assert.False(seenArriving);
    }

    /// <summary>
    /// The kick throws them out, so their own leave lands on top of it. Counting it would answer
    /// "they were not there" about the person who was just thrown out.
    /// </summary>
    [Fact]
    public async Task TheirOwnLeaveAtTheKickDoesNotEraseTheAnswer()
    {
        var device = await PairedCompanionAsync();

        await ReportAsync(FactType.InstanceJoined, Moderator, Now.AddMinutes(-50), device);
        await ReportAsync(FactType.InstanceJoined, Kicked, Now.AddMinutes(-45), device);
        await ReportAsync(FactType.InstanceLeft, Kicked, Now.AddMinutes(-5), device);

        VRChat.Groups.Add(KickEntry("gaud_kick", Now.AddMinutes(-5)));

        await RunAuditLogAsync();

        var (seconds, _) = Timing(await TheKickAsync());

        Assert.Equal(40 * 60, seconds);
    }

    /// <summary>
    /// A ban is not an instance event and has no instance to measure against, so nothing is added
    /// to it — the instance kick VRChat writes beside it is what carries the duration.
    /// </summary>
    [Fact]
    public async Task ABanIsLeftAlone()
    {
        var device = await PairedCompanionAsync();

        await ReportAsync(FactType.InstanceJoined, Moderator, Now.AddMinutes(-50), device);
        await ReportAsync(FactType.InstanceJoined, Kicked, Now.AddMinutes(-45), device);

        VRChat.Groups.Add(Entry("gaud_ban", Now.AddMinutes(-5), GroupAuditLogEvents.UserBan));

        await RunAuditLogAsync();

        var ban = Assert.Single(await FactsOfTypeAsync(FactType.MemberBanned));
        var (seconds, _) = Timing(ban);

        Assert.Null(seconds);
    }
}
