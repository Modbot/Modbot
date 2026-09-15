using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Discord;
using Modbot.Discord.Bot;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Bot;

/// <summary>
/// The connection loop against a fake gateway: starts only when configured, follows settings
/// changes, registers commands when ready, and backs off when Discord says no.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordBotServiceTests
{
    private readonly PostgresFixture _db;

    public DiscordBotServiceTests(PostgresFixture db) => _db = db;

    private static DiscordBotService Service(TestServices services, FakeGatewayFactory gateways)
        => new(
            services.Provider.GetRequiredService<IServiceScopeFactory>(),
            gateways,
            services.Clock,
            services.Status,
            new DiscordBotOptions
            {
                FirstRetry = TimeSpan.FromSeconds(30),
                MaxRetry = TimeSpan.FromMinutes(10),
                RebuildAfterDisconnected = TimeSpan.FromMinutes(3),
            },
            (_, _) => Task.CompletedTask);

    private static Task ConfigureBotAsync(TestServices services, string token, string guildId, CancellationToken ct)
        => services.ConfigureAsync(s =>
        {
            s.DiscordBotTokenEncrypted = services.Protector.Protect(token);
            s.DiscordGuildId = guildId;
        }, ct);

    [Fact]
    public async Task WithoutATokenAndGuild_TheBotDoesNotStart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateways = new FakeGatewayFactory();
        var bot = Service(services, gateways);

        await bot.TickAsync(ct);

        Assert.Empty(gateways.Created);
        Assert.Equal(DiscordBotState.NotConfigured, services.Status.Snapshot().State);

        // A token alone is not enough: commands are registered on a guild.
        await services.ConfigureAsync(s => s.DiscordBotTokenEncrypted = services.Protector.Protect("tok"), ct);
        await bot.TickAsync(ct);
        Assert.Empty(gateways.Created);
    }

    [Fact]
    public async Task WhenConfigured_ItSignsInWithTheDecryptedToken_AndRegistersCommandsOnReady()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next();
        var bot = Service(services, gateways);

        await ConfigureBotAsync(services, "secret-token", "424242", ct);
        await bot.TickAsync(ct);

        Assert.Equal(["secret-token"], gateway.Tokens);
        Assert.Equal(DiscordBotState.Connecting, services.Status.Snapshot().State);
        Assert.Null(bot.ReadyGateway);

        await gateway.RaiseReadyAsync();

        Assert.Equal("424242", gateway.RegisteredGuildId);
        Assert.Equal(DiscordCommands.All.Select(c => c.Name), gateway.RegisteredCommands.Select(c => c.Name));
        var snapshot = services.Status.Snapshot();
        Assert.Equal(DiscordBotState.Connected, snapshot.State);
        Assert.Equal(DiscordCommands.All.Count, snapshot.CommandsRegistered);
        Assert.Equal(services.Clock.UtcNow, snapshot.ConnectedSince);
        Assert.Same(gateway, bot.ReadyGateway);

        // Another tick with the same settings leaves the session alone.
        await bot.TickAsync(ct);
        Assert.Single(gateways.Created);
    }

    [Fact]
    public async Task ClearingTheSettings_StopsTheBot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next(g => g.ReadyOnConnect = true);
        var bot = Service(services, gateways);

        await ConfigureBotAsync(services, "secret-token", "424242", ct);
        await bot.TickAsync(ct);
        Assert.NotNull(bot.ReadyGateway);

        await services.ConfigureAsync(s => s.DiscordBotTokenEncrypted = null, ct);
        await bot.TickAsync(ct);

        Assert.True(gateway.Disposed);
        Assert.Null(bot.ReadyGateway);
        Assert.Equal(DiscordBotState.NotConfigured, services.Status.Snapshot().State);
    }

    [Fact]
    public async Task AChangedToken_ReconnectsWithTheNewOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateways = new FakeGatewayFactory();
        var first = gateways.Next(g => g.ReadyOnConnect = true);
        var second = gateways.Next(g => g.ReadyOnConnect = true);
        var bot = Service(services, gateways);

        await ConfigureBotAsync(services, "old-token", "424242", ct);
        await bot.TickAsync(ct);

        await ConfigureBotAsync(services, "new-token", "424242", ct);
        await bot.TickAsync(ct);

        Assert.True(first.Disposed);
        Assert.Equal(["new-token"], second.Tokens);
        Assert.Same(second, bot.ReadyGateway);
    }

    [Fact]
    public async Task AFailedSignIn_IsReported_AndRetriedWithBackoff()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateways = new FakeGatewayFactory();
        gateways.Next(g => g.ConnectError = "Discord rejected the bot token.");
        var retry = gateways.Next(g => g.ReadyOnConnect = true);
        var bot = Service(services, gateways);

        await ConfigureBotAsync(services, "bad-token", "424242", ct);
        await bot.TickAsync(ct);

        var snapshot = services.Status.Snapshot();
        Assert.Equal(DiscordBotState.Failed, snapshot.State);
        Assert.Equal("Discord rejected the bot token.", snapshot.LastError);
        Assert.True(gateways.Created[0] is FakeGateway { Disposed: true });

        // Not yet: the retry is thirty seconds away.
        services.Clock.Advance(TimeSpan.FromSeconds(10));
        await bot.TickAsync(ct);
        Assert.Single(gateways.Created);

        services.Clock.Advance(TimeSpan.FromSeconds(25));
        await bot.TickAsync(ct);
        Assert.Equal(2, gateways.Created.Count);
        Assert.Same(retry, bot.ReadyGateway);
    }

    [Fact]
    public async Task AFatalDisconnect_StopsTheBot_UntilTheSettingsChange()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next(g => g.ReadyOnConnect = true);
        var replacement = gateways.Next(g => g.ReadyOnConnect = true);
        var bot = Service(services, gateways);

        await ConfigureBotAsync(services, "token", "424242", ct);
        await bot.TickAsync(ct);

        await gateway.RaiseDisconnectedAsync("Discord rejected the bot token.", fatal: true);

        Assert.True(gateway.Disposed);
        Assert.Equal(DiscordBotState.Failed, services.Status.Snapshot().State);

        // Same settings: no knocking on a locked door.
        services.Clock.Advance(TimeSpan.FromHours(1));
        await bot.TickAsync(ct);
        Assert.Single(gateways.Created);

        // New settings: a fresh start.
        await ConfigureBotAsync(services, "token-2", "424242", ct);
        await bot.TickAsync(ct);
        Assert.Same(replacement, bot.ReadyGateway);
    }

    [Fact]
    public async Task ThePromptForNewJoiners_AsksForMemberEvents_AndOnlyThen()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateways = new FakeGatewayFactory();
        var plain = gateways.Next(g => g.ReadyOnConnect = true);
        var withMembers = gateways.Next(g => g.ReadyOnConnect = true);
        var bot = Service(services, gateways);

        await ConfigureBotAsync(services, "token", "424242", ct);
        await bot.TickAsync(ct);
        Assert.False(plain.Options.MemberEvents);

        await services.ConfigureAsync(s => s.DiscordLinkPromptNewMembers = true, ct);
        await bot.TickAsync(ct);

        Assert.True(plain.Disposed);
        Assert.True(withMembers.Options.MemberEvents);
        Assert.Same(withMembers, bot.ReadyGateway);
    }

    [Fact]
    public async Task ARefusedMembersIntent_ReconnectsWithoutIt_InsteadOfStopping()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateways = new FakeGatewayFactory();
        var refused = gateways.Next(g => g.ReadyOnConnect = true);
        var without = gateways.Next(g => g.ReadyOnConnect = true);
        var bot = Service(services, gateways);

        await ConfigureBotAsync(services, "token", "424242", ct);
        await services.ConfigureAsync(s => s.DiscordLinkPromptNewMembers = true, ct);
        await bot.TickAsync(ct);
        Assert.True(refused.Options.MemberEvents);

        await refused.RaiseIntentsRefusedAsync();
        Assert.True(refused.Disposed);
        Assert.Contains("Server Members", services.Status.Snapshot().LastError, StringComparison.Ordinal);

        await bot.TickAsync(ct);

        Assert.False(without.Options.MemberEvents);
        Assert.Same(without, bot.ReadyGateway);
    }

    [Fact]
    public async Task AMemberJoiningTheServer_IsSentTheLinkPrompt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next(g => g.ReadyOnConnect = true);
        var bot = Service(services, gateways);

        await ConfigureBotAsync(services, "token", "424242", ct);
        await services.ConfigureAsync(s =>
        {
            s.DiscordLinkPromptNewMembers = true;
            s.PublicAddress = "https://modbot.example.com";
            s.DiscordOAuthClientId = "123";
            s.DiscordOAuthClientSecretEncrypted = services.Protector.Protect("secret");
        }, ct);
        await bot.TickAsync(ct);

        await gateway.RaiseMemberJoinedAsync(new DiscordMemberJoin("424242", "8080", "newcomer", false));
        await gateway.RaiseMemberJoinedAsync(new DiscordMemberJoin("some-other-server", "9090", "elsewhere", false));

        var dm = Assert.Single(gateway.DirectMessages);
        Assert.Equal("8080", dm.UserId);
    }

    [Fact]
    public async Task AnOrdinaryDrop_IsLeftToTheLibrary_ThenRebuiltIfItStaysDown()

    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next(g => g.ReadyOnConnect = true);
        var rebuilt = gateways.Next(g => g.ReadyOnConnect = true);
        var bot = Service(services, gateways);

        await ConfigureBotAsync(services, "token", "424242", ct);
        await bot.TickAsync(ct);

        await gateway.RaiseDisconnectedAsync("The gateway connection closed.");
        Assert.Equal(DiscordBotState.Disconnected, services.Status.Snapshot().State);
        Assert.Null(bot.ReadyGateway);

        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await bot.TickAsync(ct);
        Assert.False(gateway.Disposed);

        services.Clock.Advance(TimeSpan.FromMinutes(3));
        await bot.TickAsync(ct);
        Assert.True(gateway.Disposed);
        Assert.Same(rebuilt, bot.ReadyGateway);
    }

    [Fact]
    public async Task ACommand_IsAnsweredThroughTheHandler()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next(g => g.ReadyOnConnect = true);
        var bot = Service(services, gateways);

        await ConfigureBotAsync(services, "token", "424242", ct);
        await bot.TickAsync(ct);

        DiscordReply? answered = null;
        var call = new DiscordCommandCall(
            "999", "someone", DiscordCommands.Modbot,
            new Dictionary<string, string>(StringComparer.Ordinal),
            (reply, _) =>
            {
                answered = reply;
                return Task.CompletedTask;
            });

        await gateway.RaiseCommandAsync(call);

        Assert.Equal(DiscordCommandHandler.NotLinkedMessage, answered?.Text);
    }

    [Fact]
    public async Task CommandsThatCannotBeRegistered_LeaveTheBotConnected_AndSaySo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next(g => g.RegisterError = "The bot is not in that server.");
        var bot = Service(services, gateways);

        await ConfigureBotAsync(services, "token", "not-a-guild", ct);
        await bot.TickAsync(ct);
        await gateway.RaiseReadyAsync();

        var snapshot = services.Status.Snapshot();
        Assert.Equal(DiscordBotState.Connected, snapshot.State);
        Assert.Equal(0, snapshot.CommandsRegistered);
        Assert.Contains("not in that server", snapshot.LastError, StringComparison.Ordinal);
        Assert.NotNull(bot.ReadyGateway);
    }

    /// <summary>
    /// The bug this guards: Discord.Net resumes a dropped session with no Ready, and the bot used to
    /// sit on "reconnecting" with posting stopped for as long as the process lived.
    /// </summary>
    [Fact]
    public async Task AResumedSession_IsConnectedAgain_WithoutRegisteringCommandsTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next();
        var bot = Service(services, gateways);

        await ConfigureBotAsync(services, "token", "424242", ct);
        await bot.TickAsync(ct);
        await gateway.RaiseReadyAsync();
        Assert.Equal(1, gateway.RegisterCalls);

        await gateway.RaiseDisconnectedAsync("The gateway connection closed.");
        Assert.Equal(DiscordBotState.Disconnected, services.Status.Snapshot().State);
        Assert.Null(bot.ReadyGateway);

        services.Clock.Advance(TimeSpan.FromSeconds(20));
        await gateway.RaiseResumedAsync();

        var snapshot = services.Status.Snapshot();
        Assert.Equal(DiscordBotState.Connected, snapshot.State);
        Assert.Equal(DiscordCommands.All.Count, snapshot.CommandsRegistered);
        Assert.Null(snapshot.LastError);
        Assert.Same(gateway, bot.ReadyGateway);
        Assert.Equal(1, gateway.RegisterCalls);

        // Resumed, so not rebuilt later as if it had stayed down.
        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await bot.TickAsync(ct);
        Assert.False(gateway.Disposed);
        Assert.Single(gateways.Created);
    }

    [Fact]
    public async Task ASessionWhoseSocketReturnsButNeverBecomesReady_IsRebuilt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next(g => g.ReadyOnConnect = true);
        var rebuilt = gateways.Next(g => g.ReadyOnConnect = true);
        var bot = Service(services, gateways);

        await ConfigureBotAsync(services, "token", "424242", ct);
        await bot.TickAsync(ct);

        await gateway.RaiseDisconnectedAsync("The gateway connection closed.");
        gateway.State = DiscordGatewayState.Connecting;

        services.Clock.Advance(TimeSpan.FromMinutes(4));
        await bot.TickAsync(ct);

        Assert.True(gateway.Disposed);
        Assert.Same(rebuilt, bot.ReadyGateway);
    }

    [Fact]
    public async Task AReadyGatewayTheStatusMissed_IsReportedConnectedOnTheNextTick()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next();
        var bot = Service(services, gateways);

        await ConfigureBotAsync(services, "token", "424242", ct);
        await bot.TickAsync(ct);
        Assert.Equal(DiscordBotState.Connecting, services.Status.Snapshot().State);

        gateway.State = DiscordGatewayState.Ready;
        await bot.TickAsync(ct);

        Assert.Equal(DiscordBotState.Connected, services.Status.Snapshot().State);
    }
}
