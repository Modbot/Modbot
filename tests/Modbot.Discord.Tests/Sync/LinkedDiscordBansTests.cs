using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Discord.Sync;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Sync;

/// <summary>
/// A ban or unban made through Modbot reaches the person's linked Discord account too, whatever
/// the ban sync switches say; a person with no link, or a group with no Discord, is left alone; and
/// Discord refusing is recorded and handed back rather than lost.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class LinkedDiscordBansTests
{
    private const string Person = "usr_person";
    private const string Discord = "5001";

    private readonly PostgresFixture _db;

    public LinkedDiscordBansTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string TextIn(ModbotEvent fact, string property)
        => JsonDocument.Parse(fact.Data ?? "{}").RootElement.GetProperty(property).GetString()!;

    /// <summary>Every ban sync switch is off, which is the default, and the ban still lands.</summary>
    [Fact]
    public async Task ABanThroughModbotBansTheLinkedAccount_WithNoSyncSwitchOn()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        var gateway = new FakeGateway();
        var outcome = await SyncSetUp.ModbotBanAsync(services, gateway, banning: true, Person, Ct);

        Assert.Equal(LinkedDiscordStatus.Done, outcome.Status);

        var (action, guildId, userId, reason) = Assert.Single(gateway.Moderation);
        Assert.Equal("ban", action);
        Assert.Equal(SyncSetUp.Guild, guildId);
        Assert.Equal(Discord, userId);
        Assert.Contains("Mod Person", reason, StringComparison.Ordinal);
        Assert.Contains("Spam, Harassment", reason, StringComparison.Ordinal);

        // Written down like every other Modbot action, against the person it happened to.
        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.CopiedBan, Ct));
        Assert.Equal(FactPlatform.Discord, fact.SubjectPlatform);
        Assert.Equal(Discord, fact.SubjectId);
        Assert.Equal("Mod Person", TextIn(fact, "by"));
        Assert.Equal(CopyDirections.ToDiscord, TextIn(fact, "direction"));

        // And the record that stops it coming back as a copy, written for it.
        var record = Assert.Single(await SyncSetUp.CopiesAsync(services, Ct));
        Assert.Equal(CopyKinds.ModbotBan, record.Kind);
        Assert.Equal(CopyDirections.ToDiscord, record.Direction);
        Assert.Equal(Discord, record.SubjectId);
        Assert.Equal(Person, record.OtherSideId);
        Assert.Equal(true, record.Done);

        // It names the fact that recorded the ban in VRChat, so the two read as one action.
        Assert.Equal(42, JsonDocument.Parse(fact.Data ?? "{}").RootElement.GetProperty("causedBy").GetInt64());
        Assert.Equal(42, record.CausedByFactId);
    }

    [Fact]
    public async Task AnUnbanThroughModbotLiftsTheLinkedAccountsBan()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        var gateway = new FakeGateway();
        var outcome = await SyncSetUp.ModbotBanAsync(services, gateway, banning: false, Person, Ct);

        Assert.Equal(LinkedDiscordStatus.Done, outcome.Status);
        Assert.Equal("unban", Assert.Single(gateway.Moderation).Action);
        Assert.Single(await services.FactsOfTypeAsync(FactType.CopiedUnban, Ct));

        var record = Assert.Single(await SyncSetUp.CopiesAsync(services, Ct));
        Assert.Equal(CopyKinds.ModbotUnban, record.Kind);
    }

    [Fact]
    public async Task SomebodyWithNoLinkedAccountIsLeftAlone_WithoutAWord()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        await SyncSetUp.UnlinkedMemberAsync(services, "6001", [], Ct);

        var gateway = new FakeGateway();
        var outcome = await SyncSetUp.ModbotBanAsync(services, gateway, banning: true, Person, Ct);

        Assert.Equal(LinkedDiscordStatus.Skipped, outcome.Status);
        Assert.Null(outcome.Error);
        Assert.Empty(gateway.Moderation);
        Assert.Empty(await SyncSetUp.CopiesAsync(services, Ct));
        Assert.Empty(await services.FactsOfTypeAsync(FactType.CopiedBan, Ct));
        Assert.Empty(await services.FactsOfTypeAsync(FactType.CopyFailed, Ct));
    }

    [Fact]
    public async Task ALinkThatHasEndedIsNotALinkedAccount()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);
        await SyncSetUp.UnlinkAsync(services, Discord, Ct);

        var gateway = new FakeGateway();
        var outcome = await SyncSetUp.ModbotBanAsync(services, gateway, banning: true, Person, Ct);

        Assert.Equal(LinkedDiscordStatus.Skipped, outcome.Status);
        Assert.Empty(gateway.Moderation);
    }

    [Fact]
    public async Task AGroupWithNoDiscordServerSetUpHasNothingToDoInDiscord()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, s => s.DiscordGuildId = null, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        var gateway = new FakeGateway();
        var outcome = await SyncSetUp.ModbotBanAsync(services, gateway, banning: true, Person, Ct);

        Assert.Equal(LinkedDiscordStatus.Skipped, outcome.Status);
        Assert.Empty(gateway.Moderation);
        Assert.Empty(await SyncSetUp.CopiesAsync(services, Ct));
    }

    /// <summary>
    /// Discord saying no is handed back, recorded as a failed copy in the log, and leaves a copy
    /// record that says it failed -- which excuses nothing.
    /// </summary>
    [Fact]
    public async Task ABotWithoutBanMembersFailsVisibly()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        var gateway = new FakeGateway
        {
            ModerationRefused = "The bot may not ban in this server. Give it Ban Members and a role above theirs.",
        };

        var outcome = await SyncSetUp.ModbotBanAsync(services, gateway, banning: true, Person, Ct);

        Assert.Equal(LinkedDiscordStatus.Failed, outcome.Status);
        Assert.Contains("Ban Members", outcome.Error, StringComparison.Ordinal);

        var failed = Assert.Single(await services.FactsOfTypeAsync(FactType.CopyFailed, Ct));
        Assert.Contains("Ban Members", TextIn(failed, "error"), StringComparison.Ordinal);
        Assert.Equal("Mod Person", TextIn(failed, "by"));
        Assert.Empty(await services.FactsOfTypeAsync(FactType.CopiedBan, Ct));

        var record = Assert.Single(await SyncSetUp.CopiesAsync(services, Ct));
        Assert.Equal(false, record.Done);
        Assert.NotNull(record.SeenBackAt);
    }

    [Fact]
    public async Task ABotThatIsNotConnectedFailsVisibly()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        var outcome = await SyncSetUp.ModbotBanAsync(services, null, banning: true, Person, Ct);

        Assert.Equal(LinkedDiscordStatus.Failed, outcome.Status);
        Assert.Equal("The Discord bot is not connected.", outcome.Error);
        Assert.Single(await services.FactsOfTypeAsync(FactType.CopyFailed, Ct));
    }

    /// <summary>Somebody who is not banned in Discord has nothing to unban: not a failure, and not written up as a change.</summary>
    [Fact]
    public async Task AnUnbanOfSomebodyNotBannedInDiscordIsNotAFailureAndNotAChange()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        var gateway = new FakeGateway();
        gateway.NothingToDo.Add(Discord);

        var outcome = await SyncSetUp.ModbotBanAsync(services, gateway, banning: false, Person, Ct);

        Assert.Equal(LinkedDiscordStatus.Unchanged, outcome.Status);
        Assert.Null(outcome.Error);
        Assert.Empty(await services.FactsOfTypeAsync(FactType.CopiedUnban, Ct));
        Assert.Empty(await services.FactsOfTypeAsync(FactType.CopyFailed, Ct));
    }

    /// <summary>Discord keeps 512 characters of a reason. A longer one is cut, not refused.</summary>
    [Fact]
    public async Task ALongReasonIsCutToWhatDiscordKeeps()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);

        var gateway = new FakeGateway();
        await SyncSetUp.ModbotBanAsync(services, gateway, banning: true, Person, Ct, why: new string('x', 2000));

        var (_, _, _, reason) = Assert.Single(gateway.Moderation);
        Assert.Equal(LinkedDiscordBans.MaxReasonLength, reason.Length);
    }

    /// <summary>The Discord half is asked for only ever for the person's own linked account, not somebody else's.</summary>
    [Fact]
    public async Task OnlyTheBannedPersonsOwnAccountIsBanned()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        await SyncSetUp.LinkAsync(services, Person, Discord, [], [], Ct);
        await SyncSetUp.LinkAsync(services, "usr_other", "5002", [], [], Ct);

        var gateway = new FakeGateway();
        await SyncSetUp.ModbotBanAsync(services, gateway, banning: true, Person, Ct);

        Assert.Equal(Discord, Assert.Single(gateway.Moderation).UserId);

        await using var db = services.Database.NewContext();
        Assert.Equal(1, await db.CopiedActions.CountAsync(Ct));
    }
}
