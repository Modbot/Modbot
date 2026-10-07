using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.DiscordMembers;
using Modbot.Api.Tests.Fakes;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.TestSupport;
using Modbot.VRChat;

using VRChatGroupMember = VRChat.API.Model.GroupMember;
using VRChatSuccess = VRChat.API.Model.Success;

namespace Modbot.Api.Tests.Features.DiscordMembers;

/// <summary>
/// What the bot's <c>/ban</c> and <c>/kick</c> stand on (Discord commands design §3.3, step 3): the
/// API's own services, reached through <see cref="IStaffActions"/>. A VRChat ban brings the linked
/// Discord ban with it; a Discord ban or removal is the <c>/api/discord</c> endpoints' service with
/// the same refusals; and a 429 from VRChat is reported, never retried.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class BanAndKickFromDiscordTests
{
    private const string Group = "grp_1";
    private const string Guild = "100000000000000001";
    private const string Person = "usr_troublemaker";
    private const string ModbotAccount = "usr_modbot";
    private const string Target = "200000000000000002";
    private const string Bot = "300000000000000003";
    private const string Owner = "400000000000000004";

    private static readonly DateTimeOffset Day = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _db;

    public BanAndKickFromDiscordTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Records what it was asked and answers as told.</summary>
    private sealed class FakeDiscord : IDiscordMemberActions
    {
        public List<(string Action, string UserId, string Reason, int Days)> Asked { get; } = [];

        public DiscordMemberOutcome Answer { get; set; } = DiscordMemberOutcome.Ok;

        public DiscordOffLimits OffLimits { get; set; } = DiscordOffLimits.None;

        public Task<DiscordOffLimits> OffLimitsAsync(string guildId, CancellationToken ct = default)
            => Task.FromResult(OffLimits);

        public Task<DiscordMemberOutcome> BanAsync(string guildId, string userId, string reason, int deleteMessageDays, CancellationToken ct = default)
        {
            Asked.Add(("ban", userId, reason, deleteMessageDays));
            return Task.FromResult(Answer);
        }

        public Task<DiscordMemberOutcome> UnbanAsync(string guildId, string userId, string reason, CancellationToken ct = default)
        {
            Asked.Add(("unban", userId, reason, 0));
            return Task.FromResult(Answer);
        }

        public Task<DiscordMemberOutcome> KickAsync(string guildId, string userId, string reason, CancellationToken ct = default)
        {
            Asked.Add(("kick", userId, reason, 0));
            return Task.FromResult(Answer);
        }

        public Task<DiscordMemberOutcome> TimeOutAsync(string guildId, string userId, TimeSpan duration, string reason, CancellationToken ct = default)
        {
            Asked.Add(("timeout", userId, reason, 0));
            return Task.FromResult(Answer);
        }
    }

    private sealed class RecordingLinkedDiscord : ILinkedDiscordBans
    {
        public List<(string VRChatUserId, string? Why)> Banned { get; } = [];

        public Task<LinkedDiscordOutcome> BanAsync(
            string vrchatUserId, string by, string? why, long? causedByFactId, CancellationToken ct = default)
        {
            Banned.Add((vrchatUserId, why));
            return Task.FromResult(new LinkedDiscordOutcome(LinkedDiscordStatus.Done));
        }

        public Task<LinkedDiscordOutcome> UnbanAsync(
            string vrchatUserId, string by, string? why, long? causedByFactId, CancellationToken ct = default)
            => Task.FromResult(new LinkedDiscordOutcome(LinkedDiscordStatus.Done));
    }

    private static FakeVRChatGate Accepting()
        => new FakeVRChatGate()
            .SignedInAs()
            .Returns("KickGroupMember", new VRChatSuccess())
            .Returns("BanGroupMember", new VRChatGroupMember());

    private async Task<(ReadSurfaceTestHost Host, StaffMember By)> StartAsync(
        FakeVRChatGate gate,
        FakeDiscord discord,
        ModbotPermissions held,
        RecordingLinkedDiscord? linked = null,
        string? guild = Guild)
    {
        var host = await ReadSurfaceTestHost.StartAsync(
            _db,
            gate,
            configure: services =>
            {
                services.AddSingleton<IDiscordMemberActions>(discord);

                if (linked is not null)
                    services.AddScoped<ILinkedDiscordBans>(_ => linked);
            });

        await host.ResetAsync(Ct);
        host.Clock.UtcNow = Day;

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var settings = await db.GetSettingsAsync(Ct);
            settings.ManagedGroupId = Group;
            settings.VRChatSessionUserId = ModbotAccount;
            settings.DiscordGuildId = guild;
            await db.SaveChangesAsync(Ct);
        }

        var user = await host.CreateUserAsync($"u_{Guid.NewGuid():N}", "hunter2", held, Ct);
        return (host, new StaffMember(user.Id, user.Username, held));
    }

    private static async Task<T> InScopeAsync<T>(ReadSurfaceTestHost host, Func<IStaffActions, Task<T>> work)
    {
        using var scope = host.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<IStaffActions>());
    }

    private static async Task<List<ModbotEvent>> FactsAsync(ReadSurfaceTestHost host, string type)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        return await db.Events.AsNoTracking().Where(e => e.Type == type).ToListAsync(Ct);
    }

    // ── A VRChat ban brings the linked Discord ban ───────────────────────────────────────────

    [Fact]
    public async Task ABanOfALinkedPerson_IsAGroupBan_AndTheLinkedDiscordAccountIsBannedWithIt()
    {
        var linked = new RecordingLinkedDiscord();
        var (host, by) = await StartAsync(Accepting(), new FakeDiscord(), ModbotPermissions.Ban, linked);
        await using var _ = host;

        var reasons = await InScopeAsync(host, s => s.ReasonsAsync("ban", Ct));
        var harassment = reasons.Reasons.First(r => r.Label == "Harassment").Id;

        var answer = await InScopeAsync(host, s => s.RunAsync("ban", "discord:link-1", Person, [harassment], "Slurs.", by, Ct));

        Assert.True(answer.Done);
        Assert.True(answer.DiscordDone);
        Assert.Equal(Person, Assert.Single(linked.Banned).VRChatUserId);
        Assert.Single(await FactsAsync(host, FactType.ActionBan));

        // The service claimed the key: the same confirmation again is the first answer, one ban.
        var again = await InScopeAsync(host, s => s.RunAsync("ban", "discord:link-1", Person, [harassment], "Slurs.", by, Ct));
        Assert.True(again.Repeat);
        Assert.Single(linked.Banned);
    }

    // ── A Discord-only ban or removal ────────────────────────────────────────────────────────

    [Fact]
    public async Task ADiscordBan_GoesToDiscordWithWhoAskedAndTheDays_AndIsRecorded()
    {
        var discord = new FakeDiscord();
        var (host, by) = await StartAsync(Accepting(), discord, ModbotPermissions.DiscordBan);
        await using var _ = host;

        var answer = await InScopeAsync(host, s => s.DiscordBanAsync(Target, "Spam links", 7, by, Ct));

        Assert.True(answer.Done);
        Assert.False(answer.Unchanged);

        var (action, userId, reason, days) = Assert.Single(discord.Asked);
        Assert.Equal("ban", action);
        Assert.Equal(Target, userId);
        Assert.Equal($"Modbot: banned by {by.Username}: Spam links", reason);
        Assert.Equal(7, days);

        var fact = Assert.Single(await FactsAsync(host, FactType.ActionDiscordBan));
        Assert.Equal(FactPlatform.Discord, fact.SubjectPlatform);
        Assert.Equal(Target, fact.SubjectId);
        Assert.Equal(by.UserId.ToString(), fact.ActorId);
        Assert.Equal(Guild, JsonDocument.Parse(fact.Data).RootElement.GetProperty("guildId").GetString());
    }

    [Fact]
    public async Task ADiscordRemoval_IsTheEndpointsKick_AndAlreadyGoneIsNotRecorded()
    {
        var discord = new FakeDiscord();
        var (host, by) = await StartAsync(Accepting(), discord, ModbotPermissions.DiscordKick);
        await using var _ = host;

        Assert.True((await InScopeAsync(host, s => s.DiscordKickAsync(Target, "Raid", by, Ct))).Done);
        Assert.Equal("kick", Assert.Single(discord.Asked).Action);
        Assert.Single(await FactsAsync(host, FactType.ActionDiscordKick));

        discord.Answer = DiscordMemberOutcome.Already;
        var already = await InScopeAsync(host, s => s.DiscordKickAsync(Target, "Raid", by, Ct));

        Assert.True(already.Done);
        Assert.True(already.Unchanged);
        Assert.Single(await FactsAsync(host, FactType.ActionDiscordKick));
    }

    [Fact]
    public async Task WhenDiscordRefuses_ItIsAFailureWithDiscordsSentence_NotAModbotRefusal()
    {
        var discord = new FakeDiscord { Answer = DiscordMemberOutcome.Failed("The bot may not remove that person.") };
        var (host, by) = await StartAsync(Accepting(), discord, ModbotPermissions.DiscordKick);
        await using var _ = host;

        var answer = await InScopeAsync(host, s => s.DiscordKickAsync(Target, "Raid", by, Ct));

        Assert.False(answer.Done);
        Assert.False(answer.Refused);
        Assert.Equal("The bot may not remove that person.", answer.Error);
        Assert.Empty(await FactsAsync(host, FactType.ActionDiscordKick));

        discord.Answer = DiscordMemberOutcome.Offline;
        var offline = await InScopeAsync(host, s => s.DiscordKickAsync(Target, "Raid", by, Ct));
        Assert.False(offline.Done);
        Assert.Equal("The Discord bot is not connected.", offline.Error);
    }

    // ── The three accounts never acted on ────────────────────────────────────────────────────

    [Theory]
    [InlineData("bot")]
    [InlineData("owner")]
    [InlineData("staff")]
    public async Task TheBot_TheOwner_AndAStaffAccount_AreRefusedBeforeAnythingIsAsked(string who)
    {
        const string Colleague = "500000000000000005";

        var discord = new FakeDiscord { OffLimits = new DiscordOffLimits(Bot, Owner) };
        var (host, by) = await StartAsync(Accepting(), discord, ModbotPermissions.DiscordBan | ModbotPermissions.DiscordKick);
        await using var _ = host;

        var colleague = await host.CreateUserAsync($"c_{Guid.NewGuid():N}", "hunter2", ModbotPermissions.None, Ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var row = await db.Users.SingleAsync(u => u.Id == colleague.Id, Ct);
            row.DiscordUserId = Colleague;
            await db.SaveChangesAsync(Ct);
        }

        // A leading zero is the same number to Discord, so it is the same account here.
        var (id, sentence) = who switch
        {
            "bot" => ("0" + Bot, DiscordMemberActionService.BotMessage),
            "owner" => ("0" + Owner, DiscordMemberActionService.OwnerMessage),
            _ => (Colleague, DiscordMemberActionService.StaffMessage),
        };

        // The check the bot makes before it shows a confirmation...
        Assert.Equal(sentence, await InScopeAsync(host, s => s.DiscordCheckAsync("ban", id, by, Ct)));
        Assert.Equal(sentence, await InScopeAsync(host, s => s.DiscordCheckAsync("kick", id, by, Ct)));

        // ...and the action itself, which makes it again: a stale confirmation cannot get past it.
        var ban = await InScopeAsync(host, s => s.DiscordBanAsync(id, "x", 0, by, Ct));
        var kick = await InScopeAsync(host, s => s.DiscordKickAsync(id, "x", by, Ct));

        Assert.True(ban.Refused);
        Assert.Equal(sentence, ban.Error);
        Assert.True(kick.Refused);
        Assert.Equal(sentence, kick.Error);

        Assert.Empty(discord.Asked);
        Assert.Empty(await FactsAsync(host, FactType.ActionDiscordBan));
        Assert.Empty(await FactsAsync(host, FactType.ActionDiscordKick));

        // Somebody else is fine.
        Assert.Null(await InScopeAsync(host, s => s.DiscordCheckAsync("ban", Target, by, Ct)));
    }

    // ── Permissions: the Discord ones, apart from the group's ────────────────────────────────

    [Fact]
    public async Task TheGroupsBanAndKick_AreNotPermissionToActOnDiscord_AndTheOtherWayRound()
    {
        var discord = new FakeDiscord();
        var (host, group) = await StartAsync(Accepting(), discord, ModbotPermissions.Ban | ModbotPermissions.Kick);
        await using var _ = host;

        Assert.True((await InScopeAsync(host, s => s.DiscordBanAsync(Target, "x", 0, group, Ct))).Refused);
        Assert.True((await InScopeAsync(host, s => s.DiscordKickAsync(Target, "x", group, Ct))).Refused);
        Assert.Equal("You do not have permission to do that.", await InScopeAsync(host, s => s.DiscordCheckAsync("ban", Target, group, Ct)));
        Assert.Empty(discord.Asked);

        // And Ban on Discord is not Ban: the group's action refuses it.
        var onDiscord = new StaffMember(group.UserId, group.Username, ModbotPermissions.DiscordBan | ModbotPermissions.DiscordKick);
        var vrchat = await InScopeAsync(host, s => s.RunAsync("ban", "discord:perm", Person, [], string.Empty, onDiscord, Ct));
        Assert.True(vrchat.Refused);
    }

    [Fact]
    public async Task ADiscordBanForMoreThanSevenDays_IsRefused_AsTheEndpointDoes()
    {
        var discord = new FakeDiscord();
        var (host, by) = await StartAsync(Accepting(), discord, ModbotPermissions.DiscordBan);
        await using var _ = host;

        foreach (var days in new[] { -1, 8 })
        {
            var answer = await InScopeAsync(host, s => s.DiscordBanAsync(Target, "x", days, by, Ct));
            Assert.True(answer.Refused);
        }

        Assert.Empty(discord.Asked);
    }

    [Fact]
    public async Task WithNoDiscordServerSetUp_NothingIsAsked()
    {
        var discord = new FakeDiscord();
        var (host, by) = await StartAsync(Accepting(), discord, ModbotPermissions.DiscordBan, guild: null);
        await using var _ = host;

        Assert.Equal("No Discord server is set up yet.", await InScopeAsync(host, s => s.DiscordCheckAsync("ban", Target, by, Ct)));
        Assert.True((await InScopeAsync(host, s => s.DiscordBanAsync(Target, "x", 0, by, Ct))).Refused);
        Assert.Empty(discord.Asked);
    }

    // ── A 429 on groups.moderate ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A429OnTheGroupBan_IsReported_NotRetried_AndTheLinkedDiscordAccountIsLeftAlone()
    {
        var gate = new FakeVRChatGate()
            .SignedInAs()
            .Returns("BanGroupMember", VRChatResult<VRChatGroupMember>.Failure(
                429, "VRChat rate limited this request.", kind: VRChatFailureKind.RateLimited));

        var linked = new RecordingLinkedDiscord();
        var (host, by) = await StartAsync(gate, new FakeDiscord(), ModbotPermissions.Ban, linked);
        await using var _ = host;

        var reasons = await InScopeAsync(host, s => s.ReasonsAsync("ban", Ct));
        var reason = reasons.Reasons.First().Id;

        var answer = await InScopeAsync(host, s => s.RunAsync("ban", "discord:429", Person, [reason], "x", by, Ct));

        Assert.False(answer.Done);
        Assert.False(string.IsNullOrWhiteSpace(answer.Error));

        // One request, once: a 429 is a cold stop (spec 4.3.1), and the same key again does not ask twice.
        Assert.Single(gate.Calls, c => c.Endpoint.Class == VRChatEndpointClass.GroupsModerate);
        await InScopeAsync(host, s => s.RunAsync("ban", "discord:429", Person, [reason], "x", by, Ct));
        Assert.Single(gate.Calls, c => c.Endpoint.Class == VRChatEndpointClass.GroupsModerate);

        // Nothing was banned in VRChat, so nothing was banned beside it.
        Assert.Empty(linked.Banned);
        Assert.Empty(await FactsAsync(host, FactType.ActionBan));
    }

    // ── The service the endpoints now call ───────────────────────────────────────────────────

    [Fact]
    public async Task TheService_ChecksInTheEndpointsOrder_AndSaysTheEndpointsWords()
    {
        var discord = new FakeDiscord { OffLimits = new DiscordOffLimits(Bot, Owner) };
        var (host, by) = await StartAsync(Accepting(), discord, ModbotPermissions.None);
        await using var _ = host;

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var facts = scope.ServiceProvider.GetRequiredService<Modbot.Analytics.Facts.IFactWriter>();
        var partitions = scope.ServiceProvider.GetRequiredService<Modbot.Analytics.Facts.EventPartitionMaintainer>();
        var clock = scope.ServiceProvider.GetRequiredService<Modbot.Core.Time.IModbotClock>();

        // No fact log: nothing could be recorded, so nothing is asked (503, not-set-up).
        var cannotRecord = await new DiscordMemberActionService(db, clock, discord, null, null)
            .ActAsync(DiscordMemberVerb.Kick, by.UserId, by.Username, Target, null, 0, null, Ct);
        Assert.Equal(DiscordMemberActionStatus.CannotRecord, cannotRecord.Status);
        Assert.Equal(503, cannotRecord.HttpStatus);
        Assert.Equal("not-set-up", cannotRecord.Code);
        Assert.Equal("This deployment cannot record actions.", cannotRecord.Message);

        var service = new DiscordMemberActionService(db, clock, discord, facts, partitions);

        var nobody = await service.ActAsync(DiscordMemberVerb.Kick, by.UserId, by.Username, " ", null, 0, null, Ct);
        Assert.Equal(400, nobody.HttpStatus);
        Assert.Null(nobody.Code);
        Assert.Equal("Say who, by their Discord id.", nobody.Message);

        var owner = await service.ActAsync(DiscordMemberVerb.Kick, by.UserId, by.Username, Owner, null, 0, null, Ct);
        Assert.Equal(403, owner.HttpStatus);
        Assert.Equal("refused", owner.Code);
        Assert.Equal(DiscordMemberActionService.OwnerMessage, owner.Message);

        var done = await service.ActAsync(DiscordMemberVerb.Kick, by.UserId, by.Username, Target, "Raid", 0, null, Ct);
        Assert.True(done.Done);
        Assert.True(done.Changed);
        Assert.Equal(200, done.HttpStatus);

        discord.Answer = DiscordMemberOutcome.Offline;
        var offline = await service.ActAsync(DiscordMemberVerb.Kick, by.UserId, by.Username, Target, null, 0, null, Ct);
        Assert.Equal(503, offline.HttpStatus);
        Assert.Equal("unavailable", offline.Code);

        discord.Answer = DiscordMemberOutcome.Failed("No.");
        var refused = await service.ActAsync(DiscordMemberVerb.Kick, by.UserId, by.Username, Target, null, 0, null, Ct);
        Assert.Equal(502, refused.HttpStatus);
        Assert.Equal("discord-refused", refused.Code);
        Assert.Equal("No.", refused.Message);

        discord.Answer = new DiscordMemberOutcome(false, null);
        var silent = await service.ActAsync(DiscordMemberVerb.Kick, by.UserId, by.Username, Target, null, 0, null, Ct);
        Assert.Equal("Discord refused.", silent.Message);
    }

    [Fact]
    public void TheAuditLogsReason_IsCutAtWhatDiscordKeeps()
    {
        Assert.Equal("Modbot: banned by alice", DiscordMemberActionService.Reason("banned", "alice", "  "));
        Assert.Equal("Modbot: banned by alice: Spam", DiscordMemberActionService.Reason("banned", "alice", " Spam "));

        var cut = DiscordMemberActionService.Reason("banned", "alice", new string('x', 600));
        Assert.Equal(DiscordMemberActionService.MaxReasonLength, cut.Length);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);
    }
}
