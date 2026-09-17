using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Analytics.Group;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Analytics;

/// <summary>
/// The member count chart: every reading, thinned to what a chart can draw, with the facts
/// filling in the time before the first reading.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class GroupMemberCountTests
{
    private static readonly TimeSpan PollRate = TimeSpan.FromMinutes(5);

    private readonly PostgresFixture _db;

    public GroupMemberCountTests(PostgresFixture db) => _db = db;

    [Theory]
    [InlineData(24 * 60 * 60, 173)]
    [InlineData(7 * 24 * 60 * 60, 1210)]
    [InlineData(30 * 24 * 60 * 60, 5184)]
    [InlineData(60 * 60, 8)]
    [InlineData(0, 1)]
    public void TheStep_IsTheShortestWholeSecondThatKeepsARangeUnder500Points(int spanSeconds, int expected)
    {
        var step = GroupMemberCountQuery.StepSeconds(TimeSpan.FromSeconds(spanSeconds));

        Assert.Equal(expected, step);
        Assert.True(Math.Ceiling((double)spanSeconds / step) <= GroupMemberCountQuery.MaxPoints);
    }

    /// <summary>A day's step is shorter than the poll rate, so a day is every reading, untouched.</summary>
    [Fact]
    public async Task ADay_ServesEveryReading_WithBothCounts_InOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var now = host.Clock.UtcNow;

        // Written out of order, and one from before the window that must not appear.
        await AddReadingsAsync(host,
        [
            (now.AddMinutes(-10), 8124, 41),
            (now.AddMinutes(-20), 8123, 39),
            (now.AddMinutes(-15), 8123, 40),
            (now.AddHours(-25), 8000, 10),
        ], ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var series = await host.GetJsonAsync<GroupMemberCountSeries>("/api/analytics/group/member-count?range=day", cookie, ct);

        Assert.Equal(GroupMemberCountQuery.Day, series.Range);
        Assert.Equal(now.AddDays(-1), series.From);
        Assert.Equal(now, series.To);
        Assert.Equal(173, series.StepSeconds);

        Assert.Equal([now.AddMinutes(-20), now.AddMinutes(-15), now.AddMinutes(-10)], series.Points.Select(p => p.At));
        Assert.Equal([8123, 8123, 8124], series.Points.Select(p => p.Members));
        Assert.Equal([39, 40, 41], series.Points.Select(p => p.Online));
    }

    /// <summary>
    /// A week of five-minute readings is 2,016 rows. The chart gets at most 500 of them, each the
    /// last reading in its step, so every point is still a number VRChat reported.
    /// </summary>
    [Fact]
    public async Task AWeek_IsThinnedToTheLastReadingOfEachStep()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var now = host.Clock.UtcNow;
        var from = now.AddDays(-7);
        const int Readings = 7 * 24 * 12;

        await AddReadingsAsync(host,
            Enumerable.Range(0, Readings).Select(i => (from + i * PollRate, 10_000 + i, i % 50)),
            ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var series = await host.GetJsonAsync<GroupMemberCountSeries>("/api/analytics/group/member-count?range=week", cookie, ct);

        Assert.Equal(1210, series.StepSeconds);
        Assert.InRange(series.Points.Count, 400, GroupMemberCountQuery.MaxPoints);

        // Every point is a real reading, the points come in time order, and no step is served twice.
        var steps = new HashSet<long>();
        foreach (var point in series.Points)
        {
            var sinceFrom = point.At - from;
            Assert.Equal(0, sinceFrom.Ticks % PollRate.Ticks);
            Assert.Equal(10_000 + (int)(sinceFrom / PollRate), point.Members);
            Assert.True(steps.Add((long)Math.Floor(sinceFrom.TotalSeconds / series.StepSeconds)));
        }

        Assert.Equal(series.Points.OrderBy(p => p.At), series.Points);

        // The last reading in a step is the one kept: the final point is the final reading.
        Assert.Equal(from + (Readings - 1) * PollRate, series.Points[^1].At);
        Assert.Equal(10_000 + Readings - 1, series.Points[^1].Members);
    }

    /// <summary>
    /// Before the first stored reading, the facts stand in: the last observation of each day,
    /// each count carried forward from the last fact that stated it.
    /// </summary>
    [Fact]
    public async Task BeforeTheFirstReading_TheFactsFillIn_OnePointPerDay()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var now = host.Clock.UtcNow;

        await host.WriteFactAsync(GroupInfoBaseline(14208, now.AddDays(-10), online: 30), ct);
        await host.WriteFactAsync(GroupInfoChange(14208, 14230, now.AddDays(-5)), ct);
        await host.WriteFactAsync(GroupInfoOnlineChange(30, 35, now.AddDays(-5).AddHours(2)), ct);

        // A fact from after the first reading is not used: the readings are the record from then on.
        await host.WriteFactAsync(GroupInfoChange(14230, 14999, now.AddDays(-1)), ct);

        await AddReadingsAsync(host, [(now.AddDays(-2), 14240, 33), (now.AddDays(-1).AddHours(1), 14241, 34)], ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var series = await host.GetJsonAsync<GroupMemberCountSeries>("/api/analytics/group/member-count?range=month", cookie, ct);

        Assert.Equal(
            [now.AddDays(-10), now.AddDays(-5).AddHours(2), now.AddDays(-2), now.AddDays(-1).AddHours(1)],
            series.Points.Select(p => p.At));
        Assert.Equal([14208, 14230, 14240, 14241], series.Points.Select(p => p.Members));
        Assert.Equal([30, 35, 33, 34], series.Points.Select(p => p.Online));
    }

    [Fact]
    public async Task AllTime_StartsAtTheEarliestThingKnown_AndIsEmptyWhenNothingIs()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var now = host.Clock.UtcNow;
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);

        var empty = await host.GetJsonAsync<GroupMemberCountSeries>("/api/analytics/group/member-count?range=all", cookie, ct);
        Assert.Empty(empty.Points);
        Assert.Equal(now, empty.From);

        await host.WriteFactAsync(GroupInfoBaseline(9000, now.AddDays(-400)), ct);
        await AddReadingsAsync(host, [(now.AddDays(-3), 9500, 12)], ct);

        var all = await host.GetJsonAsync<GroupMemberCountSeries>("/api/analytics/group/member-count?range=all", cookie, ct);

        Assert.Equal(now.AddDays(-400), all.From);
        Assert.Equal(now, all.To);
        Assert.Equal([9000, 9500], all.Points.Select(p => p.Members));
    }

    [Fact]
    public async Task TheDefaultRangeIsAWeek_AndAnUnknownRangeIs400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);

        var series = await host.GetJsonAsync<GroupMemberCountSeries>("/api/analytics/group/member-count", cookie, ct);
        Assert.Equal(GroupMemberCountQuery.Week, series.Range);

        var bad = await host.GetAsync("/api/analytics/group/member-count?range=fortnight", cookie, ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    private static async Task AddReadingsAsync(
        ReadSurfaceTestHost host,
        IEnumerable<(DateTimeOffset At, int Members, int Online)> readings,
        CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        db.GroupMemberCounts.AddRange(readings.Select(r => new GroupMemberCount
        {
            GroupId = "grp_1",
            CountedAt = r.At,
            MemberCount = r.Members,
            OnlineMemberCount = r.Online,
        }));

        await db.SaveChangesAsync(ct);
    }
}
