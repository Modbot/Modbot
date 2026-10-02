using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Gate;
using Modbot.Discord.Gateway;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Gate;

/// <summary>
/// The join gate (join gate design): a new member is welcomed with Get in, gets the member role
/// once the steps are done, is warned halfway and removed at the end, and is never removed while
/// Modbot is the reason they are stuck. Watch only does nothing in Discord.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class JoinGateTests
{
    private const string Guild = "700";
    private const string Role = "800";
    private const string Channel = "900";

    private readonly PostgresFixture _db;

    public JoinGateTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TestServices> SetUpAsync(
        PostgresFixture db, string mode = DiscordGateModes.On, int? removeAfter = null, Action<Settings>? more = null)
    {
        var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s =>
        {
            s.DiscordGuildId = Guild;
            s.DiscordGateMode = mode;
            s.DiscordGateMemberRoleId = Role;
            s.DiscordGateChannelId = Channel;
            s.DiscordGateRemoveAfterMinutes = removeAfter;
            s.DiscordGateStartedAt = services.Clock.UtcNow;
            more?.Invoke(s);
        }, Ct);
        return services;
    }

    private static async Task<bool> JoinAsync(TestServices services, FakeGateway gateway, string userId, params string[] roles)
    {
        using var scope = services.Scope();
        var member = new DiscordMemberSnapshot(userId, "newcomer", "newcomer", null, false, services.Clock.UtcNow, roles, null);
        return await scope.ServiceProvider.GetRequiredService<JoinGate>()
            .JoinedAsync(gateway, new DiscordMemberJoin(Guild, userId, "newcomer", false, member), Ct);
    }

    private static async Task<JoinGatePass> PassAsync(TestServices services, FakeGateway gateway)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<JoinGate>().RunAsync(gateway, Ct);
    }

    private static async Task<DiscordReply> PressAsync(TestServices services, FakeGateway gateway, string userId, string button)
    {
        using var scope = services.Scope();
        var press = new DiscordButtonPress(userId, "newcomer", DiscordActionButton.Marked(button, Guild), (_, _) => Task.CompletedTask);
        return await scope.ServiceProvider.GetRequiredService<JoinGate>().PressAsync(gateway, press, Ct);
    }

    private static async Task<DiscordGateEntry> EntryAsync(TestServices services, string userId)
    {
        await using var db = services.Database.NewContext();
        return await db.DiscordGateEntries.AsNoTracking()
            .Where(e => e.DiscordUserId == userId)
            .OrderByDescending(e => e.JoinedAt)
            .FirstAsync(Ct);
    }

    [Fact]
    public async Task ANewMember_IsWelcomedWithGetIn_AndWaits()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();

        Assert.True(await JoinAsync(services, gateway, "1001"));

        var dm = Assert.Single(gateway.ActionMessages);
        Assert.Equal("1001", dm.UserId);
        var button = Assert.Single(dm.Actions);
        Assert.Equal(JoinGateButtons.GetInLabel, button.Label);
        Assert.Equal(JoinGateButtons.GetIn, DiscordActionButton.Plain(button.Id));

        var entry = await EntryAsync(services, "1001");
        Assert.Null(entry.ClosedAt);
        Assert.False(entry.WatchOnly);
        Assert.Empty(gateway.RoleChanges);
    }

    [Fact]
    public async Task SomebodyWhoJoinsWithTheMemberRole_IsNotGated()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();

        Assert.False(await JoinAsync(services, gateway, "1002", Role));

        Assert.Empty(gateway.ActionMessages);
        await using var db = services.Database.NewContext();
        Assert.False(await db.DiscordGateEntries.AnyAsync(Ct));
    }

    [Fact]
    public async Task WatchOnly_RecordsTheJoin_AndSendsNothing()
    {
        await using var services = await SetUpAsync(_db, DiscordGateModes.Watch);
        var gateway = new FakeGateway();

        Assert.False(await JoinAsync(services, gateway, "1003"));

        Assert.Empty(gateway.ActionMessages);
        Assert.True((await EntryAsync(services, "1003")).WatchOnly);
    }

    [Fact]
    public async Task IAgree_GivesTheMemberRole_AndRecordsThePass()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();
        await JoinAsync(services, gateway, "1004");

        var steps = await PressAsync(services, gateway, "1004", JoinGateButtons.GetIn);
        Assert.Contains(steps.Actions ?? [], a => DiscordActionButton.Plain(a.Id) == JoinGateButtons.Agree);
        Assert.Empty(gateway.RoleChanges);

        var reply = await PressAsync(services, gateway, "1004", JoinGateButtons.Agree);

        Assert.Equal(JoinGate.YoureIn, reply.Text);
        var change = Assert.Single(gateway.RoleChanges);
        Assert.True(change.Added);
        Assert.Equal(Role, change.RoleId);
        Assert.Contains(JoinGate.PassedReason, gateway.RoleReasons);

        Assert.Equal(DiscordGateOutcomes.Passed, (await EntryAsync(services, "1004")).Outcome);
        Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordGatePassed, Ct));
    }

    [Fact]
    public async Task WhileHeld_NobodyGetsInByThemselves()
    {
        await using var services = await SetUpAsync(_db, more: s => s.DiscordGateHeldAt = new DateTimeOffset(2026, 9, 13, 11, 0, 0, TimeSpan.Zero));
        var gateway = new FakeGateway();
        await JoinAsync(services, gateway, "1005");

        var reply = await PressAsync(services, gateway, "1005", JoinGateButtons.Agree);
        await PassAsync(services, gateway);

        Assert.Equal(JoinGate.Held, reply.Text);
        Assert.Empty(gateway.RoleChanges);
        Assert.Null((await EntryAsync(services, "1005")).ClosedAt);
    }

    [Fact]
    public async Task SomebodyWhoNeverFinishes_IsWarnedHalfway_AndRemovedAtTheEnd()
    {
        await using var services = await SetUpAsync(_db, removeAfter: 30);
        var gateway = new FakeGateway();
        await JoinAsync(services, gateway, "1006");
        gateway.ActionMessages.Clear();

        // A pass a minute: time counts a minute at a time.
        for (var minute = 0; minute < 16; minute++)
        {
            services.Clock.Advance(TimeSpan.FromMinutes(1));
            await PassAsync(services, gateway);
        }

        var warning = Assert.Single(gateway.ActionMessages);
        Assert.Equal("1006", warning.UserId);
        Assert.Empty(gateway.Moderation);

        for (var minute = 0; minute < 15; minute++)
        {
            services.Clock.Advance(TimeSpan.FromMinutes(1));
            await PassAsync(services, gateway);
        }

        var removal = Assert.Single(gateway.Moderation);
        Assert.Equal("remove", removal.Action);
        Assert.Equal(JoinGate.RemovedReason, removal.Reason);
        Assert.Equal(DiscordGateOutcomes.Removed, (await EntryAsync(services, "1006")).Outcome);
        Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordGateRemoved, Ct));
    }

    /// <summary>A day without a pass -- Modbot down -- counts as two minutes, not a day.</summary>
    [Fact]
    public async Task ADayWithoutAPass_CountsAsTwoMinutes()
    {
        await using var services = await SetUpAsync(_db, removeAfter: 30);
        var gateway = new FakeGateway();
        await JoinAsync(services, gateway, "1007");

        services.Clock.Advance(TimeSpan.FromDays(1));
        await PassAsync(services, gateway);

        var entry = await EntryAsync(services, "1007");
        Assert.Equal(JoinGate.MostMinutesAPass, entry.MinutesCounted);
        Assert.Null(entry.ClosedAt);
        Assert.Empty(gateway.Moderation);
    }

    [Fact]
    public async Task WhileTheBotCannotGiveTheRole_TimeDoesNotCount()
    {
        await using var services = await SetUpAsync(_db, removeAfter: 30);
        var gateway = new FakeGateway { RoleError = "Missing Permissions" };
        await JoinAsync(services, gateway, "1008");
        await JoinAsync(services, gateway, "1009");

        // One person is done and the role is refused: Modbot is the reason, for everybody.
        await PressAsync(services, gateway, "1008", JoinGateButtons.Agree);

        for (var minute = 0; minute < 40; minute++)
        {
            services.Clock.Advance(TimeSpan.FromMinutes(1));
            await PassAsync(services, gateway);
        }

        Assert.Empty(gateway.Moderation);
        Assert.Equal(0, (await EntryAsync(services, "1009")).MinutesCounted);
    }

    [Fact]
    public async Task WatchOnly_RecordsWhenItWouldHaveRemoved_AndRemovesNobody()
    {
        await using var services = await SetUpAsync(_db, DiscordGateModes.Watch, removeAfter: 30);
        var gateway = new FakeGateway();
        await JoinAsync(services, gateway, "1010");

        for (var minute = 0; minute < 31; minute++)
        {
            services.Clock.Advance(TimeSpan.FromMinutes(1));
            await PassAsync(services, gateway);
        }

        var entry = await EntryAsync(services, "1010");
        Assert.NotNull(entry.WarnedAt);
        Assert.NotNull(entry.WouldRemoveAt);
        Assert.Null(entry.ClosedAt);
        Assert.Empty(gateway.Moderation);
        Assert.Empty(gateway.ActionMessages);
        Assert.Empty(gateway.ActionPosts);
    }

    [Fact]
    public async Task TheMemberRoleGivenInDiscord_CountsAsLetIn()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();
        await JoinAsync(services, gateway, "1011");

        await using (var db = services.Database.NewContext())
        {
            var now = services.Clock.UtcNow;
            db.DiscordMembers.Add(new DiscordMember
            {
                GuildId = Guild,
                UserId = "1011",
                Username = "newcomer",
                DisplayName = "newcomer",
                Roles = $"[\"{Role}\"]",
                FirstSeenAt = now,
                JoinedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync(Ct);
        }

        await PassAsync(services, gateway);

        Assert.Equal(DiscordGateOutcomes.LetInInDiscord, (await EntryAsync(services, "1011")).Outcome);
        Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordGateLetIn, Ct));
        Assert.Empty(gateway.RoleChanges);
    }

    [Fact]
    public async Task TheGateMessage_IsPostedOnce_WithGetIn()
    {
        await using var services = await SetUpAsync(_db, more: s => s.DiscordGateMessage = "Read the rules.");
        var gateway = new FakeGateway();

        await PassAsync(services, gateway);
        await PassAsync(services, gateway);

        var post = Assert.Single(gateway.ActionPosts);
        Assert.Equal(Channel, post.ChannelId);
        Assert.Equal("Read the rules.", post.Text);
        Assert.Equal(JoinGateButtons.GetIn, DiscordActionButton.Plain(Assert.Single(post.Actions).Id));
    }

    [Fact]
    public async Task HoldFromAnAlert_NeedsManageTheJoinGate()
    {
        await using var services = await SetUpAsync(_db);
        var gateway = new FakeGateway();
        await services.LinkedAccountAsync("5001", ModbotPermissions.ViewMembers, ct: Ct);
        await services.LinkedAccountAsync("5002", ModbotPermissions.ManageJoinGate, ct: Ct);

        var refused = await PressAsync(services, gateway, "5001", JoinGateButtons.Hold);
        Assert.Contains("Manage the join gate", refused.Text, StringComparison.Ordinal);
        Assert.Null((await services.SettingsAsync(Ct)).DiscordGateHeldAt);

        await PressAsync(services, gateway, "5002", JoinGateButtons.Hold);
        Assert.NotNull((await services.SettingsAsync(Ct)).DiscordGateHeldAt);
        Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordGateHeld, Ct));
    }

    [Theory]
    [InlineData(0.5, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(90, 2)]
    public void TimeCounts_InWholeMinutes_AtMostTwoAPass(double minutesSince, int counted)
    {
        var start = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var entry = new DiscordGateEntry { LastCountedAt = start };

        JoinGate.CountTime(entry, start.AddMinutes(minutesSince), counts: true);

        Assert.Equal(counted, entry.MinutesCounted);
    }

    [Fact]
    public void TimeThatDoesNotCount_StartsTheClockAgain()
    {
        var start = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var entry = new DiscordGateEntry { LastCountedAt = start };

        JoinGate.CountTime(entry, start.AddMinutes(10), counts: false);

        Assert.Equal(0, entry.MinutesCounted);
        Assert.Equal(start.AddMinutes(10), entry.LastCountedAt);
    }
}
