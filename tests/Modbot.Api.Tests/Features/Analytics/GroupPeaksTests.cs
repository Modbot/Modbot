using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Analytics.Group;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Analytics;

/// <summary>
/// My Group's two peaks: the most members and the most online at once, each with the reading that
/// reached it.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class GroupPeaksTests
{
    private readonly PostgresFixture _db;

    public GroupPeaksTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task BothPeaks_ComeFromTheReadings_AndCarryTheirMoment()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var now = host.Clock.UtcNow;

        await ReadingsAsync(host, ct,
            (now.AddHours(-30), 8000, 12),
            (now.AddHours(-20), 8200, 61),
            (now.AddHours(-10), 8150, 40));

        var page = await PageAsync(host, "days=7", ct);

        Assert.NotNull(page.Peaks.Members);
        Assert.Equal(8200, page.Peaks.Members.Value);
        Assert.Equal(now.AddHours(-20), page.Peaks.Members.At, TimeSpan.FromSeconds(1));

        Assert.NotNull(page.Peaks.Online);
        Assert.Equal(61, page.Peaks.Online.Value);
        Assert.Equal(now.AddHours(-20), page.Peaks.Online.At, TimeSpan.FromSeconds(1));
    }

    /// <summary>The same number twice is ordinary; the earlier reading is the one named, always.</summary>
    [Fact]
    public async Task ATie_GoesToTheEarliestReading()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var now = host.Clock.UtcNow;

        await ReadingsAsync(host, ct,
            (now.AddHours(-5), 900, 30),
            (now.AddHours(-9), 900, 30),
            (now.AddHours(-1), 900, 30));

        var page = await PageAsync(host, "days=7", ct);

        Assert.Equal(now.AddHours(-9), page.Peaks.Members!.At, TimeSpan.FromSeconds(1));
        Assert.Equal(now.AddHours(-9), page.Peaks.Online!.At, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ARangeWithNoReadings_AnswersEmpty_RatherThanThrowing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var page = await PageAsync(host, "days=30", ct);

        Assert.Null(page.Peaks.Members);
        Assert.Null(page.Peaks.Online);
        Assert.Equal(0, page.Peaks.Coverage.Readings);
        Assert.Equal(30, page.Peaks.Coverage.WindowDays);
        Assert.False(page.Peaks.Coverage.Thin);
    }

    /// <summary>
    /// A month asked for and two days read: the peak is the highest Modbot saw, not the highest
    /// there was, and the response says so.
    /// </summary>
    [Fact]
    public async Task AWindowMostlyWithoutReadings_IsMarkedThin()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var now = host.Clock.UtcNow;

        await ReadingsAsync(host, ct, (now.AddHours(-1), 500, 20), (now.AddDays(-1), 490, 18));

        var page = await PageAsync(host, "days=30", ct);

        Assert.True(page.Peaks.Coverage.Thin);
        Assert.Equal(2, page.Peaks.Coverage.Readings);
        Assert.InRange(page.Peaks.Coverage.DaysWithReadings, 1, 2);

        // Marked, not hidden: the number is still there to read.
        Assert.Equal(500, page.Peaks.Members!.Value);
    }

    [Fact]
    public async Task AWindowReadThroughout_IsNotThin()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var now = host.Clock.UtcNow;

        await ReadingsAsync(host, ct,
            Enumerable.Range(0, 7).Select(i => (now.AddDays(-i), 1000 + i, 20 + i)).ToArray());

        var page = await PageAsync(host, "days=7", ct);

        Assert.False(page.Peaks.Coverage.Thin);
        Assert.Equal(7, page.Peaks.Coverage.DaysWithReadings);
    }

    private static async Task<GroupAnalytics> PageAsync(ReadSurfaceTestHost host, string query, CancellationToken ct)
    {
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        return await host.GetJsonAsync<GroupAnalytics>($"/api/analytics/group?{query}", cookie, ct);
    }

    private static async Task ReadingsAsync(
        ReadSurfaceTestHost host,
        CancellationToken ct,
        params (DateTimeOffset At, int Members, int Online)[] readings)
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
