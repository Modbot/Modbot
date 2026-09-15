using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Alerts;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Alerts;

/// <summary>Unusual-activity alerts reaching the channel chosen for them, once.</summary>
[Collection(nameof(PostgresCollection))]
public class AlertPosterTests
{
    private const string Channel = "1234567890";

    private readonly PostgresFixture _db;

    public AlertPosterTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> AddAsync(TestServices services, Action<Alert>? change = null)
    {
        var at = services.Clock.UtcNow;

        var alert = new Alert
        {
            Watcher = AlertWatchers.VRChatJoins,
            At = at,
            WindowStart = at.AddHours(-1),
            WindowEnd = at,
            Now = 43,
            Normal = 4,
            Spread = 1,
            Score = 39,
            Sensitivity = AlertSensitivities.Normal,
            Figures = "{}",
            Link = "/?joinedFrom=x&joinedTo=y",
            Model = "some-model",
            Text = "Forty-three people joined in the last hour, against about four on a usual day.",
            DiscordChannelId = Channel,
        };

        change?.Invoke(alert);

        await using var context = services.Database.NewContext();
        context.Alerts.Add(alert);
        await context.SaveChangesAsync(Ct);
        return alert.Id;
    }

    private static async Task SetAddressAsync(TestServices services, string address)
    {
        await using var context = services.Database.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        settings.PublicAddress = address;
        await context.SaveChangesAsync(Ct);
    }

    private static async Task<AlertPostPass> RunAsync(TestServices services, FakeGateway gateway)
    {
        await using var context = services.Database.NewContext();
        return await new AlertPoster(context, services.Clock).RunOnceAsync(gateway, Ct);
    }

    private static async Task<Alert> ReadAsync(TestServices services, Guid id)
    {
        await using var context = services.Database.NewContext();
        return await context.Alerts.AsNoTracking().SingleAsync(a => a.Id == id, Ct);
    }

    [Fact]
    public async Task AnAlertIsPostedOnce_WithTheFigureAndWhatNormalIs()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();
        var id = await AddAsync(services);

        Assert.Equal(1, (await RunAsync(services, gateway)).Posted);
        Assert.Equal(0, (await RunAsync(services, gateway)).Posted);

        var (channel, embeds) = Assert.Single(gateway.Posts);
        Assert.Equal(Channel, channel);

        var card = Assert.Single(embeds);
        Assert.Equal("People joining the group", card.Title);
        Assert.Equal("Forty-three people joined in the last hour, against about four on a usual day.", card.Description);
        Assert.Equal("Unusual activity · some-model", card.Footer);
        Assert.Contains(card.Fields, f => f is { Name: "Now", Value: "43 joins" });
        Assert.Contains(card.Fields, f => f is { Name: "Normally", Value: "4" });

        Assert.Equal(services.Clock.UtcNow, (await ReadAsync(services, id)).DiscordPostedAt);
    }

    [Fact]
    public async Task AnAlertWithNoSentenceIsStillPosted()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();
        await AddAsync(services, a =>
        {
            a.Text = null;
            a.Model = null;
        });

        Assert.Equal(1, (await RunAsync(services, gateway)).Posted);

        var card = Assert.Single(Assert.Single(gateway.Posts).Embeds);
        Assert.Null(card.Description);
        Assert.Equal("Unusual activity", card.Footer);
        Assert.Contains(card.Fields, f => f.Name == "Now");
    }

    [Fact]
    public async Task TheCardLinksIntoModbotOnlyWhenThePublicAddressIsSet()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();

        await AddAsync(services);
        await RunAsync(services, gateway);
        Assert.Null(Assert.Single(Assert.Single(gateway.Posts).Embeds).Url);

        await SetAddressAsync(services, "https://modbot.example/");
        await AddAsync(services, a => a.Watcher = AlertWatchers.Flags);
        await RunAsync(services, gateway);

        var card = Assert.Single(gateway.Posts[1].Embeds);
        Assert.Equal("https://modbot.example/?joinedFrom=x&joinedTo=y", card.Url);
    }

    [Fact]
    public async Task AnAlertWithNoChannelIsNotPosted()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();

        await AddAsync(services, a => a.DiscordChannelId = null);
        await RunAsync(services, gateway);

        Assert.Empty(gateway.Posts);
    }

    [Fact]
    public async Task AChannelThatIsGoneIsRecordedAndNotTriedAgain()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();
        var id = await AddAsync(services);

        gateway.FailNextPost("Unknown Channel", permanent: true);
        await RunAsync(services, gateway);
        await RunAsync(services, gateway);

        Assert.Empty(gateway.Posts);
        Assert.Equal("Unknown Channel", (await ReadAsync(services, id)).DiscordError);
    }

    [Fact]
    public async Task AnAlertNotPostedWithinSixHoursIsGivenUpOn()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();
        var id = await AddAsync(services);

        services.Clock.Advance(AlertPoster.GiveUpAfter + TimeSpan.FromMinutes(1));
        await RunAsync(services, gateway);

        Assert.Empty(gateway.Posts);
        Assert.Equal("Not posted within six hours.", (await ReadAsync(services, id)).DiscordError);
    }
}
