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
/// The Discord server's ban list as the Bans page shows it: read in full on sign-in and once a day
/// when the bot holds Ban Members, and kept current one row at a time by ban and unban events.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordBanListTests
{
    private readonly PostgresFixture _db;

    public DiscordBanListTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DiscordServerSnapshot Server(bool canBan, bool canViewAuditLog = false)
        => new(Guild, "The Black Cat", canViewAuditLog, BotCanManageRoles: false, [], [], BotCanBanMembers: canBan);

    private static async Task<(DiscordBotService Bot, FakeGateway Gateway)> ReadyBotAsync(
        TestServices services, bool canBan, bool canViewAuditLog = false, Action<FakeGateway>? configure = null)
    {
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next(g =>
        {
            g.Server = Server(canBan, canViewAuditLog);
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

    private static async Task<List<DiscordBan>> BansAsync(TestServices services)
    {
        await using var db = services.Database.NewContext();
        return await db.DiscordBans.AsNoTracking().Where(b => b.GuildId == Guild).OrderBy(b => b.UserId).ToListAsync(Ct);
    }

    private static async Task<DateTimeOffset?> ListedAtAsync(TestServices services)
    {
        await using var db = services.Database.NewContext();
        return await db.DiscordServers.AsNoTracking().Where(s => s.GuildId == Guild).Select(s => s.BansListedAt).FirstOrDefaultAsync(Ct);
    }

    [Fact]
    public async Task Signing_in_reads_the_whole_list_with_reasons_and_no_dates()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (_, gateway) = await ReadyBotAsync(services, canBan: true, configure: g =>
        {
            g.Banned = ["1", "2"];
            g.BanReasons["1"] = "spam links";
        });

        var bans = await BansAsync(services);

        Assert.Equal(1, gateway.BanListReads);
        Assert.Equal(["1", "2"], bans.Select(b => b.UserId));
        Assert.Equal("spam links", bans[0].Reason);
        Assert.Null(bans[1].Reason);
        Assert.All(bans, b => Assert.Null(b.BannedAt));
        Assert.All(bans, b => Assert.Null(b.LiftedAt));
        Assert.Equal(services.Clock.UtcNow, await ListedAtAsync(services));
    }

    [Fact]
    public async Task Without_Ban_Members_the_list_is_not_asked_for()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (_, gateway) = await ReadyBotAsync(services, canBan: false, configure: g => g.Banned = ["1"]);

        Assert.Equal(0, gateway.BanListReads);
        Assert.Empty(await BansAsync(services));
        Assert.Null(await ListedAtAsync(services));
    }

    [Fact]
    public async Task A_ban_event_adds_its_one_row_without_reading_the_list()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (_, gateway) = await ReadyBotAsync(services, canBan: true, configure: g => g.Banned = []);

        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await gateway.RaiseMemberBannedAsync(Guild, "7");

        var ban = Assert.Single(await BansAsync(services));
        Assert.Equal("7", ban.UserId);
        Assert.Equal(services.Clock.UtcNow, ban.BannedAt);
        Assert.Equal(services.Clock.UtcNow, ban.FirstSeenAt);
        Assert.Equal(1, gateway.BanListReads);
    }

    [Fact]
    public async Task An_unban_event_marks_the_ban_lifted_and_a_new_ban_clears_it()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (_, gateway) = await ReadyBotAsync(services, canBan: true, configure: g => g.Banned = ["1"]);

        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await gateway.RaiseMemberUnbannedAsync(Guild, "1");

        var lifted = Assert.Single(await BansAsync(services));
        Assert.Equal(services.Clock.UtcNow, lifted.LiftedAt);

        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await gateway.RaiseMemberBannedAsync(Guild, "1");

        var again = Assert.Single(await BansAsync(services));
        Assert.Null(again.LiftedAt);
        Assert.Equal(services.Clock.UtcNow, again.BannedAt);
    }

    [Fact]
    public async Task The_daily_read_finds_bans_lifted_and_added_while_nobody_was_looking()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (bot, gateway) = await ReadyBotAsync(services, canBan: true, configure: g => g.Banned = ["1", "2"]);

        // Not due yet: the sign-in read was just now.
        await bot.TickAsync(Ct);
        Assert.Equal(1, gateway.BanListReads);

        gateway.Banned = ["2", "3"];
        services.Clock.Advance(TimeSpan.FromDays(1));
        await bot.TickAsync(Ct);

        Assert.Equal(2, gateway.BanListReads);

        var bans = await BansAsync(services);
        Assert.Equal(services.Clock.UtcNow, bans.Single(b => b.UserId == "1").LiftedAt);
        Assert.Null(bans.Single(b => b.UserId == "2").LiftedAt);

        var found = bans.Single(b => b.UserId == "3");
        Assert.Null(found.BannedAt);
        Assert.Equal(services.Clock.UtcNow, found.FirstSeenAt);
        Assert.Equal(services.Clock.UtcNow, await ListedAtAsync(services));
    }

    [Fact]
    public async Task The_audit_log_gives_a_live_ban_its_reason_and_time()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (_, gateway) = await ReadyBotAsync(services, canBan: true, canViewAuditLog: true, configure: g => g.Banned = []);

        services.Clock.Advance(TimeSpan.FromMinutes(5));
        var at = services.Clock.UtcNow.AddSeconds(-2);

        await gateway.RaiseMemberBannedAsync(Guild, "1");
        gateway.AuditLog.Add(new DiscordAuditEntry("5002", at, DiscordAuditKinds.Ban, "9", "1", Reason: "spam links"));
        await gateway.RaiseAuditLogChangedAsync(Guild);

        var ban = Assert.Single(await BansAsync(services));
        Assert.Equal("spam links", ban.Reason);
        Assert.Equal(at, ban.BannedAt);
    }

    [Fact]
    public async Task A_banned_member_is_named_as_the_server_showed_them()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (_, gateway) = await ReadyBotAsync(services, canBan: true, configure: g =>
        {
            g.Banned = [];
            g.Members = [new DiscordMemberSnapshot("1", "ada", "Ada Lovelace", "Ada Lovelace", IsBot: false, null, [], null)];
        });

        await gateway.RaiseMemberBannedAsync(Guild, "1");

        var ban = Assert.Single(await BansAsync(services));
        Assert.Equal("Ada Lovelace", ban.DisplayName);
        Assert.Equal("ada", ban.Username);
    }
}
