using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Bot;
using Modbot.Discord.Gateway;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;
using static Modbot.Discord.Tests.Messages.MessageTestData;

namespace Modbot.Discord.Tests.Members;

/// <summary>
/// Members, voice and moderation as facts: live from the gateway, caught up from the audit log and
/// the member list after the bot was away, and never recorded twice.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordEventRecorderTests
{
    private readonly PostgresFixture _db;

    public DiscordEventRecorderTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DiscordMemberSnapshot Member(
        string id,
        string name = "Ada",
        string? nickname = null,
        DateTimeOffset? joinedAt = null,
        string[]? roles = null,
        DateTimeOffset? timedOutUntil = null)
        => new(id, name.ToLowerInvariant(), nickname ?? name, nickname, IsBot: false, joinedAt, roles ?? [], timedOutUntil);

    private static DiscordServerSnapshot Server(bool canViewAuditLog)
        => new(Guild, "The Black Cat", canViewAuditLog, BotCanManageRoles: false, [], []);

    /// <summary>A bot signed in to the server and done with its first catch-up.</summary>
    private static async Task<(DiscordBotService Bot, FakeGateway Gateway)> ReadyBotAsync(
        TestServices services, bool canViewAuditLog = false, Action<FakeGateway>? configure = null)
    {
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next(g =>
        {
            g.Server = Server(canViewAuditLog);
            configure?.Invoke(g);
        });

        var bot = new DiscordBotService(
            services.Provider.GetRequiredService<IServiceScopeFactory>(),
            gateways,
            services.Clock,
            services.Status,
            new DiscordBotOptions(),
            (_, _) => Task.CompletedTask);

        await services.ConfigureAsync(s =>
        {
            s.DiscordBotTokenEncrypted = services.Protector.Protect("token");
            s.DiscordGuildId = Guild;
        }, Ct);

        await bot.TickAsync(Ct);
        await gateway.RaiseReadyAsync();
        await bot.Reading;

        return (bot, gateway);
    }

    private static async Task<List<ModbotEvent>> FactsAsync(TestServices services, string prefix = "discord.")
    {
        await using var db = services.Database.NewContext();
        return await db.Events.AsNoTracking()
            .Where(e => e.Type.StartsWith(prefix))
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
            .ToListAsync(Ct);
    }

    private static string? Data(ModbotEvent fact, string key) => JsonNode.Parse(fact.Data)?[key]?.ToString();

    [Fact]
    public async Task TheFirstMemberList_RecordsHowManyThereWere_NotAJoinForEach()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await ReadyBotAsync(services, configure: g => g.Members = [Member("1", "Ada"), Member("2", "Bo"), Member("3", "Cy")]);

        var facts = await FactsAsync(services);
        var snapshot = Assert.Single(facts);
        Assert.Equal(FactType.DiscordMembersSnapshot, snapshot.Type);
        Assert.Equal("3", Data(snapshot, "count"));

        await using var db = services.Database.NewContext();
        Assert.Equal(3, await db.DiscordMembers.CountAsync(m => m.LeftAt == null, Ct));

        var count = await db.DailyTotals.AsNoTracking().SingleAsync(t => t.Metric == "discord.members.count", Ct);
        Assert.Equal(3, count.Value);
    }

    [Fact]
    public async Task TheMemberList_IsReadOnResumeToo_AndKeepsEverythingDiscordSaysAboutAMember()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var joined = services.Clock.UtcNow.AddDays(-400);
        var boosting = services.Clock.UtcNow.AddDays(-10);

        var (bot, gateway) = await ReadyBotAsync(services, configure: g => g.Members =
        [
            new DiscordMemberSnapshot("1", "ada", "Ada the Brave", "Ada the Brave", false, joined, ["r1", "r2"], null,
                GlobalName: "Ada", AvatarUrl: "https://cdn.discordapp.com/avatars/1/a.png", IsPending: true, BoostingSince: boosting),
            Member("2", "Bo"),
        ]);

        await using (var db = services.Database.NewContext())
        {
            var ada = await db.DiscordMembers.AsNoTracking().SingleAsync(m => m.UserId == "1", Ct);
            Assert.Equal("ada", ada.Username);
            Assert.Equal("Ada", ada.GlobalName);
            Assert.Equal("Ada the Brave", ada.Nickname);
            Assert.Equal("Ada the Brave", ada.DisplayName);
            Assert.Equal("https://cdn.discordapp.com/avatars/1/a.png", ada.AvatarUrl);
            Assert.True(ada.IsPending);
            Assert.Equal(boosting, ada.BoostingSince);
            Assert.Equal(joined, ada.JoinedAt);
            Assert.Equal(["r1", "r2"], System.Text.Json.JsonSerializer.Deserialize<string[]>(ada.Roles)!);
            Assert.Null(ada.LeftAt);
        }

        // Bo left while the session was away; it resumed rather than signing in again.
        await gateway.RaiseDisconnectedAsync("gone");
        services.Clock.Advance(TimeSpan.FromMinutes(10));
        gateway.Members!.RemoveAll(m => m.UserId == "2");
        await gateway.RaiseResumedAsync();
        await bot.Reading;

        await using (var db = services.Database.NewContext())
        {
            var bo = await db.DiscordMembers.AsNoTracking().SingleAsync(m => m.UserId == "2", Ct);
            Assert.Equal(services.Clock.UtcNow, bo.LeftAt);
        }

        Assert.Single(await FactsAsync(services), f => f.Type == FactType.DiscordMemberLeft && f.SubjectId == "2");
    }

    [Fact]
    public async Task WithoutTheServerMembersIntent_NoMemberListIsRead_AndNobodyIsMarkedLeft()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (bot, gateway) = await ReadyBotAsync(services, configure: g => g.Members = [Member("1"), Member("2")]);

        // The next session was refused the intent and connects without it: Discord sends no list.
        gateway.Options = new DiscordGatewayOptions(MemberEvents: false);
        gateway.Members = [];
        await gateway.RaiseReadyAsync();
        await bot.Reading;

        await using var db = services.Database.NewContext();
        Assert.Equal(2, await db.DiscordMembers.CountAsync(m => m.LeftAt == null, Ct));
    }

    [Fact]
    public async Task LiveJoinsLeavesAndChanges_AreFacts()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (_, gateway) = await ReadyBotAsync(services, configure: g => g.Members = [Member("1", "Ada", roles: ["r1"])]);

        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await gateway.RaiseMemberJoinedAsync(Guild, Member("2", "Bo"));

        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await gateway.RaiseMemberUpdatedAsync(Guild, Member("1", "Ada", nickname: "Ada the Brave", roles: ["r2"], timedOutUntil: services.Clock.UtcNow.AddHours(1)));

        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await gateway.RaiseMemberLeftAsync(Guild, "2");

        var facts = (await FactsAsync(services)).Where(f => f.Type != FactType.DiscordMembersSnapshot).ToList();
        Assert.Equal(
            [
                FactType.DiscordMemberJoined,
                FactType.DiscordMemberNicknameChanged,
                FactType.DiscordRoleGranted,
                FactType.DiscordRoleRevoked,
                FactType.DiscordMemberTimedOut,
                FactType.DiscordMemberLeft,
            ],
            facts.Select(f => f.Type));

        Assert.All(facts, f => Assert.Equal(FactPlatform.Discord, f.SubjectPlatform));
        Assert.Equal("Bo", Data(facts[0], "displayName"));
        Assert.Equal("Ada the Brave", Data(facts[1], "new"));
        Assert.Equal("r2", Data(facts[2], "roleId"));
        Assert.Equal("r1", Data(facts[3], "roleId"));
        Assert.Null(facts[4].ActorId);

        await using var db = services.Database.NewContext();
        var left = await db.DiscordMembers.AsNoTracking().SingleAsync(m => m.UserId == "2", Ct);
        Assert.Equal(services.Clock.UtcNow, left.LeftAt);
    }

    [Fact]
    public async Task WithTheAuditLog_ModerationComesFromItAlone_WithTheModeratorNamed()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (_, gateway) = await ReadyBotAsync(services, canViewAuditLog: true, configure: g =>
            g.Members = [Member("1", "Ada", roles: ["r1"]), Member("9", "Mod")]);

        // The gateway says a ban and a role change happened, but not who did them.
        await gateway.RaiseMemberBannedAsync(Guild, "1");
        await gateway.RaiseMemberUpdatedAsync(Guild, Member("1", "Ada", roles: []));
        Assert.DoesNotContain(await FactsAsync(services), f => f.Type is FactType.DiscordMemberBanned or FactType.DiscordRoleRevoked);

        // The audit log does.
        gateway.AuditLog.Add(new DiscordAuditEntry("5001", services.Clock.UtcNow, DiscordAuditKinds.Roles, "9", "1",
            Roles: [new DiscordRoleChange("r1", "Regulars", Added: false)]));
        gateway.AuditLog.Add(new DiscordAuditEntry("5002", services.Clock.UtcNow, DiscordAuditKinds.Ban, "9", "1", Reason: "spam links"));
        await gateway.RaiseAuditLogChangedAsync(Guild);

        var ban = Assert.Single(await FactsAsync(services), f => f.Type == FactType.DiscordMemberBanned);
        Assert.Equal("9", ban.ActorId);
        Assert.Equal(FactPlatform.Discord, ban.ActorPlatform);
        Assert.Equal("spam links", Data(ban, "reason"));
        Assert.Equal("Mod", Data(ban, "actorDisplayName"));
        Assert.Equal("5002", Data(ban, "auditEntryId"));

        var revoked = Assert.Single(await FactsAsync(services), f => f.Type == FactType.DiscordRoleRevoked);
        Assert.Equal("Regulars", Data(revoked, "roleName"));

        await using var db = services.Database.NewContext();
        Assert.Equal("5002", (await db.DiscordServers.AsNoTracking().SingleAsync(Ct)).AuditLogReadThrough);
    }

    [Fact]
    public async Task ReadingTheAuditLogAgain_RecordsNothingTwice()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var at = services.Clock.UtcNow;

        var (bot, gateway) = await ReadyBotAsync(services, canViewAuditLog: true, configure: g =>
        {
            g.AuditLog.Add(new DiscordAuditEntry("100", at.AddDays(-3), DiscordAuditKinds.Kick, "9", "1"));
            g.AuditLog.Add(new DiscordAuditEntry("101", at.AddDays(-2), DiscordAuditKinds.Timeout, "9", "2", Until: at.AddDays(-1)));
            g.AuditLog.Add(new DiscordAuditEntry("102", at.AddDays(-1), DiscordAuditKinds.MessagesDeleted, "9", "2", ChannelId: "500", Count: 3));
        });

        Assert.Equal(3, (await FactsAsync(services)).Count(f => f.Type != FactType.DiscordMembersSnapshot));

        // A new session with the read position lost: every entry comes back, and none is recorded twice.
        await using (var db = services.Database.NewContext())
        {
            await db.DiscordServers.ExecuteUpdateAsync(u => u.SetProperty(s => s.AuditLogReadThrough, (string?)null), Ct);
        }

        await gateway.RaiseReadyAsync();
        await bot.Reading;

        var facts = (await FactsAsync(services)).Where(f => f.Type != FactType.DiscordMembersSnapshot).ToList();
        Assert.Equal([FactType.DiscordMemberKicked, FactType.DiscordMemberTimedOut, FactType.DiscordMessagesRemoved], facts.Select(f => f.Type));
        Assert.Equal("3", Data(facts[2], "count"));
        Assert.Equal(at.AddDays(-3), facts[0].OccurredAt);
    }

    [Fact]
    public async Task ABanTheGatewayRecordedBeforeTheBotCouldReadTheAuditLog_IsNotRecordedAgainFromIt()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (bot, gateway) = await ReadyBotAsync(services, canViewAuditLog: false, configure: g => g.Members = [Member("1")]);

        await gateway.RaiseMemberBannedAsync(Guild, "1");
        Assert.Single(await FactsAsync(services), f => f.Type == FactType.DiscordMemberBanned);

        // View Audit Log is given afterwards, and the catch-up finds the same ban.
        gateway.Server = Server(canViewAuditLog: true);
        gateway.AuditLog.Add(new DiscordAuditEntry("700", services.Clock.UtcNow.AddSeconds(1), DiscordAuditKinds.Ban, "9", "1"));
        await gateway.RaiseReadyAsync();
        await bot.Reading;

        Assert.Single(await FactsAsync(services), f => f.Type == FactType.DiscordMemberBanned);
    }

    [Fact]
    public async Task AfterTheBotWasAway_JoinsAndLeavesAreFound_WithinTheGap()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (bot, gateway) = await ReadyBotAsync(services, configure: g => g.Members = [Member("1", "Ada"), Member("2", "Bo")]);

        // The bot notes it is listening, then goes away for two hours.
        services.Clock.Advance(TimeSpan.FromMinutes(2));
        await bot.TickAsync(Ct);
        var lastListening = services.Clock.UtcNow;
        await gateway.RaiseDisconnectedAsync("gone");

        services.Clock.Advance(TimeSpan.FromHours(2));
        var joinedWhileAway = lastListening.AddMinutes(30);
        gateway.Members = [Member("1", "Ada"), Member("3", "Cy", joinedAt: joinedWhileAway)];

        await gateway.RaiseReadyAsync();
        await bot.Reading;

        var facts = (await FactsAsync(services)).Where(f => f.Type != FactType.DiscordMembersSnapshot).ToList();

        var joined = Assert.Single(facts, f => f.Type == FactType.DiscordMemberJoined);
        Assert.Equal("3", joined.SubjectId);
        Assert.Equal(joinedWhileAway, joined.OccurredAt);
        Assert.Null(joined.OccurredBefore);

        var left = Assert.Single(facts, f => f.Type == FactType.DiscordMemberLeft);
        Assert.Equal("2", left.SubjectId);
        Assert.Equal(lastListening, left.OccurredAt);
        Assert.Equal(services.Clock.UtcNow, left.OccurredBefore);
    }

    [Fact]
    public async Task VoiceSessions_AreJoinsMovesAndLeaves_AndOneLeftOpenIsClosedOverTheGap()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (bot, gateway) = await ReadyBotAsync(services, configure: g => g.Members = [Member("1"), Member("2")]);

        await gateway.RaiseVoiceChangedAsync(Guild, "1", null, "800");
        services.Clock.Advance(TimeSpan.FromMinutes(10));
        await gateway.RaiseVoiceChangedAsync(Guild, "1", "800", "801");
        services.Clock.Advance(TimeSpan.FromMinutes(10));
        await gateway.RaiseVoiceChangedAsync(Guild, "1", "801", null);

        await gateway.RaiseVoiceChangedAsync(Guild, "2", null, "800");
        await bot.TickAsync(Ct);
        var lastListening = services.Clock.UtcNow;

        // Away for an hour; person 2 left meanwhile, and person 1 is back in voice.
        await gateway.RaiseDisconnectedAsync("gone");
        services.Clock.Advance(TimeSpan.FromHours(1));
        gateway.Voice = [new DiscordVoiceState("1", "802")];
        await gateway.RaiseReadyAsync();
        await bot.Reading;

        var voice = await FactsAsync(services, "discord.voice.");
        Assert.Equal(
            [
                (FactType.DiscordVoiceJoined, "1"),
                (FactType.DiscordVoiceMoved, "1"),
                (FactType.DiscordVoiceLeft, "1"),
                (FactType.DiscordVoiceJoined, "2"),
                (FactType.DiscordVoiceLeft, "2"),
                (FactType.DiscordVoiceJoined, "1"),
            ],
            voice.Select(f => (f.Type, f.SubjectId)));

        var closed = voice[4];
        Assert.Equal(lastListening, closed.OccurredAt);
        Assert.Equal(services.Clock.UtcNow, closed.OccurredBefore);
        Assert.Equal("801", Data(voice[2], "channelId"));
        Assert.Equal("800", Data(voice[1], "from"));
    }

    [Fact]
    public async Task AfterTheBotWasAway_MessagesAreCaughtUpForward_PastThreePagesAlreadyStored()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (bot, gateway) = await ReadyBotAsync(services, configure: g =>
        {
            g.Server = Server(canViewAuditLog: false) with
            {
                Channels = [new DiscordChannelSnapshot("500", "general", DiscordChannelTypes.Text, null, 0, false, new(true, true, true, true, true, false))],
            };
            g.History["500"] = History(50);
        });

        // 250 messages were posted while the bot was offline.
        await gateway.RaiseDisconnectedAsync("gone");
        gateway.History["500"] = History(300);
        gateway.Reads.Clear();

        await gateway.RaiseReadyAsync();
        await bot.Reading;

        await using (var db = services.Database.NewContext())
            Assert.Equal(300, await db.DiscordMessages.CountAsync(Ct));

        Assert.Equal("500", gateway.Reads[0].ChannelId);
        Assert.Null(gateway.Reads[0].BeforeId);
        Assert.Equal("50", gateway.Reads[0].AfterId);
        Assert.Equal(3, gateway.Reads.Count(r => r.AfterId is not null));

        // Caught-up messages are recent, so AI moderation checks them; the history read back was not.
        Assert.Equal(250, services.Checker.Checked.Count);
    }
}
