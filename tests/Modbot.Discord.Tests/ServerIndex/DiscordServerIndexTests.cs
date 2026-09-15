using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Bot;
using Modbot.Discord.Gateway;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.ServerIndex;

/// <summary>
/// The bot's copy of its server's channels and roles: read whole on sign-in and on resume, kept
/// current from single events, and never deleted -- only marked removed.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordServerIndexTests
{
    private const string Guild = "424242";

    private static readonly DiscordChannelPermissions PostAndRead = new(
        ViewChannel: true, ReadMessageHistory: true, SendMessages: true, EmbedLinks: true, AttachFiles: true, ManageMessages: false);

    private readonly PostgresFixture _db;

    public DiscordServerIndexTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DiscordChannelSnapshot Channel(
        string id, string name, string type = DiscordChannelTypes.Text, string? category = null, int position = 0,
        DiscordChannelPermissions? permissions = null)
        => new(id, name, type, category, position, Nsfw: false, permissions ?? PostAndRead);

    private static DiscordRoleSnapshot Role(string id, string name, int position, bool canAssign = true, bool managed = false)
        => new(id, name, 0x3498DB, position, managed, Everyone: false, canAssign);

    private static DiscordServerSnapshot Server(
        IReadOnlyList<DiscordChannelSnapshot>? channels = null,
        IReadOnlyList<DiscordRoleSnapshot>? roles = null,
        bool canManageRoles = true)
        => new(
            Guild,
            "The Black Cat",
            BotCanViewAuditLog: true,
            canManageRoles,
            channels ??
            [
                Channel("100", "Staff", DiscordChannelTypes.Category),
                Channel("101", "mod-log", category: "100", position: 1),
                Channel("102", "announcements", DiscordChannelTypes.Announcement, position: 2),
            ],
            roles ??
            [
                new DiscordRoleSnapshot(Guild, "@everyone", 0, 0, Managed: false, Everyone: true, BotCanAssign: false),
                Role("201", "Member", 1),
                Role("202", "Modbot", 2, canAssign: false, managed: true),
            ]);

    private static DiscordBotService Service(TestServices services, FakeGatewayFactory gateways)
        => new(
            services.Provider.GetRequiredService<IServiceScopeFactory>(),
            gateways,
            services.Clock,
            services.Status,
            new DiscordBotOptions(),
            (_, _) => Task.CompletedTask);

    /// <summary>A bot signed in to <see cref="Guild"/>, ready, with the default server behind it.</summary>
    private static async Task<(DiscordBotService Bot, FakeGateway Gateway)> ReadyBotAsync(TestServices services)
    {
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next(g => g.Server = Server());
        var bot = Service(services, gateways);

        await services.ConfigureAsync(s =>
        {
            s.DiscordBotTokenEncrypted = services.Protector.Protect("token");
            s.DiscordGuildId = Guild;
        }, Ct);

        await bot.TickAsync(Ct);
        await gateway.RaiseReadyAsync();

        return (bot, gateway);
    }

    private static async Task<DiscordChannel> ChannelRowAsync(TestServices services, string id)
    {
        await using var db = services.Database.NewContext();
        return await db.DiscordChannels.AsNoTracking().SingleAsync(c => c.ChannelId == id, Ct);
    }

    private static async Task<DiscordRole> RoleRowAsync(TestServices services, string id)
    {
        await using var db = services.Database.NewContext();
        return await db.DiscordRoles.AsNoTracking().SingleAsync(r => r.RoleId == id, Ct);
    }

    [Fact]
    public async Task OnReady_EveryChannelAndRoleIsStored_WithTheBotsPermissions()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await ReadyBotAsync(services);

        await using var db = services.Database.NewContext();

        var server = await db.DiscordServers.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(Guild, server.GuildId);
        Assert.Equal("The Black Cat", server.Name);
        Assert.True(server.BotCanViewAuditLog);
        Assert.True(server.BotCanManageRoles);
        Assert.Equal(services.Clock.UtcNow, server.RefreshedAt);

        var channels = await db.DiscordChannels.AsNoTracking().OrderBy(c => c.ChannelId).ToListAsync(Ct);
        Assert.Equal(["100", "101", "102"], channels.Select(c => c.ChannelId));

        var log = channels[1];
        Assert.Equal("mod-log", log.Name);
        Assert.Equal(DiscordChannelTypes.Text, log.Type);
        Assert.Equal("100", log.CategoryId);
        Assert.True(log.BotCanView && log.BotCanSend && log.BotCanEmbedLinks && log.BotCanReadHistory && log.BotCanAttachFiles);
        Assert.False(log.BotCanManageMessages);
        Assert.Null(log.RemovedAt);

        Assert.Equal(DiscordChannelTypes.Announcement, channels[2].Type);

        var roles = await db.DiscordRoles.AsNoTracking().ToDictionaryAsync(r => r.RoleId, Ct);
        Assert.True(roles[Guild].Everyone);
        Assert.False(roles[Guild].BotCanAssign);
        Assert.True(roles["201"].BotCanAssign);
        Assert.Equal(0x3498DB, roles["201"].Color);
        Assert.True(roles["202"].Managed);
        Assert.False(roles["202"].BotCanAssign);

        Assert.Null(services.Status.Snapshot().LastError);
    }

    [Fact]
    public async Task OnResume_TheServerIsReadAgain_AndWhatIsGoneIsMarkedRemovedNotDeleted()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (_, gateway) = await ReadyBotAsync(services);
        var firstRead = services.Clock.UtcNow;

        // While the session was away, #announcements and the Member role were deleted.
        gateway.Server = Server(
            channels: [Channel("100", "Staff", DiscordChannelTypes.Category), Channel("101", "mod-log", category: "100", position: 1)],
            roles: [Role("202", "Modbot", 2, canAssign: false, managed: true)]);

        await gateway.RaiseDisconnectedAsync("The gateway connection closed.");
        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await gateway.RaiseResumedAsync();

        var announcements = await ChannelRowAsync(services, "102");
        Assert.Equal("announcements", announcements.Name);
        Assert.Equal(services.Clock.UtcNow, announcements.RemovedAt);

        var member = await RoleRowAsync(services, "201");
        Assert.Equal(services.Clock.UtcNow, member.RemovedAt);

        var log = await ChannelRowAsync(services, "101");
        Assert.Null(log.RemovedAt);
        Assert.Equal(firstRead, log.UpdatedAt);

        await using var db = services.Database.NewContext();
        var server = await db.DiscordServers.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(services.Clock.UtcNow, server.RefreshedAt);
    }

    [Fact]
    public async Task AChannelThatComesBack_IsNoLongerMarkedRemoved()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (_, gateway) = await ReadyBotAsync(services);

        await gateway.RaiseChannelRemovedAsync(Guild, "101");
        Assert.NotNull((await ChannelRowAsync(services, "101")).RemovedAt);

        await gateway.RaiseResumedAsync();
        Assert.Null((await ChannelRowAsync(services, "101")).RemovedAt);
    }

    [Fact]
    public async Task ACreatedChannel_IsAdded()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (_, gateway) = await ReadyBotAsync(services);
        var readsBefore = gateway.ReadServerCalls;

        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await gateway.RaiseChannelChangedAsync(Guild, Channel("103", "instances", category: "100", position: 3));

        var added = await ChannelRowAsync(services, "103");
        Assert.Equal("instances", added.Name);
        Assert.Equal("100", added.CategoryId);
        Assert.Equal(services.Clock.UtcNow, added.FirstSeenAt);

        // One channel is saved on its own; the server is not read again for it.
        Assert.Equal(readsBefore, gateway.ReadServerCalls);

        await using var db = services.Database.NewContext();
        Assert.Equal(services.Clock.UtcNow, (await db.DiscordServers.AsNoTracking().SingleAsync(Ct)).UpdatedAt);
    }

    [Fact]
    public async Task APermissionOverwriteTakingAwaySendMessages_IsSeenOnTheChannel()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (_, gateway) = await ReadyBotAsync(services);
        Assert.True((await ChannelRowAsync(services, "101")).BotCanSend);

        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await gateway.RaiseChannelChangedAsync(
            Guild,
            Channel("101", "mod-log", category: "100", position: 1, permissions: PostAndRead with { SendMessages = false }));

        var log = await ChannelRowAsync(services, "101");
        Assert.False(log.BotCanSend);
        Assert.True(log.BotCanView);
        Assert.Equal(services.Clock.UtcNow, log.UpdatedAt);

        // And given back.
        await gateway.RaiseChannelChangedAsync(Guild, Channel("101", "mod-log", category: "100", position: 1));
        Assert.True((await ChannelRowAsync(services, "101")).BotCanSend);
    }

    [Fact]
    public async Task ADeletedChannel_IsMarkedRemoved()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (_, gateway) = await ReadyBotAsync(services);

        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await gateway.RaiseChannelRemovedAsync(Guild, "102");

        var removed = await ChannelRowAsync(services, "102");
        Assert.Equal("announcements", removed.Name);
        Assert.Equal(services.Clock.UtcNow, removed.RemovedAt);

        // Removing one that was never stored is nothing.
        await gateway.RaiseChannelRemovedAsync(Guild, "999");
        Assert.Null(services.Status.Snapshot().LastError);
    }

    [Fact]
    public async Task AChangedCategory_ReadsTheWholeServer_BecauseItsOverwritesReachTheChannelsUnderIt()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (_, gateway) = await ReadyBotAsync(services);
        var readsBefore = gateway.ReadServerCalls;

        var hidden = PostAndRead with { ViewChannel = false, SendMessages = false };
        gateway.Server = Server(channels:
        [
            Channel("100", "Staff", DiscordChannelTypes.Category, permissions: hidden),
            Channel("101", "mod-log", category: "100", position: 1, permissions: hidden),
            Channel("102", "announcements", DiscordChannelTypes.Announcement, position: 2),
        ]);

        await gateway.RaiseChannelChangedAsync(Guild, Channel("100", "Staff", DiscordChannelTypes.Category, permissions: hidden));

        Assert.Equal(readsBefore + 1, gateway.ReadServerCalls);
        var log = await ChannelRowAsync(services, "101");
        Assert.False(log.BotCanView);
        Assert.False(log.BotCanSend);
    }

    [Fact]
    public async Task ARoleOrTheBotsOwnRolesChanging_ReadsTheWholeServerAgain()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (_, gateway) = await ReadyBotAsync(services);

        // The bot lost Manage Roles: nothing can be handed out, and a new role appeared.
        gateway.Server = Server(
            roles:
            [
                Role("201", "Member", 1, canAssign: false),
                Role("202", "Modbot", 3, canAssign: false, managed: true),
                Role("203", "Verified", 2, canAssign: false),
            ],
            canManageRoles: false);

        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await gateway.RaiseServerChangedAsync(Guild);

        Assert.False((await RoleRowAsync(services, "201")).BotCanAssign);
        Assert.Equal("Verified", (await RoleRowAsync(services, "203")).Name);
        Assert.Equal(services.Clock.UtcNow, (await RoleRowAsync(services, Guild)).RemovedAt);

        await using var db = services.Database.NewContext();
        Assert.False((await db.DiscordServers.AsNoTracking().SingleAsync(Ct)).BotCanManageRoles);
    }

    [Fact]
    public async Task ChangesInAnotherServerTheBotIsIn_AreIgnored()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (_, gateway) = await ReadyBotAsync(services);
        var readsBefore = gateway.ReadServerCalls;

        await gateway.RaiseChannelChangedAsync("777", Channel("900", "elsewhere"));
        await gateway.RaiseChannelRemovedAsync("777", "101");
        await gateway.RaiseServerChangedAsync("777");

        await using var db = services.Database.NewContext();
        Assert.False(await db.DiscordChannels.AnyAsync(c => c.ChannelId == "900", Ct));
        Assert.Null((await ChannelRowAsync(services, "101")).RemovedAt);
        Assert.Equal(readsBefore, gateway.ReadServerCalls);
    }

    [Fact]
    public async Task WhenTheBotIsNotInTheServer_NothingIsStored()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next();
        var bot = Service(services, gateways);

        await services.ConfigureAsync(s =>
        {
            s.DiscordBotTokenEncrypted = services.Protector.Protect("token");
            s.DiscordGuildId = Guild;
        }, Ct);

        await bot.TickAsync(Ct);
        await gateway.RaiseReadyAsync();

        await using var db = services.Database.NewContext();
        Assert.False(await db.DiscordServers.AnyAsync(Ct));
        Assert.False(await db.DiscordChannels.AnyAsync(Ct));
    }
}
