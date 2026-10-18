using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Audit;
using Modbot.Api.Features.Companion.Devices;
using Modbot.Api.Features.Live.Stream;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Companion;

/// <summary>
/// What a paired companion that asks for moderation is sent besides presence: the audit log's facts
/// about its own instance and about the people seen in it, by its owner's permissions
/// (<see cref="InstanceAudit"/>). Read by long poll, which shares the reader with the socket.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CompanionAuditFeedTests
{
    private readonly PostgresFixture _db;

    public CompanionAuditFeedTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Instance = "39911";

    private const string World = "wrld_4b34";

    private const string OtherInstance = "11111";

    /// <summary>A moderator whose device has watched the instance since before anybody below arrived.</summary>
    private sealed record Setup(CompanionApiTestHost Host, string Token, Guid DeviceId, long Before, Guid OwnerId, string DeviceHash);

    private async Task<Setup> ReadyAsync(ModbotPermissions ownerHolds)
    {
        await CompanionApiTestHost.ResetAsync(_db, Ct);
        var host = await CompanionApiTestHost.StartAsync(_db);
        await host.ConfigureGroupAsync(_db, "grp_cats", Ct);

        // The reset empties the log while the id sequence carries on; one fact first, as any
        // deployment has (see CompanionLiveTests).
        await WriteAsync(host, FactType.InstanceLeft, Subject(), instance: "0", world: World);

        var owner = await host.CreateOwnerAsync(ModbotPermissions.PairCompanion | ownerHolds, Ct);
        var (token, deviceId) = await host.PairDeviceToAsync(owner.Id, Ct);

        // The moderator's own arrival, reported by their device: this is the watch's start.
        var before = await WriteAsync(
            host, FactType.InstanceJoined, owner.VRChatUserId!, instance: Instance, world: World, reporter: deviceId);

        return new Setup(host, token, deviceId, before, owner.Id, DeviceTokens.Hash(token));
    }

    private static string Subject() => $"usr_{Guid.NewGuid():N}";

    private static async Task<long> WriteAsync(
        CompanionApiTestHost host,
        string type,
        string subject,
        FactPlatform platform = FactPlatform.VRChat,
        string? instance = null,
        string? world = null,
        string? actor = null,
        Guid? reporter = null,
        FactSource source = FactSource.Companion)
    {
        var data = new JsonObject();
        if (reporter is { } device)
            data["deviceId"] = device.ToString();

        using var scope = host.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IFactWriter>().WriteAsync(new FactRecord
        {
            Type = type,
            OccurredAt = host.Clock.UtcNow,
            SubjectPlatform = platform,
            SubjectId = subject,
            ActorPlatform = actor is null ? null : FactPlatform.VRChat,
            ActorId = actor,
            WorldId = world,
            InstanceId = instance,
            Source = source,
            Data = data,
        }, Ct);

        return result.Id;
    }

    /// <summary>Somebody who arrives in the instance, reported by the moderator's device.</summary>
    private static async Task<string> SomebodyArrivesAsync(Setup setup)
    {
        var person = Subject();
        await WriteAsync(setup.Host, FactType.InstanceJoined, person, instance: Instance, world: World, reporter: setup.DeviceId);
        return person;
    }

    private static async Task<List<JsonElement>> PollAsync(Setup setup, bool moderation = true, int wait = 0)
    {
        var response = await setup.Host.Client.SendAsync(
            setup.Host.WithToken(
                HttpMethod.Get,
                $"/api/v1/companion/poll?instanceId={Instance}&worldId={World}&after={setup.Before}&wait={wait}"
                + (moderation ? "&moderation=true" : ""),
                setup.Token),
            Ct);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        return [.. (await ApiTestHost.BodyOf(response, Ct)).GetProperty("events").EnumerateArray()];
    }

    private static IEnumerable<string?> Types(IEnumerable<JsonElement> events) => events.Select(e => e.GetProperty("type").GetString());

    [Fact]
    public async Task AnOwnerWhoMayReadTheAuditLog_IsSentAKickInItsInstance_WithTheSentenceAndItsSource()
    {
        var setup = await ReadyAsync(ModbotPermissions.ViewAuditLog);
        await using var keep = setup.Host;

        var kicked = Subject();
        await WriteAsync(
            setup.Host, FactType.GroupInstanceKick, kicked, instance: Instance, world: World, actor: Subject(), source: FactSource.AuditLog);

        var kick = Assert.Single(await PollAsync(setup), e => e.GetProperty("type").GetString() == FactType.GroupInstanceKick);

        Assert.Equal("VRChat", kick.GetProperty("origin").GetString());
        Assert.Contains("kicked", kick.GetProperty("sentence").GetString());
        Assert.Contains(kicked, kick.GetProperty("sentence").GetString());
    }

    [Fact]
    public async Task AGroupJoinByAPersonSeenInTheInstance_IsSent_AndByAPersonNeverSeenThereIsNot()
    {
        var setup = await ReadyAsync(ModbotPermissions.ViewAuditLog);
        await using var keep = setup.Host;

        var seen = await SomebodyArrivesAsync(setup);
        await WriteAsync(setup.Host, FactType.MemberJoined, seen, source: FactSource.AuditLog);
        await WriteAsync(setup.Host, FactType.MemberJoined, Subject(), source: FactSource.AuditLog);

        var joins = (await PollAsync(setup)).Where(e => e.GetProperty("type").GetString() == FactType.MemberJoined).ToList();

        var join = Assert.Single(joins);
        Assert.Equal(seen, join.GetProperty("subject").GetProperty("id").GetString());
        Assert.Equal("VRChat", join.GetProperty("origin").GetString());
    }

    [Fact]
    public async Task ADiscordBan_OfAnAccountLinkedToAPersonSeenInTheInstance_IsSent_WithItsSourceDiscord()
    {
        var setup = await ReadyAsync(ModbotPermissions.ViewAuditLog);
        await using var keep = setup.Host;

        var seen = await SomebodyArrivesAsync(setup);

        await using (var context = setup.Host.Database)
        {
            context.DiscordAccountLinks.Add(new DiscordAccountLink
            {
                DiscordUserId = "111222333",
                DiscordUsername = "rin",
                VRChatUserId = seen,
                LinkedAt = setup.Host.Clock.UtcNow,
            });
            await context.SaveChangesAsync(Ct);
        }

        await WriteAsync(
            setup.Host, FactType.DiscordMemberBanned, "111222333", platform: FactPlatform.Discord, source: FactSource.Discord);
        await WriteAsync(
            setup.Host, FactType.DiscordMemberBanned, "999888777", platform: FactPlatform.Discord, source: FactSource.Discord);

        var bans = (await PollAsync(setup)).Where(e => e.GetProperty("type").GetString() == FactType.DiscordMemberBanned).ToList();

        var ban = Assert.Single(bans);
        Assert.Equal("111222333", ban.GetProperty("subject").GetProperty("id").GetString());
        Assert.Equal("Discord", ban.GetProperty("origin").GetString());
        Assert.Contains("banned", ban.GetProperty("sentence").GetString());
    }

    [Fact]
    public async Task AnOwnerWhoMayNotReadTheAuditLog_IsSentPresenceOnly()
    {
        var setup = await ReadyAsync(ModbotPermissions.None);
        await using var keep = setup.Host;

        var seen = await SomebodyArrivesAsync(setup);
        await WriteAsync(setup.Host, FactType.GroupInstanceKick, Subject(), instance: Instance, world: World, actor: Subject(), source: FactSource.AuditLog);
        await WriteAsync(setup.Host, FactType.MemberJoined, seen, source: FactSource.AuditLog);

        var events = await PollAsync(setup);

        Assert.Equal([FactType.InstanceJoined], Types(events));
    }

    [Fact]
    public async Task ADeviceThatDidNotAsk_IsSentPresenceOnly_WhateverItsOwnerHolds()
    {
        var setup = await ReadyAsync(ModbotPermissions.ViewAuditLog);
        await using var keep = setup.Host;

        await SomebodyArrivesAsync(setup);
        await WriteAsync(setup.Host, FactType.GroupInstanceKick, Subject(), instance: Instance, world: World, actor: Subject(), source: FactSource.AuditLog);

        var events = await PollAsync(setup, moderation: false);

        Assert.Equal([FactType.InstanceJoined], Types(events));
    }

    [Fact]
    public async Task AKickInAnotherInstance_OfSomebodyNeverSeenHere_IsNotSent()
    {
        var setup = await ReadyAsync(ModbotPermissions.ViewAuditLog);
        await using var keep = setup.Host;

        await WriteAsync(setup.Host, FactType.GroupInstanceKick, Subject(), instance: OtherInstance, world: World, actor: Subject(), source: FactSource.AuditLog);

        Assert.Empty(await PollAsync(setup));
    }

    [Fact]
    public async Task ForAPersonSeenHere_TheirOtherInstancesAvatarsVoiceAndNicknames_AreLeftOut()
    {
        var setup = await ReadyAsync(ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewLiveInstances);
        await using var keep = setup.Host;

        var seen = await SomebodyArrivesAsync(setup);

        await WriteAsync(setup.Host, FactType.InstanceJoined, seen, instance: OtherInstance, world: World);
        await WriteAsync(setup.Host, FactType.AvatarChanged, seen, instance: OtherInstance, world: World);
        await WriteAsync(setup.Host, FactType.DiscordVoiceJoined, seen, source: FactSource.Discord);
        await WriteAsync(setup.Host, FactType.DiscordMemberNicknameChanged, seen, source: FactSource.Discord);

        var types = Types(await PollAsync(setup)).ToList();

        Assert.Equal([FactType.InstanceJoined], types);
    }

    [Fact]
    public async Task ABanOfAPersonSeenHere_ByModbotItself_IsSent_WithItsSourceModbot()
    {
        var setup = await ReadyAsync(ModbotPermissions.ViewAuditLog);
        await using var keep = setup.Host;

        var seen = await SomebodyArrivesAsync(setup);
        await WriteAsync(setup.Host, FactType.ActionBan, seen, source: FactSource.Modbot);

        var ban = Assert.Single(await PollAsync(setup), e => e.GetProperty("type").GetString() == FactType.ActionBan);

        Assert.Equal("Modbot", ban.GetProperty("origin").GetString());
    }

    /// <summary>Takes the owner's roles away and gives back only the pairing permission.</summary>
    private static async Task OwnerLosesTheAuditLogAsync(Setup setup)
    {
        await using var context = setup.Host.Database;
        context.UserRoles.RemoveRange(context.UserRoles.Where(r => r.UserId == setup.OwnerId));
        context.UserRoles.Add(new ModbotUserRole
        {
            UserId = setup.OwnerId,
            RoleId = await TestAccounts.RoleForAsync(context, ModbotPermissions.PairCompanion, Ct),
        });
        await context.SaveChangesAsync(Ct);
    }

    private static async Task<(CompanionDevice Device, ModbotPermissions Permissions)?> StandingAsync(Setup setup)
    {
        using var scope = setup.Host.Services.CreateScope();
        return await DeviceStanding.ResolveWithOwnerAsync(
            setup.DeviceHash,
            scope.ServiceProvider.GetRequiredService<ICompanionDeviceStore>(),
            scope.ServiceProvider.GetRequiredService<UserAccountService>(),
            setup.Host.Clock.UtcNow,
            Ct);
    }

    [Fact]
    public async Task AKickInTheSameInstanceNumberInAnotherWorld_IsNotSent()
    {
        var setup = await ReadyAsync(ModbotPermissions.ViewAuditLog);
        await using var keep = setup.Host;

        await WriteAsync(setup.Host, FactType.GroupInstanceKick, Subject(), instance: Instance, world: "wrld_other", actor: Subject(), source: FactSource.AuditLog);

        Assert.Empty(await PollAsync(setup));
    }

    [Fact]
    public async Task ADisabledOwner_IsNoLongerResolved_SoTheRefreshEndsTheFeed()
    {
        var setup = await ReadyAsync(ModbotPermissions.ViewAuditLog);
        await using var keep = setup.Host;

        var before = await StandingAsync(setup);
        Assert.True(before!.Value.Permissions.HasFlag(ModbotPermissions.ViewAuditLog));

        await using (var context = setup.Host.Database)
        {
            context.Users.Single(u => u.Id == setup.OwnerId).IsDisabled = true;
            await context.SaveChangesAsync(Ct);
        }

        Assert.Null(await StandingAsync(setup));

        var response = await setup.Host.Client.SendAsync(
            setup.Host.WithToken(
                HttpMethod.Get, $"/api/v1/companion/poll?instanceId={Instance}&worldId={World}&moderation=true&wait=0", setup.Token),
            Ct);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnOwnerWhoLosesTheAuditLog_IsResolvedWithoutIt_AndTheScopeStopsCarryingModeration()
    {
        var setup = await ReadyAsync(ModbotPermissions.ViewAuditLog);
        await using var keep = setup.Host;

        await OwnerLosesTheAuditLogAsync(setup);

        var standing = await StandingAsync(setup);
        Assert.False(standing!.Value.Permissions.HasFlag(ModbotPermissions.ViewAuditLog));

        var scope = LiveScope.ForDevice(
            setup.DeviceId, Instance, World, ModbotPermissions.ViewAuditLog, setup.OwnerId, moderation: true);
        Assert.False(scope.WithOwnerPermissions(standing.Value.Permissions).CarriesModeration);
    }

    [Fact]
    public async Task APermissionRemovedWhileAPollIsOpen_StopsTheModerationEvents_AtTheNextWait()
    {
        var setup = await ReadyAsync(ModbotPermissions.ViewAuditLog);
        await using var keep = setup.Host;

        var open = PollAsync(setup, wait: 2);

        // Past the poll's first read, which found nothing and is now waiting.
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        await OwnerLosesTheAuditLogAsync(setup);
        await WriteAsync(setup.Host, FactType.GroupInstanceKick, Subject(), instance: Instance, world: World, actor: Subject(), source: FactSource.AuditLog);

        Assert.DoesNotContain(FactType.GroupInstanceKick, Types(await open));
    }

    [Fact]
    public async Task AMemberReportAboutAStaffAccount_IsNeverSentToADevice()
    {
        var setup = await ReadyAsync(ModbotPermissions.ViewAuditLog);
        await using var keep = setup.Host;

        var staffDiscordId = "555666777";
        await using (var context = setup.Host.Database)
        {
            var staff = context.Users.Single(u => u.Id == setup.OwnerId);
            staff.DiscordUserId = staffDiscordId;
            staff.DiscordVerifiedAt = setup.Host.Clock.UtcNow;
            await context.SaveChangesAsync(Ct);
        }

        // Placed in the device's own instance, so only the report rules and the log's categories
        // stand between it and the feed.
        await WriteAsync(
            setup.Host, FactType.MemberReportOpened, staffDiscordId, platform: FactPlatform.Discord,
            instance: Instance, world: World, source: FactSource.Modbot);

        Assert.DoesNotContain(FactType.MemberReportOpened, Types(await PollAsync(setup)));

        // Not a quirk of this owner: the log files a report under operational facts, which no device is sent.
        var everything = LiveScope.ForDevice(
            setup.DeviceId, Instance, World, ModbotPermissions.Administrator, setup.OwnerId, moderation: true);
        Assert.False(everything.CanSee(LiveKinds.Fact, FactType.MemberReportOpened));
    }

    [Fact]
    public void ADevice_IsSentModeration_OnlyWhileItsOwnerMayReadTheAuditLog()
    {
        var asked = LiveScope.ForDevice(Guid.NewGuid(), Instance, World, ModbotPermissions.ViewAuditLog, Guid.NewGuid(), moderation: true);

        Assert.True(asked.CarriesModeration);
        Assert.True(asked.CanSee(LiveKinds.Fact, FactType.MemberBanned));

        // The permission goes at the next refresh: the same device is back to presence only.
        var removed = asked.WithOwnerPermissions(ModbotPermissions.None);

        Assert.False(removed.CarriesModeration);
        Assert.False(removed.CanSee(LiveKinds.Fact, FactType.MemberBanned));
        Assert.True(removed.CanSee(LiveKinds.PersonJoined, FactType.InstanceJoined));
        Assert.True(removed.InInstance("2222", World).AsksForModeration);
    }

    [Fact]
    public void ADeviceThatDidNotAsk_CarriesNoModeration_EvenWithThePermission()
    {
        var scope = LiveScope.ForDevice(Guid.NewGuid(), Instance, World, ModbotPermissions.Administrator, Guid.NewGuid());

        Assert.False(scope.CarriesModeration);
        Assert.False(scope.CanSee(LiveKinds.Fact, FactType.MemberBanned));
    }

    [Fact]
    public void EvidenceFacts_StillNeedTheEvidencePermission_AndOperationalOnesNeverReachADevice()
    {
        var log = LiveScope.ForDevice(Guid.NewGuid(), Instance, World, ModbotPermissions.ViewAuditLog, Guid.NewGuid(), moderation: true);
        var both = log.WithOwnerPermissions(ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewEvidence);

        Assert.False(log.CanSee(LiveKinds.Fact, FactType.EvidenceAttached));
        Assert.True(both.CanSee(LiveKinds.Fact, FactType.EvidenceAttached));
        Assert.False(both.CanSee(LiveKinds.Fact, FactType.Login));
        Assert.False(both.CanSee(LiveKinds.Fact, FactType.AvatarChanged));
    }

    [Fact]
    public void ThePeopleTypes_AreOnlyTheOnesTheFeedMeans()
    {
        Assert.Contains(FactType.MemberJoined, InstanceAudit.PeopleTypes);
        Assert.Contains(FactType.DiscordMemberTimedOut, InstanceAudit.PeopleTypes);
        Assert.Contains(FactType.ReportCreated, InstanceAudit.PeopleTypes);
        Assert.DoesNotContain(FactType.AvatarChanged, InstanceAudit.PeopleTypes);
        Assert.DoesNotContain(FactType.InstanceJoined, InstanceAudit.PeopleTypes);
        Assert.DoesNotContain(FactType.DiscordVoiceJoined, InstanceAudit.PeopleTypes);
        Assert.DoesNotContain(FactType.DiscordMemberNicknameChanged, InstanceAudit.PeopleTypes);
        Assert.DoesNotContain(FactType.DiscordMessagesRemoved, InstanceAudit.PeopleTypes);
        Assert.All(InstanceAudit.PeopleTypes, type => Assert.True(AuditSentence.Writes(type), type));
        Assert.All(InstanceAudit.PeopleTypes, type => Assert.Equal(AuditCategory.Moderation, AuditVisibility.CategoryOf(type)));
    }

    [Theory]
    [InlineData(FactType.MemberBanned, "Mira", "Ada", "Mira banned Ada from the group.")]
    [InlineData(FactType.MemberJoined, null, "Ada", "Ada joined the group.")]
    [InlineData(FactType.MemberBanned, null, "Ada", "Ada was banned from the group.")]
    [InlineData(FactType.GroupInstanceKick, "Mira", "Ada", "Mira kicked Ada out of The Black Cat #39047.")]
    [InlineData(FactType.GroupInstanceWarn, "Mira", "Ada", "Mira warned Ada in The Black Cat #39047.")]
    public void TheSentence_ReadsAsTheWebAuditLogDoes(string type, string? actor, string subject, string expected)
    {
        var entry = new AuditEntry(
            1, DateTimeOffset.UnixEpoch, null, DateTimeOffset.UnixEpoch, TimePrecision.Exact, type, null, AuditCategory.Moderation,
            "AuditLog", "VRChat", "usr_1", SubjectKind.Person, subject, actor is null ? null : "VRChat", actor is null ? null : "usr_2", actor,
            World, "The Black Cat", "39047", null, null, null, new JsonObject());

        Assert.Equal(expected, AuditSentence.For(entry));
    }
}
