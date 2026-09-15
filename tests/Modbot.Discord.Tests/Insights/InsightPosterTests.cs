using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Insights;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Insights;

/// <summary>Scheduled insights reaching the channel their schedule named, once.</summary>
[Collection(nameof(PostgresCollection))]
public class InsightPosterTests
{
    private const string Channel = "1234567890";

    private readonly PostgresFixture _db;

    public InsightPosterTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> AddAsync(TestServices services, Action<Insight>? change = null)
    {
        var insight = new Insight
        {
            Kind = InsightKinds.Group,
            FirstDay = new DateOnly(2026, 9, 6),
            LastDay = new DateOnly(2026, 9, 12),
            CreatedAt = services.Clock.UtcNow,
            StartedBy = InsightKinds.StartedBySchedule,
            Model = "some-model",
            Text = "Joins were up on the week before.",
            DiscordChannelId = Channel,
        };

        change?.Invoke(insight);

        await using var context = services.Database.NewContext();
        context.Insights.Add(insight);
        await context.SaveChangesAsync(Ct);
        return insight.Id;
    }

    private static async Task<InsightPostPass> RunAsync(TestServices services, FakeGateway gateway)
    {
        await using var context = services.Database.NewContext();
        return await new InsightPoster(context, services.Clock).RunOnceAsync(gateway, Ct);
    }

    private static async Task<Insight> ReadAsync(TestServices services, Guid id)
    {
        await using var context = services.Database.NewContext();
        return await context.Insights.AsNoTracking().SingleAsync(i => i.Id == id, Ct);
    }

    [Fact]
    public async Task AnInsightIsPostedOnce_WithItsKindAndDaysInTheTitle()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();
        var id = await AddAsync(services);

        Assert.Equal(1, (await RunAsync(services, gateway)).Posted);
        Assert.Equal(0, (await RunAsync(services, gateway)).Posted);

        var (channel, embeds) = Assert.Single(gateway.Posts);
        Assert.Equal(Channel, channel);

        var card = Assert.Single(embeds);
        Assert.Equal("Group: 6 September to 12 September 2026", card.Title);
        Assert.Equal("Joins were up on the week before.", card.Description);
        Assert.Equal("AI insight · some-model", card.Footer);

        Assert.Equal(services.Clock.UtcNow, (await ReadAsync(services, id)).DiscordPostedAt);
    }

    [Fact]
    public async Task InsightsWithNoChannelOrNoTextAreNotPosted()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();

        await AddAsync(services, i => i.DiscordChannelId = null);
        await AddAsync(services, i =>
        {
            i.Text = null;
            i.Error = "No credit left";
        });

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
    public async Task APassingProblemIsTriedAgainNextPass()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();
        var id = await AddAsync(services);

        gateway.FailNextPost("Discord is having a moment");
        var failed = await RunAsync(services, gateway);

        Assert.Equal("Discord is having a moment", failed.Error);
        Assert.Null((await ReadAsync(services, id)).DiscordError);

        Assert.Equal(1, (await RunAsync(services, gateway)).Posted);
    }

    [Fact]
    public async Task AnInsightNotPostedWithinADayIsGivenUpOn()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var gateway = new FakeGateway();
        var id = await AddAsync(services);

        services.Clock.Advance(InsightPoster.GiveUpAfter + TimeSpan.FromMinutes(1));
        await RunAsync(services, gateway);

        Assert.Empty(gateway.Posts);
        Assert.Equal("Not posted within a day.", (await ReadAsync(services, id)).DiscordError);
    }
}
