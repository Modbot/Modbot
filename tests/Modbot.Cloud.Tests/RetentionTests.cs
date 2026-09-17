using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Cloud.Features.Retention;

namespace Modbot.Cloud.Tests;

[Collection(nameof(PostgresCollection))]
public class RetentionTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<RetentionResult> RunAsync(CloudTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RetentionPruner>().RunAsync(Ct);
    }

    [Fact]
    public void TheDefaultIsAYearOfEvents() => Assert.Equal(365, new CloudSettings().EventKeepDays);

    [Fact]
    public async Task EventsPastTheWindowAreDeletedAndTotalsKept()
    {
        await using var host = await CloudTestHost.StartAsync(db, now: new DateTimeOffset(2025, 9, 1, 12, 0, 0, TimeSpan.Zero));
        var (_, bearer) = await host.RegisterAsync();

        using (var old = await host.PostBatchAsync(bearer, host.Batch(CloudTestHost.Event("old", host.Time.GetUtcNow()))))
            Assert.Equal(HttpStatusCode.OK, old.StatusCode);

        host.Time.Set(new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero));
        using (var recent = await host.PostBatchAsync(bearer, host.Batch(CloudTestHost.Event("recent", host.Time.GetUtcNow()))))
            Assert.Equal(HttpStatusCode.OK, recent.StatusCode);

        var result = await RunAsync(host);

        Assert.Equal(1, result.EventsRemoved);

        await using var engine = db.NewEngineContext();
        Assert.Equal(["recent"], await engine.Events.Select(e => e.CompanionEventId).ToListAsync(Ct));
        Assert.Equal(2, await engine.EventDayTotals.CountAsync(Ct));
        Assert.Equal(2, await engine.EventHourTotals.CountAsync(Ct));
    }

    [Fact]
    public async Task ADeleteLargerThanOneSliceFinishes()
    {
        await using var host = await CloudTestHost.StartAsync(db, now: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var (id, _) = await host.RegisterAsync();

        await using (var engine = db.NewEngineContext())
        {
            await engine.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO companion_event (install_id, companion_event_id, received_at, sent_at, occurred_at, clock_adjustment_ms,
                                          type, subject_id, world_id, instance_id, companion_version, data)
                SELECT {id}, 'e' || n, {host.Time.GetUtcNow()}, {host.Time.GetUtcNow()}, {host.Time.GetUtcNow()}, 0,
                       'vrchat.instance.join', 'usr_1', 'wrld_1', '1', '2026.9.0', jsonb_build_object()
                FROM generate_series(1, {RetentionPruner.Slice + 5}) AS n
                """, Ct);
        }

        host.Time.Set(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var result = await RunAsync(host);

        Assert.Equal(RetentionPruner.Slice + 5, result.EventsRemoved);
    }

    [Fact]
    public async Task ZeroKeepsEverything()
    {
        await using var host = await CloudTestHost.StartAsync(db, now: new DateTimeOffset(2020, 1, 10, 0, 0, 0, TimeSpan.Zero));
        var (_, bearer) = await host.RegisterAsync();
        using (var old = await host.PostBatchAsync(bearer, host.Batch(CloudTestHost.Event("old", host.Time.GetUtcNow()))))
            Assert.Equal(HttpStatusCode.OK, old.StatusCode);

        await using (var cloud = db.NewCloudContext())
        {
            cloud.Settings.Add(new CloudSettings { EventKeepDays = 0 });
            await cloud.SaveChangesAsync(Ct);

            // Saved as zero, not quietly turned into a default.
            Assert.Equal(0, (await cloud.GetSettingsAsync(Ct)).EventKeepDays);
        }

        host.Time.Set(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var result = await RunAsync(host);

        Assert.Equal(0, result.EventsRemoved);
    }
}
