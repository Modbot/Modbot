using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Cloud.Engine;
using Modbot.Cloud.Features.Retention;

namespace Modbot.Cloud.Tests;

[Collection(nameof(PostgresCollection))]
public class RetentionTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<string>> PartitionsAsync(EngineContext engine) =>
        await engine.Database.SqlQuery<string>($"""
            SELECT child.relname AS "Value" FROM pg_inherits
            JOIN pg_class child ON child.oid = pg_inherits.inhrelid
            WHERE child.relkind = 'r'
            ORDER BY 1
            """).ToListAsync(Ct);

    private static async Task<T> InScopeAsync<T>(CloudTestHost host, Func<IServiceProvider, Task<T>> run)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await run(scope.ServiceProvider);
    }

    [Fact]
    public void TheDefaultsAreNinetyDaysOfLinesAndAYearOfEvents()
    {
        var settings = new CloudSettings();

        Assert.Equal(90, settings.LogLineKeepDays);
        Assert.Equal(365, settings.LogEventKeepDays);
    }

    [Fact]
    public async Task PartitionsAreMadeAroundNowAndAcrossAMonthBoundary()
    {
        await using var host = await CloudTestHost.StartAsync(db, now: new DateTimeOffset(2026, 9, 30, 23, 59, 0, TimeSpan.Zero));
        await using var engine = db.NewEngineContext();

        Assert.Equal(
            ["log_event_2026_08", "log_event_2026_09", "log_event_2026_10", "log_event_2026_11",
             "log_line_2026_08", "log_line_2026_09", "log_line_2026_10", "log_line_2026_11"],
            await PartitionsAsync(engine));

        host.Time.Advance(TimeSpan.FromMinutes(2));
        var created = await InScopeAsync(host, s => s.GetRequiredService<PartitionMaintainer>().EnsureAsync(Ct));

        Assert.Equal(["log_line_2026_12", "log_event_2026_12"], created);

        // A batch received just after midnight lands in October's partition.
        var (_, bearer) = await host.RegisterAsync();
        using var response = await host.PostBatchAsync(bearer, host.Batch(CloudTestHost.Line("output_log_x.txt", 0, "x")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rows = await engine.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM log_line_2026_10").SingleAsync(Ct);
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task WholeMonthsPastTheWindowAreDroppedAndTheRestKept()
    {
        await using var host = await CloudTestHost.StartAsync(db, now: new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero));
        var (_, bearer) = await host.RegisterAsync();

        using (var may = await host.PostBatchAsync(bearer, host.Batch(
                   CloudTestHost.Line("output_log_may.txt", 0, "may", @event: new { type = "PlayerJoined", data = new { } }))))
        {
            Assert.Equal(HttpStatusCode.OK, may.StatusCode);
        }

        await using (var cloud = db.NewCloudContext())
        {
            cloud.Settings.Add(new CloudSettings { LogLineKeepDays = 90, LogEventKeepDays = 0 });
            await cloud.SaveChangesAsync(Ct);
        }

        // 1 September: May ended 93 days ago, June 62. Lines from May go; events are kept forever.
        host.Time.Set(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        await InScopeAsync(host, s => s.GetRequiredService<PartitionMaintainer>().EnsureAsync(Ct));
        var result = await InScopeAsync(host, s => s.GetRequiredService<RetentionPruner>().RunAsync(Ct));

        Assert.Equal(["log_line_2026_04", "log_line_2026_05"], result.DroppedPartitions);
        Assert.Equal(1, result.LogFilesRemoved);
        Assert.Equal(1, result.ClocksRemoved);

        await using var engine = db.NewEngineContext();
        var partitions = await PartitionsAsync(engine);
        Assert.DoesNotContain("log_line_2026_05", partitions);
        Assert.Contains("log_line_2026_06", partitions);
        Assert.Contains("log_event_2026_05", partitions);

        Assert.Equal(0, await engine.LogLines.CountAsync(Ct));
        Assert.Equal(1, await engine.LogEvents.CountAsync(Ct));

        // Totals hold no log content and stay.
        Assert.Equal(1, await engine.LineDayTotals.CountAsync(Ct));
        Assert.Equal(1, await engine.EventHourTotals.CountAsync(Ct));
    }

    [Fact]
    public async Task ZeroKeepsEverything()
    {
        await using var host = await CloudTestHost.StartAsync(db, now: new DateTimeOffset(2020, 1, 10, 0, 0, 0, TimeSpan.Zero));

        await using (var cloud = db.NewCloudContext())
        {
            cloud.Settings.Add(new CloudSettings { LogLineKeepDays = 0, LogEventKeepDays = 0 });
            await cloud.SaveChangesAsync(Ct);

            // Saved as zero, not quietly turned into a default.
            Assert.Equal(0, (await cloud.GetSettingsAsync(Ct)).LogLineKeepDays);
        }

        host.Time.Set(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var result = await InScopeAsync(host, s => s.GetRequiredService<RetentionPruner>().RunAsync(Ct));

        Assert.Empty(result.DroppedPartitions);
    }

    [Fact]
    public async Task APartitionNotMadeByCloudIsLeftAlone()
    {
        await using var host = await CloudTestHost.StartAsync(db, now: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        await using (var engine = db.NewEngineContext())
        {
            await engine.Database.ExecuteSqlRawAsync(
                "CREATE TABLE log_line_by_hand PARTITION OF log_line FOR VALUES FROM ('2000-01-01+00') TO ('2000-02-01+00')", Ct);
        }

        var result = await InScopeAsync(host, s => s.GetRequiredService<RetentionPruner>().RunAsync(Ct));

        Assert.DoesNotContain("log_line_by_hand", result.DroppedPartitions);

        await using var check = db.NewEngineContext();
        Assert.Contains("log_line_by_hand", await PartitionsAsync(check));
    }
}
