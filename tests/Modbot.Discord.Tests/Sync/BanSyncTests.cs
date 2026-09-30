using System.Text.Json.Nodes;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Sync;

/// <summary>
/// Ban sync: a ban crossing each way, the loop refusing to close in either direction, a person's
/// Discord ban telling itself apart from Modbot's own, and an unlinked member left alone
/// (M5 §4).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class BanSyncTests
{
    private const string Bot = "999000999";
    private const string Person = "usr_person";
    private const string Discord = "5001";

    private readonly PostgresFixture _db;

    public BanSyncTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeGateway Gateway() => new() { BotUserId = Bot };

    private static Task<TestServices> BothWaysAsync(PostgresFixture db)
        => SyncSetUp.CreateAsync(db, s =>
        {
            s.DiscordBanSyncToDiscord = true;
            s.DiscordBanSyncToVRChat = true;
        }, Ct);

    /// <summary>A ban in the VRChat group, as the group's audit log records it.</summary>
    private static Task<long> GroupBanAsync(TestServices services, string userId, string? actorId = "usr_moderator")
        => services.WriteFactAsync(new FactRecord
        {
            Type = FactType.MemberBanned,
            OccurredAt = services.Clock.UtcNow,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = userId,
            ActorPlatform = actorId is null ? null : FactPlatform.VRChat,
            ActorId = actorId,
            Source = FactSource.AuditLog,
            Data = new JsonObject { ["displayName"] = "Person" },
        }, Ct);

    /// <summary>A ban in Discord, as the server's audit log records it.</summary>
    private static Task<long> DiscordBanAsync(
        TestServices services, string userId, string? actorId, bool? actorIsBot = null, string? reason = null)
    {
        var data = new JsonObject { ["displayName"] = "Member" };

        if (actorIsBot is { } bot)
            data["actorIsBot"] = bot;

        if (reason is not null)
            data["reason"] = reason;

        return services.WriteFactAsync(new FactRecord
        {
            Type = FactType.DiscordMemberBanned,
            OccurredAt = services.Clock.UtcNow,
            SubjectPlatform = FactPlatform.Discord,
            SubjectId = userId,
            ActorPlatform = actorId is null ? null : FactPlatform.Discord,
            ActorId = actorId,
            Source = FactSource.Discord,
            Data = data,
        }, Ct);
    }

    private static Task<TestServices> DiscordIntoTheGroupAsync(PostgresFixture db, bool fromBots)
        => SyncSetUp.CreateAsync(db, s =>
        {
            s.DiscordBanSyncToVRChat = true;
            s.DiscordBanSyncFromBots = fromBots;
        }, Ct);

    private static string TextIn(ModbotEvent fact, string property)
        => System.Text.Json.JsonDocument.Parse(fact.Data ?? "{}").RootElement.GetProperty(property).GetString()!;

    [Fact]
    public async Task AVRChatBanBecomesADiscordBan()
    {
        await using var services = await BothWaysAsync(_db);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        var gateway = Gateway();
        await GroupBanAsync(services, Person);

        var pass = await SyncSetUp.BanPassAsync(services, gateway, Ct);

        Assert.Equal(1, pass.Copied);
        var (action, guildId, userId, _) = Assert.Single(gateway.Moderation);
        Assert.Equal("ban", action);
        Assert.Equal(SyncSetUp.Guild, guildId);
        Assert.Equal(Discord, userId);

        Assert.Single(await services.FactsOfTypeAsync(FactType.CopiedBan, Ct));
    }

    [Fact]
    public async Task ADiscordBanBecomesAVRChatBan()
    {
        await using var services = await BothWaysAsync(_db);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        var gateway = Gateway();
        await DiscordBanAsync(services, Discord, actorId: "4242");

        var pass = await SyncSetUp.BanPassAsync(services, gateway, Ct);

        Assert.Equal(1, pass.Copied);
        var (what, banned, _) = Assert.Single(services.VRChat.Actions);
        Assert.Equal("ban", what);
        Assert.Equal(Person, banned);
    }

    /// <summary>
    /// The loop, going out through Discord: the ban Modbot sent comes back as a Discord ban event
    /// and must not become a second VRChat ban.
    /// </summary>
    [Fact]
    public async Task TheDiscordBanModbotSentDoesNotComeBackAsAVRChatBan()
    {
        await using var services = await BothWaysAsync(_db);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        var gateway = Gateway();
        await GroupBanAsync(services, Person);
        await SyncSetUp.BanPassAsync(services, gateway, Ct);

        // Discord now reports the ban Modbot just made. Without an actor, which is what happens
        // when the bot may not read the server's audit log -- so only the copy record can tell.
        await DiscordBanAsync(services, Discord, actorId: null);

        var second = await SyncSetUp.BanPassAsync(services, gateway, Ct);

        Assert.Equal(0, second.Copied);
        Assert.Equal(1, second.Dropped);
        Assert.Empty(services.VRChat.Actions);
    }

    /// <summary>The loop the other way: a VRChat ban Modbot copied does not go back to Discord.</summary>
    [Fact]
    public async Task TheVRChatBanModbotSentDoesNotComeBackAsADiscordBan()
    {
        await using var services = await BothWaysAsync(_db);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        var gateway = Gateway();
        await DiscordBanAsync(services, Discord, actorId: "4242");
        await SyncSetUp.BanPassAsync(services, gateway, Ct);

        gateway.Moderation.Clear();

        // VRChat's audit log now shows the ban Modbot made, attributed -- as everything Modbot does
        // in VRChat is -- to Modbot's own account. Nothing about the actor can tell this apart from
        // a moderator's own ban, so only the copy record stops it.
        await GroupBanAsync(services, Person, actorId: "usr_modbot");

        var second = await SyncSetUp.BanPassAsync(services, gateway, Ct);

        Assert.Equal(0, second.Copied);
        Assert.Equal(1, second.Dropped);
        Assert.Empty(gateway.Moderation);
    }

    /// <summary>
    /// A person's Discord ban and Modbot's own look different in the record, and only the person's
    /// is acted on.
    /// </summary>
    [Fact]
    public async Task APersonsDiscordBanIsToldApartFromModbotsOwn()
    {
        await using var services = await BothWaysAsync(_db);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);
        await SyncSetUp.LinkAsync(services, "usr_other", "5002", [], [], Ct);

        var gateway = Gateway();

        // The bot's own account banned this one: Modbot's work, whoever started it.
        await DiscordBanAsync(services, Discord, actorId: Bot);

        // A person banned this one.
        await DiscordBanAsync(services, "5002", actorId: "4242");

        var pass = await SyncSetUp.BanPassAsync(services, gateway, Ct);

        Assert.Equal(1, pass.Copied);
        Assert.Equal(1, pass.Dropped);
        var (_, banned, _) = Assert.Single(services.VRChat.Actions);
        Assert.Equal("usr_other", banned);
    }

    /// <summary>
    /// One copy answers for one returning event and no more, so a second ban of the same person is
    /// still copied.
    /// </summary>
    [Fact]
    public async Task ASecondBanOfTheSamePersonIsStillCopied()
    {
        await using var services = await BothWaysAsync(_db);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        var gateway = Gateway();

        await GroupBanAsync(services, Person);
        await SyncSetUp.BanPassAsync(services, gateway, Ct);

        // The copy coming back, recognised and dropped.
        await DiscordBanAsync(services, Discord, actorId: null);
        await SyncSetUp.BanPassAsync(services, gateway, Ct);

        // A person bans them in Discord again. The copy record is spent, so this one crosses over.
        await DiscordBanAsync(services, Discord, actorId: null);
        var third = await SyncSetUp.BanPassAsync(services, gateway, Ct);

        Assert.Equal(1, third.Copied);
        Assert.Single(services.VRChat.Actions);
    }

    [Fact]
    public async Task SomebodyWithNoLinkIsLeftAlone()
    {
        await using var services = await BothWaysAsync(_db);
        await SyncSetUp.UnlinkedMemberAsync(services, "6001", [], Ct);

        var gateway = Gateway();
        await DiscordBanAsync(services, "6001", actorId: "4242");

        var pass = await SyncSetUp.BanPassAsync(services, gateway, Ct);

        Assert.Equal(0, pass.Copied);
        Assert.Equal(1, pass.NotLinked);
        Assert.Empty(services.VRChat.Actions);
        Assert.Empty(await SyncSetUp.CopiesAsync(services, Ct));
    }

    [Fact]
    public async Task ADirectionThatIsOffCopiesNothing()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, s => s.DiscordBanSyncToDiscord = true, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        var gateway = Gateway();
        await DiscordBanAsync(services, Discord, actorId: "4242");

        var pass = await SyncSetUp.BanPassAsync(services, gateway, Ct);

        Assert.Equal(0, pass.Copied);
        Assert.Empty(services.VRChat.Actions);
    }

    [Fact]
    public async Task AnUnbanCrossesOverTheSameWayTheBanDid()
    {
        await using var services = await BothWaysAsync(_db);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        var gateway = Gateway();

        await services.WriteFactAsync(new FactRecord
        {
            Type = FactType.MemberUnbanned,
            OccurredAt = services.Clock.UtcNow,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = Person,
            Source = FactSource.AuditLog,
        }, Ct);

        var pass = await SyncSetUp.BanPassAsync(services, gateway, Ct);

        Assert.Equal(1, pass.Copied);
        var (action, _, _, _) = Assert.Single(gateway.Moderation);
        Assert.Equal("unban", action);
        Assert.Single(await services.FactsOfTypeAsync(FactType.CopiedUnban, Ct));
    }

    /// <summary>
    /// A missing permission has to be visible: the copy is recorded as failed, the pass says so,
    /// and the fact it was going to copy is left for the next pass rather than lost.
    /// </summary>
    [Fact]
    public async Task ABotWithoutBanMembersFailsVisibly()
    {
        await using var services = await BothWaysAsync(_db);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        var gateway = Gateway();
        gateway.ModerationRefused = "The bot may not ban in this server. Give it Ban Members and a role above theirs.";

        await GroupBanAsync(services, Person);

        var pass = await SyncSetUp.BanPassAsync(services, gateway, Ct);

        Assert.Equal(0, pass.Copied);
        Assert.NotNull(pass.Problem);
        Assert.Contains("Ban Members", pass.Problem, StringComparison.Ordinal);

        Assert.Single(await services.FactsOfTypeAsync(FactType.CopyFailed, Ct));

        var copy = Assert.Single(await SyncSetUp.CopiesAsync(services, Ct));
        Assert.False(copy.Done);

        // The marker did not move past it, so fixing the permission and running again copies it.
        gateway.ModerationRefused = null;
        var again = await SyncSetUp.BanPassAsync(services, gateway, Ct);
        Assert.Equal(1, again.Copied);
    }

    /// <summary>
    /// A copy that failed excuses nothing. If it did, the next real ban of that person would be
    /// dropped as though Modbot had caused it.
    /// </summary>
    [Fact]
    public async Task AFailedCopyDoesNotExcuseTheNextRealBan()
    {
        await using var services = await BothWaysAsync(_db);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        var gateway = Gateway();
        gateway.ModerationError = "Discord is having a bad day.";

        await GroupBanAsync(services, Person);
        await SyncSetUp.BanPassAsync(services, gateway, Ct);

        gateway.ModerationError = null;

        // A person really does ban them in Discord. Nothing may swallow this.
        await DiscordBanAsync(services, Discord, actorId: "4242");
        var pass = await SyncSetUp.BanPassAsync(services, gateway, Ct);

        Assert.Equal(1, pass.Copied);
        Assert.Equal(0, pass.Dropped);
    }

    /// <summary>The catch-up's dry run reads both ban lists and changes nothing at all.</summary>
    [Fact]
    public async Task TheDryRunOfTheFirstRunChangesNothing()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, s => s.DiscordBanSyncToDiscord = true, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        await using (var db = services.Database.NewContext())
        {
            db.GroupBans.Add(new GroupBan
            {
                GroupId = SyncSetUp.Group,
                UserId = Person,
                BannedAt = services.Clock.UtcNow,
                FirstSeenAt = services.Clock.UtcNow,
                LastSeenAt = services.Clock.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        var gateway = Gateway();

        var preview = await SyncSetUp.CatchUpAsync(services, gateway, apply: false, Ct);

        Assert.Equal(1, preview.Total);
        Assert.Single(preview.Changes);
        Assert.Empty(gateway.Moderation);
        Assert.Empty(await SyncSetUp.CopiesAsync(services, Ct));
        Assert.Empty(await services.FactsOfTypeAsync(FactType.CopiedBan, Ct));

        // The same list, applied, is what actually happens.
        var run = await SyncSetUp.CatchUpAsync(services, gateway, apply: true, Ct);

        Assert.Equal(1, run.Total);
        Assert.Single(gateway.Moderation);
    }

    // ── Other bots' bans ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A ban another bot made is somebody else's rule. Off by default: it is skipped, counted, and
    /// leaves no record and no VRChat request behind.
    /// </summary>
    [Fact]
    public async Task ABanAnotherBotMadeIsSkippedUnlessCopyingThemIsSwitchedOn()
    {
        await using var services = await DiscordIntoTheGroupAsync(_db, fromBots: false);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        await DiscordBanAsync(services, Discord, actorId: "3131", actorIsBot: true);

        var pass = await SyncSetUp.BanPassAsync(services, Gateway(), Ct);

        Assert.Equal(0, pass.Copied);
        Assert.Equal(1, pass.FromBots);
        Assert.Empty(services.VRChat.Actions);
        Assert.Empty(await SyncSetUp.CopiesAsync(services, Ct));
    }

    [Fact]
    public async Task ABanAnotherBotMadeIsCopiedWhenTheSwitchIsOn()
    {
        await using var services = await DiscordIntoTheGroupAsync(_db, fromBots: true);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        await DiscordBanAsync(services, Discord, actorId: "3131", actorIsBot: true);

        var pass = await SyncSetUp.BanPassAsync(services, Gateway(), Ct);

        Assert.Equal(1, pass.Copied);
        Assert.Equal(0, pass.FromBots);
        Assert.Equal(Person, Assert.Single(services.VRChat.Actions).UserId);
    }

    /// <summary>A person's ban is copied whether or not the switch for bots is on.</summary>
    [Fact]
    public async Task ABanAPersonMadeIsCopiedWhateverTheBotSwitchSays()
    {
        await using var services = await DiscordIntoTheGroupAsync(_db, fromBots: false);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        await DiscordBanAsync(services, Discord, actorId: "4242", actorIsBot: false);

        var pass = await SyncSetUp.BanPassAsync(services, Gateway(), Ct);

        Assert.Equal(1, pass.Copied);
        Assert.Equal(0, pass.FromBots);
    }

    /// <summary>
    /// A fact from before the audit log said whether a bot did it is checked against the member
    /// list, which knows which of the server's members are bots.
    /// </summary>
    [Fact]
    public async Task AnOlderBanIsCheckedAgainstTheMemberList()
    {
        await using var services = await DiscordIntoTheGroupAsync(_db, fromBots: false);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);
        await SyncSetUp.LinkAsync(services, "usr_other", "5002", [], [], Ct);
        await SyncSetUp.AddBotMemberAsync(services, "3131", Ct);

        // No actorIsBot on either: the shape of every fact written before the field existed.
        await DiscordBanAsync(services, Discord, actorId: "3131");
        await DiscordBanAsync(services, "5002", actorId: "4242");

        var pass = await SyncSetUp.BanPassAsync(services, Gateway(), Ct);

        Assert.Equal(1, pass.Copied);
        Assert.Equal(1, pass.FromBots);
        Assert.Equal("usr_other", Assert.Single(services.VRChat.Actions).UserId);
    }

    /// <summary>
    /// A ban nobody is named for cannot be told apart from a person's, so it is copied, as it was
    /// before this check existed.
    /// </summary>
    [Fact]
    public async Task ABanWithNoNamedAuthorIsTreatedAsAPersons()
    {
        await using var services = await DiscordIntoTheGroupAsync(_db, fromBots: false);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        await DiscordBanAsync(services, Discord, actorId: null);

        var pass = await SyncSetUp.BanPassAsync(services, Gateway(), Ct);

        Assert.Equal(1, pass.Copied);
        Assert.Equal(0, pass.FromBots);
    }

    /// <summary>Unbans follow their bans: another bot lifting a ban is no more a moderator's decision than banning was.</summary>
    [Fact]
    public async Task AnUnbanAnotherBotMadeIsSkippedToo()
    {
        await using var services = await DiscordIntoTheGroupAsync(_db, fromBots: false);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        await services.WriteFactAsync(new FactRecord
        {
            Type = FactType.DiscordMemberUnbanned,
            OccurredAt = services.Clock.UtcNow,
            SubjectPlatform = FactPlatform.Discord,
            SubjectId = Discord,
            ActorPlatform = FactPlatform.Discord,
            ActorId = "3131",
            Source = FactSource.Discord,
            Data = new JsonObject { ["actorIsBot"] = true },
        }, Ct);

        var pass = await SyncSetUp.BanPassAsync(services, Gateway(), Ct);

        Assert.Equal(0, pass.Copied);
        Assert.Equal(1, pass.FromBots);
        Assert.Empty(services.VRChat.Actions);
    }

    // ── Discord's reason ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheReasonGivenInDiscordIsKeptOnTheCopiedBan()
    {
        await using var services = await DiscordIntoTheGroupAsync(_db, fromBots: false);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        await DiscordBanAsync(services, Discord, actorId: "4242", actorIsBot: false, reason: "spam links");

        await SyncSetUp.BanPassAsync(services, Gateway(), Ct);

        var copied = Assert.Single(await services.FactsOfTypeAsync(FactType.CopiedBan, Ct));
        Assert.Equal("spam links", TextIn(copied, "discordReason"));
    }

    [Fact]
    public async Task ACopiedBanWithNoReasonInDiscordCarriesNone()
    {
        await using var services = await DiscordIntoTheGroupAsync(_db, fromBots: false);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        await DiscordBanAsync(services, Discord, actorId: "4242", actorIsBot: false);

        await SyncSetUp.BanPassAsync(services, Gateway(), Ct);

        var copied = Assert.Single(await services.FactsOfTypeAsync(FactType.CopiedBan, Ct));
        Assert.False(System.Text.Json.JsonDocument.Parse(copied.Data ?? "{}").RootElement.TryGetProperty("discordReason", out _));
    }

    /// <summary>"Copy what is different" reads the reason off Discord's own ban list.</summary>
    [Fact]
    public async Task CopyWhatIsDifferentCarriesTheReasonFromDiscordsBanList()
    {
        await using var services = await DiscordIntoTheGroupAsync(_db, fromBots: false);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        var gateway = Gateway();
        gateway.Banned = [Discord];
        gateway.BanReasons[Discord] = "raid";

        var run = await SyncSetUp.CatchUpAsync(services, gateway, apply: true, Ct);

        Assert.Equal(1, run.Total);
        var copied = Assert.Single(await services.FactsOfTypeAsync(FactType.CopiedBan, Ct));
        Assert.Equal("raid", TextIn(copied, "discordReason"));
    }

    /// <summary>The first run follows the same rule about other bots as the pass does.</summary>
    [Fact]
    public async Task CopyWhatIsDifferentSkipsABanAnotherBotMadeUnlessCopyingThemIsOn()
    {
        await using var off = await DiscordIntoTheGroupAsync(_db, fromBots: false);
        await SyncSetUp.LinkAsync(off, Person, Discord, [], [], Ct);
        await DiscordBanAsync(off, Discord, actorId: "3131", actorIsBot: true);

        var skipped = Gateway();
        skipped.Banned = [Discord];

        var offRun = await SyncSetUp.CatchUpAsync(off, skipped, apply: true, Ct);

        Assert.Equal(0, offRun.Total);
        Assert.Empty(off.VRChat.Actions);

        await using var on = await DiscordIntoTheGroupAsync(_db, fromBots: true);
        await SyncSetUp.LinkAsync(on, Person, Discord, [], [], Ct);
        await DiscordBanAsync(on, Discord, actorId: "3131", actorIsBot: true);

        var copying = Gateway();
        copying.Banned = [Discord];

        var onRun = await SyncSetUp.CatchUpAsync(on, copying, apply: true, Ct);

        Assert.Equal(1, onRun.Total);
        Assert.Equal(Person, Assert.Single(on.VRChat.Actions).UserId);
    }

    // ── A ban made through Modbot ──────────────────────────────────────────────────────────

    /// <summary>
    /// A ban a moderator made in Modbot is made in Discord by Modbot itself, and that Discord ban
    /// coming back round is not a reason to ban in VRChat again -- with the bot named in the audit
    /// log, and with nobody named because the bot may not read it.
    /// </summary>
    [Fact]
    public async Task TheDiscordBanModbotMadeForAModbotBanDoesNotComeBackToVRChat()
    {
        await using var services = await DiscordIntoTheGroupAsync(_db, fromBots: false);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        var gateway = Gateway();
        var made = await SyncSetUp.ModbotBanAsync(services, gateway, banning: true, Person, Ct);
        Assert.Equal(LinkedDiscordStatus.Done, made.Status);

        await DiscordBanAsync(services, Discord, actorId: Bot, actorIsBot: true);
        await DiscordBanAsync(services, Discord, actorId: null);

        var pass = await SyncSetUp.BanPassAsync(services, gateway, Ct);

        Assert.Equal(0, pass.Copied);
        Assert.Equal(2, pass.Dropped);
        Assert.Empty(services.VRChat.Actions);
    }

    /// <summary>The same guard with the gateway not there to name the bot: only the copy record can tell.</summary>
    [Fact]
    public async Task TheCopyRecordAloneStopsAModbotBansDiscordHalfComingBack()
    {
        await using var services = await DiscordIntoTheGroupAsync(_db, fromBots: true);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        await SyncSetUp.ModbotBanAsync(services, Gateway(), banning: true, Person, Ct);
        await DiscordBanAsync(services, Discord, actorId: null);

        // No gateway, so no bot id to compare against.
        var pass = await SyncSetUp.BanPassAsync(services, null, Ct);

        Assert.Equal(1, pass.Dropped);
        Assert.Empty(services.VRChat.Actions);
    }

    /// <summary>
    /// The VRChat half of the same ban shows in the group's audit log as a ban by Modbot's own
    /// account. With copying group bans into Discord on, it must not be copied a second time.
    /// </summary>
    [Fact]
    public async Task TheVRChatHalfOfAModbotBanIsNotCopiedIntoDiscordAgain()
    {
        await using var services = await BothWaysAsync(_db);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        var gateway = Gateway();
        await SyncSetUp.ModbotBanAsync(services, gateway, banning: true, Person, Ct);
        Assert.Single(gateway.Moderation);

        gateway.Moderation.Clear();
        await GroupBanAsync(services, Person, actorId: "usr_modbot");

        var pass = await SyncSetUp.BanPassAsync(services, gateway, Ct);

        Assert.Equal(0, pass.Copied);
        Assert.Equal(1, pass.Dropped);
        Assert.Empty(gateway.Moderation);
    }

    [Fact]
    public async Task ABanAModeratorMadeInVRChatItselfIsStillCopiedWhenThatSwitchIsOn()
    {
        await using var services = await BothWaysAsync(_db);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        var gateway = Gateway();
        await GroupBanAsync(services, Person, actorId: "usr_moderator");

        var pass = await SyncSetUp.BanPassAsync(services, gateway, Ct);

        Assert.Equal(1, pass.Copied);
        Assert.Single(gateway.Moderation);
    }
}
