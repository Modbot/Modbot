using Microsoft.Extensions.DependencyInjection;
using Modbot.Discord.Bot;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Bot;

/// <summary>
/// The online count asks Discord at most once per five minutes a server, and not at all with the
/// bot offline.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordOnlineCountTests
{
    private readonly PostgresFixture _db;

    public DiscordOnlineCountTests(PostgresFixture db) => _db = db;

    private static DiscordBotService Service(TestServices services, FakeGatewayFactory gateways)
        => new(
            services.Provider.GetRequiredService<IServiceScopeFactory>(),
            gateways,
            services.Clock,
            services.Status,
            new DiscordBotOptions(),
            (_, _) => Task.CompletedTask);

    [Fact]
    public async Task WithTheBotOffline_NothingIsAsked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateways = new FakeGatewayFactory();
        var online = new DiscordOnlineCount(Service(services, gateways), services.Clock);

        Assert.Null(await online.ReadAsync("424242", ct));
        Assert.Empty(gateways.Created);
    }

    [Fact]
    public async Task AnAnswerIsKeptFiveMinutes_ThenAskedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next(g =>
        {
            g.ReadyOnConnect = true;
            g.Online = 120;
        });
        var bot = Service(services, gateways);
        await services.ConfigureAsync(s =>
        {
            s.DiscordBotTokenEncrypted = services.Protector.Protect("tok");
            s.DiscordGuildId = "424242";
        }, ct);
        await bot.TickAsync(ct);

        var online = new DiscordOnlineCount(bot, services.Clock);

        Assert.Equal(120, await online.ReadAsync("424242", ct));

        gateway.Online = 130;
        services.Clock.Advance(DiscordOnlineCount.KeepFor - TimeSpan.FromSeconds(1));
        Assert.Equal(120, await online.ReadAsync("424242", ct));
        Assert.Equal(1, gateway.OnlineReads);

        services.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(130, await online.ReadAsync("424242", ct));
        Assert.Equal(2, gateway.OnlineReads);
    }

    [Fact]
    public async Task NoAnswer_IsKeptToo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next(g => g.ReadyOnConnect = true);
        var bot = Service(services, gateways);
        await services.ConfigureAsync(s =>
        {
            s.DiscordBotTokenEncrypted = services.Protector.Protect("tok");
            s.DiscordGuildId = "424242";
        }, ct);
        await bot.TickAsync(ct);

        var online = new DiscordOnlineCount(bot, services.Clock);

        Assert.Null(await online.ReadAsync("424242", ct));
        Assert.Null(await online.ReadAsync("424242", ct));
        Assert.Equal(1, gateway.OnlineReads);
    }
}
