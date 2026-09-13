using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.TestSupport;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.RateLimiting;

/// <summary>
/// The restart guarantee against the database it actually depends on.
/// </summary>
/// <remarks>
/// The in-memory store proves the limiter's logic; this proves the state survives the thing that
/// destroys it in production — the process going away. Real PostgreSQL rather than a substitute,
/// for the reason the whole suite uses it: a guarantee about persistence tested against a fake
/// persistence layer is not a guarantee.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class DatabaseRateLimitStoreTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VRChatEndpoint Members =
        new(VRChatEndpointClass.GroupsMembers, "grp_persisted");

    [Fact]
    public async Task AColdStopSurvivesTheProcessThatCreatedIt()
    {
        var clock = new FakeClock();
        var options = new RateLimitOptions();

        await using var services = NewServices();
        var store = new DatabaseRateLimitStore(services.GetRequiredService<IServiceScopeFactory>());

        var first = new InProcessRateLimiter(store, clock, new TestDelayScheduler(clock), options);
        var lease = await first.AcquireAsync(Members, ct: Ct);
        await lease.ReportAsync(429, Ct);
        await lease.DisposeAsync();

        // A redeploy: same database, nothing else carried over.
        clock.Advance(TimeSpan.FromMinutes(4));
        var second = new InProcessRateLimiter(store, clock, new TestDelayScheduler(clock), options);

        var afterRestart = await second.AcquireAsync(Members, ct: Ct);
        await using (afterRestart)
        {
            Assert.False(afterRestart.IsAcquired);
            Assert.Equal(RateLimitDenialReason.ColdStop, afterRestart.Denial!.Reason);
            Assert.Equal(options.ColdStopBase - TimeSpan.FromMinutes(4), afterRestart.Denial.RetryAfter);
        }

        var health = (await second.DescribeAsync(Ct))
            .Single(b => b.Name == $"{VRChatEndpointClass.GroupsMembers}:grp_persisted");

        Assert.True(health.IsColdStopped);
        Assert.Equal(0.5, health.BudgetMultiplier, 6);
        Assert.Equal(1, health.RateLimitHits);
    }

    [Fact]
    public async Task TheRowIsUpsertedRatherThanDuplicated()
    {
        var clock = new FakeClock();
        await using var services = NewServices();
        var store = new DatabaseRateLimitStore(services.GetRequiredService<IServiceScopeFactory>());

        var endpoint = new VRChatEndpoint(VRChatEndpointClass.GroupsBans, "grp_upsert");
        var limiter = new InProcessRateLimiter(store, clock, new TestDelayScheduler(clock));

        for (var i = 0; i < 3; i++)
        {
            var lease = await limiter.AcquireAsync(endpoint, ct: Ct);
            await lease.ReportAsync(200, Ct);
            await lease.DisposeAsync();
        }

        await using var context = db.NewContext();
        var rows = await context.RateLimitBuckets
            .Where(b => b.EndpointClass == VRChatEndpointClass.GroupsBans)
            .ToListAsync(Ct);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.ResourceId == "grp_upsert");
        Assert.Contains(rows, r => r.ResourceId is null);
    }

    private ServiceProvider NewServices()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ModbotContext>(options => options.UseNpgsql(db.ConnectionString));

        return services.BuildServiceProvider();
    }
}
