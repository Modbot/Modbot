using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Discord.Bot;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.Interactions;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Bot;

/// <summary>
/// Which commands are on the server follows the <c>discord_commands</c> setting (Discord commands
/// design §3.8): a command that is off is not registered, the bot registers again only when the set
/// of names changes, and a missing name takes its default.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CommandSwitchRegistrationTests
{
    private readonly PostgresFixture _db;

    public CommandSwitchRegistrationTests(PostgresFixture db) => _db = db;

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

    private static async Task<(DiscordBotService Bot, FakeGateway Gateway)> ConnectedAsync(
        TestServices services, CancellationToken ct, Action<Settings>? more = null)
    {
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next();
        var bot = Service(services, gateways);

        await services.ConfigureAsync(s =>
        {
            s.DiscordBotTokenEncrypted = services.Protector.Protect("token");
            s.DiscordGuildId = "424242";
            more?.Invoke(s);
        }, ct);

        await bot.TickAsync(ct);
        await gateway.RaiseReadyAsync();
        return (bot, gateway);
    }

    private static DiscordCommandCall Call(string discordUserId, string command)
        => new(discordUserId, "someone", command, new Dictionary<string, string>(), (_, _) => Task.CompletedTask);

    // ── Registration ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithNothingStored_TheDefaultsAreRegistered()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);

        var (_, gateway) = await ConnectedAsync(services, ct);

        var names = gateway.RegisteredCommands.Select(c => c.Name).ToList();
        Assert.Contains("lookup", names);
        Assert.Contains("recent", names);
        Assert.Contains("Look up in Modbot", names);
        Assert.DoesNotContain("me", names);
        Assert.Equal(DiscordCommands.For(DiscordCommandSwitches.Empty).Count, services.Status.Snapshot().CommandsRegistered);
    }

    [Fact]
    public async Task ACommandSwitchedOffBeforeSignIn_IsNeverRegistered()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);

        var (_, gateway) = await ConnectedAsync(services, ct, s =>
        {
            s.SwitchCommand("recent", false);
            s.SwitchCommand("Add a note", false);
        });

        Assert.DoesNotContain(gateway.RegisteredCommands, c => c.Name == "recent");
        Assert.DoesNotContain(gateway.RegisteredCommands, c => c.Name == "Add a note");
        Assert.Contains(gateway.RegisteredCommands, c => c.Name == "lookup");
        Assert.Equal(1, gateway.RegisterCalls);
    }

    [Fact]
    public async Task SwitchingACommandOffAndOn_RegistersAgain_EachTime_WithoutReconnecting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var (bot, gateway) = await ConnectedAsync(services, ct);
        Assert.Equal(1, gateway.RegisterCalls);

        await services.ConfigureAsync(s => s.SwitchCommand("recent", false), ct);
        await bot.TickAsync(ct);

        Assert.Equal(2, gateway.RegisterCalls);
        Assert.DoesNotContain(gateway.RegisteredCommands, c => c.Name == "recent");

        await services.ConfigureAsync(s => s.SwitchCommand("recent", true), ct);
        await bot.TickAsync(ct);

        Assert.Equal(3, gateway.RegisterCalls);
        Assert.Contains(gateway.RegisteredCommands, c => c.Name == "recent");
        Assert.Equal(DiscordCommands.For(DiscordCommandSwitches.Empty).Count, services.Status.Snapshot().CommandsRegistered);
    }

    /// <summary>It compares the set of names, not the setting: saving something that changes no command does nothing.</summary>
    [Fact]
    public async Task ASettingThatLeavesTheSameCommandsOn_RegistersNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var (bot, gateway) = await ConnectedAsync(services, ct);

        // A stored choice equal to the default, and a name that is not a command.
        await services.ConfigureAsync(s => s.DiscordCommands = "{\"lookup\":true,\"me\":false,\"nonsense\":true}", ct);
        await bot.TickAsync(ct);
        await bot.TickAsync(ct);

        Assert.Equal(1, gateway.RegisterCalls);
    }

    [Fact]
    public async Task ASettingThatIsNotAnObject_FallsBackToTheDefaults()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);

        var (bot, gateway) = await ConnectedAsync(services, ct, s => s.DiscordCommands = "[1,2]");

        Assert.Equal(DiscordCommands.For(DiscordCommandSwitches.Empty).Select(c => c.Name), gateway.RegisteredCommands.Select(c => c.Name));

        await bot.TickAsync(ct);
        Assert.Equal(1, gateway.RegisterCalls);
    }

    // ── A run that arrives before Discord drops the command ────────────────────────────────

    [Fact]
    public async Task ASlashCommandThatIsSwitchedOff_IsTold_AndRecordedAsOff()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.ConfigureAsync(s => s.SwitchCommand("recent", false), ct);

        using var scope = services.Scope();
        var reply = await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>()
            .RunAsync(Call("999", DiscordCommands.Recent), ct);

        Assert.Equal("/recent is turned off on this server.", reply?.Text);

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordCommandRun, ct));
        Assert.Equal("off", JsonDocument.Parse(fact.Data).RootElement.GetProperty("outcome").GetString());
    }

    /// <summary>The switch holds for every command through <c>RunAsync</c>, not only <c>/me</c>, which keeps its own wording.</summary>
    [Theory]
    [InlineData("lookup", "/lookup is turned off on this server.")]
    [InlineData("modbot", "/modbot is turned off on this server.")]
    [InlineData("help", "/help is turned off on this server.")]
    [InlineData("verify", "/verify is turned off on this server.")]
    [InlineData("note", "/note is turned off on this server.")]
    [InlineData("watch", "/watch is turned off on this server.")]
    [InlineData("live", "/live is turned off on this server.")]
    [InlineData("ban", "/ban is turned off on this server.")]
    [InlineData("kick", "/kick is turned off on this server.")]
    [InlineData("gate", "/gate is turned off on this server.")]
    [InlineData("events", "/events is turned off on this server.")]
    [InlineData("me", MeCommand.OffMessage)]
    public async Task EveryCommandThatIsSwitchedOff_IsRefusedThroughRunAsync(string command, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.ConfigureAsync(s => s.SwitchCommand(command, false), ct);

        using var scope = services.Scope();
        var reply = await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>()
            .RunAsync(Call("999", command), ct);

        Assert.Equal(expected, reply?.Text);
    }

    [Fact]
    public async Task ACommandThatIsOn_RunsAsBefore()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);

        using var scope = services.Scope();
        var reply = await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>()
            .RunAsync(Call("999", DiscordCommands.Recent), ct);

        Assert.Equal(DiscordCommandHandler.NotLinkedMessage, reply?.Text);
    }

    [Fact]
    public async Task AMenuThatIsSwitchedOff_IsTold()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.ConfigureAsync(s => s.SwitchCommand(StaffMenus.AddNote, false), ct);

        using var scope = services.Scope();
        var call = new DiscordCommandCall(
            "999", "someone", StaffMenus.AddNote, new Dictionary<string, string>(), (_, _) => Task.CompletedTask)
        {
            Kind = DiscordCommandKind.User,
            TargetUser = new DiscordTargetUser("555", "target", IsBot: false),
        };

        var reply = await scope.ServiceProvider.GetRequiredService<StaffInteractionHandler>()
            .HandleMenuAsync(call, gateway: null, ct);

        Assert.Equal("\"Add a note\" is turned off on this server.", reply?.Text);
    }

    // ── /help names what is registered ─────────────────────────────────────────────────────

    [Fact]
    public async Task Help_LeavesOutACommandThatIsSwitchedOff()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.ViewProfile | ModbotPermissions.ViewAuditLog, ct: ct);

        using (var scope = services.Scope())
        {
            var before = await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>()
                .HandleAsync(Call("100", DiscordCommands.Help), ct);
            Assert.Contains("`/recent count:`", before.Text, StringComparison.Ordinal);
        }

        await services.ConfigureAsync(s => s.SwitchCommand("recent", false), ct);

        using var again = services.Scope();
        var after = await again.ServiceProvider.GetRequiredService<DiscordCommandHandler>()
            .HandleAsync(Call("100", DiscordCommands.Help), ct);

        Assert.DoesNotContain("/recent", after.Text, StringComparison.Ordinal);
        Assert.Contains("`/lookup", after.Text, StringComparison.Ordinal);
    }
}
