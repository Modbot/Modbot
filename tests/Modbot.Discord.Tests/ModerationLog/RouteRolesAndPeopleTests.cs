using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Discord.ModerationLog;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.ModerationLog;

/// <summary>
/// Role filters decide on the roles held when the event happened, and person filters match an
/// account on its own with a link only adding (Discord event routes design §3), end to end through
/// the fact writer, the database and the poster.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class RouteRolesAndPeopleTests
{
    private const string Channel = "1234567890";
    private const string Group = "grp_7f8e1c4a-0000-4000-8000-000000000001";
    private const string Wren = "usr_wren";
    private const string TeaSpoon = "usr_teaspoon";
    private const string Moderator = "grol_moderator";

    private readonly PostgresFixture _db;

    public RouteRolesAndPeopleTests(PostgresFixture db) => _db = db;

    private static async Task<ModerationLogPass> RunAsync(TestServices services, FakeGateway gateway, CancellationToken ct)
    {
        using var scope = services.Scope();
        var poster = scope.ServiceProvider.GetRequiredService<ModerationLogPoster>();
        return await poster.RunOnceAsync(gateway, (_, _) => Task.CompletedTask, ct);
    }

    /// <summary>A group with Wren as a Moderator and TeaSpoon as a plain member, and a channel for bans by Moderators, started.</summary>
    private static async Task<TestServices> ModeratorBansAsync(PostgresFixture db, FakeGateway gateway, CancellationToken ct)
    {
        var services = await TestServices.CreateAsync(db, ct);
        await services.ConfigureAsync(s => s.ManagedGroupId = Group, ct);
        await SetRolesAsync(services, Wren, [Moderator], ct);
        await SetRolesAsync(services, TeaSpoon, [], ct);

        await services.AddRouteAsync(Channel, [FactType.MemberBanned], r => r.ActorVRChatRoleIds = [Moderator], ct: ct);
        await RunAsync(services, gateway, ct);
        return services;
    }

    [Fact]
    public async Task ARoleTakenAwayBeforeThePost_StillPosts_BecauseTheRolesOfTheMomentWereSaved()
    {
        var ct = TestContext.Current.CancellationToken;
        var gateway = new FakeGateway();
        await using var services = await ModeratorBansAsync(_db, gateway, ct);

        // Wren bans TeaSpoon while a Moderator.
        var ban = await services.WriteAuditFactAsync(FactType.MemberBanned, TeaSpoon, Wren, "Wren", ct: ct);

        // The bot is offline; Wren stops being a Moderator.
        services.Clock.Advance(TimeSpan.FromHours(1));
        await SetRolesAsync(services, Wren, [], ct);
        await RoleChangeAsync(services, FactType.RoleRevoked, Wren, Moderator, services.Clock.UtcNow, ct);

        var saved = HeldRoles.Read(Assert.Single(await services.FactsOfTypeAsync(FactType.MemberBanned, ct)).Data);
        Assert.NotNull(saved);
        Assert.Equal([Moderator], saved.ActorVRChatRoles);

        // Catch-up still posts the ban.
        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(1, pass.Posted);
        Assert.Equal("Banned", Assert.Single(Assert.Single(gateway.Posts).Embeds).Title);
        Assert.True((await services.ChannelPlaceAsync(Channel, ct))!.PostedThrough >= ban);
    }

    [Fact]
    public async Task ARoleGivenBeforeThePost_DoesNotPost_ABanFromBeforeTheyHadIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var gateway = new FakeGateway();
        await using var services = await ModeratorBansAsync(_db, gateway, ct);

        await services.WriteAuditFactAsync(FactType.MemberBanned, Wren, TeaSpoon, "TeaSpoon", ct: ct);

        services.Clock.Advance(TimeSpan.FromHours(1));
        await SetRolesAsync(services, TeaSpoon, [Moderator], ct);
        await RoleChangeAsync(services, FactType.RoleGranted, TeaSpoon, Moderator, services.Clock.UtcNow, ct);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(0, pass.Posted);
        Assert.Empty(gateway.Posts);
    }

    [Fact]
    public async Task AnOldFactWithoutSavedRoles_IsDecidedFromTheRoleChangesRecordedSince()
    {
        var ct = TestContext.Current.CancellationToken;
        var gateway = new FakeGateway();
        await using var services = await ModeratorBansAsync(_db, gateway, ct);

        // Written before roles were saved: a ban by Wren two hours ago, with no roles in it.
        var at = services.Clock.UtcNow.AddHours(-2);
        await OldFactAsync(services, FactType.MemberBanned, TeaSpoon, Wren, at, ct);

        // An hour after it, Wren lost Moderator, and the sweep recorded that.
        await SetRolesAsync(services, Wren, [], ct);
        await RoleChangeAsync(services, FactType.RoleRevoked, Wren, Moderator, at.AddHours(1), ct);

        // And a ban by TeaSpoon, who was given Moderator after it.
        await OldFactAsync(services, FactType.MemberBanned, Wren, TeaSpoon, at, ct);
        await SetRolesAsync(services, TeaSpoon, [Moderator], ct);
        await RoleChangeAsync(services, FactType.RoleGranted, TeaSpoon, Moderator, at.AddMinutes(30), ct);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(1, pass.Posted);
        var embed = Assert.Single(Assert.Single(gateway.Posts).Embeds);
        Assert.Contains(TeaSpoon, embed.Fields.Single(f => f.Name == "Who").Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOldFactWithNoRoleHistoryAtAll_FallsBackToTheRolesHeldNow()
    {
        var ct = TestContext.Current.CancellationToken;
        var gateway = new FakeGateway();
        await using var services = await ModeratorBansAsync(_db, gateway, ct);

        var at = services.Clock.UtcNow.AddHours(-2);
        await OldFactAsync(services, FactType.MemberBanned, TeaSpoon, Wren, at, ct);
        await OldFactAsync(services, FactType.MemberBanned, Wren, TeaSpoon, at, ct);

        var pass = await RunAsync(services, gateway, ct);

        // Wren is a Moderator now and nothing says otherwise; TeaSpoon is not.
        Assert.Equal(1, pass.Posted);
        var embed = Assert.Single(Assert.Single(gateway.Posts).Embeds);
        Assert.Contains(TeaSpoon, embed.Fields.Single(f => f.Name == "Who").Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOldFactByAModbotAccount_UsesTheModbotRolesBeforeTheNextRolesChange()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        ModbotUser sam;
        Guid role;
        await using (var db = services.Database.NewContext())
        {
            sam = await TestAccounts.CreateAsync(db, "sam", TestAccounts.Password, ModbotPermissions.None, linked: true, ct);
            role = await TestAccounts.RoleForAsync(db, ModbotPermissions.ViewAuditLog, ct);
        }

        await services.AddRouteAsync(Channel, [FactType.ReportCreated], r => r.ActorModbotRoleIds = [role], ct: ct);
        await RunAsync(services, gateway, ct);

        // Sam wrote a case file while holding the role; it was taken away an hour later.
        var at = services.Clock.UtcNow.AddHours(-2);
        await OldFactAsync(services, FactType.ReportCreated, TeaSpoon, sam.Id.ToString(), at, ct, FactPlatform.Modbot);
        await services.WriteFactAsync(new FactRecord
        {
            Type = FactType.UserRolesChanged,
            OccurredAt = at.AddHours(1),
            SubjectPlatform = FactPlatform.Modbot,
            SubjectId = sam.Id.ToString(),
            Source = FactSource.Modbot,
            Data = new JsonObject
            {
                ["before"] = "Reader",
                ["after"] = string.Empty,
                ["beforeRoleIds"] = new JsonArray(role.ToString()),
                ["afterRoleIds"] = new JsonArray(),
            },
        }, ct);

        var pass = await RunAsync(services, gateway, ct);
        Assert.Equal(1, pass.Posted);
    }

    [Fact]
    public async Task TheFactWriter_SavesTheRolesOfBothPeople_AndLeavesClientReportsAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.ConfigureAsync(s => s.ManagedGroupId = Group, ct);
        await SetRolesAsync(services, Wren, [Moderator, "grol_member"], ct);
        await SetRolesAsync(services, TeaSpoon, ["grol_member"], ct);

        await services.WriteAuditFactAsync(FactType.GroupInstanceWarn, TeaSpoon, Wren, ct: ct);
        await services.WriteFactAsync(new FactRecord
        {
            Type = FactType.InstanceJoined,
            OccurredAt = services.Clock.UtcNow,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = TeaSpoon,
            WorldId = "wrld_1",
            InstanceId = "1",
            Source = FactSource.Client,
        }, ct);

        var warn = HeldRoles.Read(Assert.Single(await services.FactsOfTypeAsync(FactType.GroupInstanceWarn, ct)).Data)!;
        Assert.Equal(["grol_member"], warn.SubjectVRChatRoles);
        Assert.Equal(["grol_member", Moderator], warn.ActorVRChatRoles);
        Assert.Empty(warn.ActorModbotRoles);

        Assert.Null(HeldRoles.Read(Assert.Single(await services.FactsOfTypeAsync(FactType.InstanceJoined, ct)).Data));
    }

    [Fact]
    public async Task PersonFilters_MatchVRChatOnly_DiscordOnly_AndLinkedPeople()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        const string DiscordOnly = "900000000000000777";
        const string JessieVRChat = "usr_jessie";
        const string JessieDiscord = "900000000000000555";

        await using (var db = services.Database.NewContext())
        {
            db.DiscordAccountLinks.Add(new DiscordAccountLink
            {
                DiscordUserId = JessieDiscord,
                DiscordUsername = "jessie",
                VRChatUserId = JessieVRChat,
                LinkedAt = services.Clock.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        string[] types = [FactType.MemberKicked, FactType.DiscordMemberJoined];
        await services.AddRouteAsync(Channel, types, r => r.SubjectIds = [TeaSpoon], ct: ct);
        await services.AddRouteAsync(Channel, types, r => r.SubjectDiscordIds = [DiscordOnly], ct: ct);
        await services.AddRouteAsync(Channel, types, r => r.SubjectIds = [JessieVRChat], ct: ct);
        await RunAsync(services, gateway, ct);

        await services.WriteAuditFactAsync(FactType.MemberKicked, TeaSpoon, ct: ct);
        await DiscordJoinAsync(services, DiscordOnly, ct);
        await services.WriteAuditFactAsync(FactType.MemberKicked, JessieVRChat, ct: ct);
        await DiscordJoinAsync(services, JessieDiscord, ct);

        // Nobody picked: not sent.
        await services.WriteAuditFactAsync(FactType.MemberKicked, "usr_stranger", ct: ct);
        await DiscordJoinAsync(services, "900000000000000999", ct);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(4, pass.Posted);
        var who = Assert.Single(gateway.Posts).Embeds.Select(e => e.Fields.Single(f => f.Name == "Who").Value).ToList();
        Assert.Contains(who, w => w.Contains(TeaSpoon, StringComparison.Ordinal));
        Assert.Contains(who, w => w.Contains(DiscordOnly, StringComparison.Ordinal));
        Assert.Contains(who, w => w.Contains(JessieVRChat, StringComparison.Ordinal));
        Assert.Contains(who, w => w.Contains(JessieDiscord, StringComparison.Ordinal));
    }

    private static Task<long> DiscordJoinAsync(TestServices services, string discordId, CancellationToken ct)
        => services.WriteFactAsync(new FactRecord
        {
            Type = FactType.DiscordMemberJoined,
            OccurredAt = services.Clock.UtcNow,
            SubjectPlatform = FactPlatform.Discord,
            SubjectId = discordId,
            Source = FactSource.Discord,
        }, ct);

    private static async Task SetRolesAsync(TestServices services, string userId, string[] roles, CancellationToken ct)
    {
        await using var db = services.Database.NewContext();
        var row = db.GroupMembers.FirstOrDefault(m => m.GroupId == Group && m.UserId == userId);

        if (row is null)
        {
            row = new GroupMember
            {
                GroupId = Group,
                UserId = userId,
                FirstSeenAt = services.Clock.UtcNow,
                LastSeenAt = services.Clock.UtcNow,
            };
            db.GroupMembers.Add(row);
        }

        row.Roles = JsonSerializer.Serialize(roles);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A role change as the member sweep records one.</summary>
    private static Task<long> RoleChangeAsync(
        TestServices services, string type, string userId, string roleId, DateTimeOffset at, CancellationToken ct)
        => services.WriteFactAsync(new FactRecord
        {
            Type = type,
            OccurredAt = at,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = userId,
            Source = FactSource.SyncDiff,
            Data = new JsonObject { ["roleId"] = roleId, ["groupId"] = Group },
        }, ct);

    /// <summary>A fact as written before roles were saved: straight into the log, payload without them.</summary>
    private static async Task<long> OldFactAsync(
        TestServices services,
        string type,
        string subjectId,
        string actorId,
        DateTimeOffset at,
        CancellationToken ct,
        FactPlatform actorPlatform = FactPlatform.VRChat)
    {
        await using var db = services.Database.NewContext();
        var fact = new ModbotEvent
        {
            Type = type,
            OccurredAt = at,
            ObservedAt = at,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = subjectId,
            ActorPlatform = actorPlatform,
            ActorId = actorId,
            Source = FactSource.AuditLog,
            Data = "{}",
        };
        db.Events.Add(fact);
        await db.SaveChangesAsync(ct);
        return fact.Id;
    }
}
