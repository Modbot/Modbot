using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Discord;
using Modbot.Discord.Bot;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Bot;

/// <summary>
/// Calendar design §16: the server's events are read from Discord at most once per five minutes a
/// server, and not at all with the bot offline.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordServerEventsTests(PostgresFixture db)
{
    private const string Guild = "424242";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateways = new FakeGatewayFactory();
        var events = new DiscordServerEvents(Service(services, gateways), services.Clock);

        Assert.Null(await events.ReadAsync(Guild, Ct));
        Assert.Empty(gateways.Created);
    }

    [Fact]
    public async Task AListIsKeptFiveMinutes_ThenReadAgain()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateways = new FakeGatewayFactory();
        var now = services.Clock.UtcNow;
        var gateway = gateways.Next(g =>
        {
            g.ReadyOnConnect = true;
            g.ServerEventList = [new DiscordServerEvent("1", "Movie night", now.AddDays(1), null, false, DiscordEventMakers.Bot, "Some bot")];
        });
        var bot = Service(services, gateways);
        await services.ConfigureAsync(s =>
        {
            s.DiscordBotTokenEncrypted = services.Protector.Protect("tok");
            s.DiscordGuildId = Guild;
        }, Ct);
        await bot.TickAsync(Ct);

        var events = new DiscordServerEvents(bot, services.Clock);

        var first = await events.ReadAsync(Guild, Ct);
        Assert.Equal("Movie night", Assert.Single(first!.Events).Name);

        gateway.ServerEventList = [];
        services.Clock.Advance(DiscordServerEvents.KeepFor - TimeSpan.FromSeconds(1));
        Assert.Single((await events.ReadAsync(Guild, Ct))!.Events);
        Assert.Equal(1, gateway.ServerEventReads);

        services.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty((await events.ReadAsync(Guild, Ct))!.Events);
        Assert.Equal(2, gateway.ServerEventReads);
    }

    [Fact]
    public async Task NoAnswer_IsKeptToo()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next(g =>
        {
            g.ReadyOnConnect = true;
            g.ServerEventList = null;
        });
        var bot = Service(services, gateways);
        await services.ConfigureAsync(s =>
        {
            s.DiscordBotTokenEncrypted = services.Protector.Protect("tok");
            s.DiscordGuildId = Guild;
        }, Ct);
        await bot.TickAsync(Ct);

        var events = new DiscordServerEvents(bot, services.Clock);

        Assert.Null(await events.ReadAsync(Guild, Ct));
        Assert.Null(await events.ReadAsync(Guild, Ct));
        Assert.Equal(1, gateway.ServerEventReads);
    }
}
