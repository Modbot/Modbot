using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Activity;
using Modbot.Api.Features.Analytics.Instances;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Api.Tests.Features.Places;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Analytics;

/// <summary>
/// The Instances page's peaks, built from VRChat's own head counts rather than from the companion's
/// presence reports — so they cover instances no moderator was standing in.
/// </summary>
/// <remarks>
/// Written straight into <c>instance_head_count</c>, the way <c>HeadCounts.Record</c> writes them:
/// a row only where the number changed. These tests are about what the read makes of those rows,
/// not about how they got there, and the sync that writes them needs a live VRChat gate.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class InstancePeaksTests
{
    private const string World = "wrld_peak";
    private const string Group = "grp_1";

    private readonly PostgresFixture _db;

    public InstancePeaksTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task APeak_IsFound_AndCarriesWhenItHappened()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await ManagedGroupAsync(host, ct);

        var opened = host.Clock.UtcNow.AddHours(-6);
        var instance = await PlacesFixtures.InstanceAsync(host, World, "1", opened, host.Clock.UtcNow, null, ct);

        await CountsAsync(host, instance.Id, ct,
            (opened, 3),
            (opened.AddMinutes(30), 9),
            (opened.AddMinutes(60), 5));

        var page = await PageAsync(host, "days=7", ct);

        Assert.NotNull(page.Peaks.MostPeopleAtOnce);
        Assert.Equal(9, page.Peaks.MostPeopleAtOnce.Value);
        Assert.Equal(opened.AddMinutes(30), page.Peaks.MostPeopleAtOnce.At, TimeSpan.FromSeconds(1));

        Assert.NotNull(page.Peaks.BusiestInstance);
        Assert.Equal(instance.Id, page.Peaks.BusiestInstance.Id);
        Assert.Equal(9, page.Peaks.BusiestInstance.People);
        Assert.Equal(opened.AddMinutes(30), page.Peaks.BusiestInstance.At, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Two instances that both reached the same height. Without a rule the page would name whichever
    /// the database returned first and move between two loads with no new data behind it.
    /// </summary>
    [Fact]
    public async Task ATie_GoesToTheEarliestMoment()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await ManagedGroupAsync(host, ct);

        var early = host.Clock.UtcNow.AddHours(-8);
        var late = host.Clock.UtcNow.AddHours(-4);

        var first = await PlacesFixtures.InstanceAsync(host, World, "1", early, early.AddHours(1), early.AddHours(1), ct);
        var second = await PlacesFixtures.InstanceAsync(host, World, "2", late, late.AddHours(1), late.AddHours(1), ct);

        // Each instance on its own reached seven, and never at the same time as the other.
        await CountsAsync(host, first.Id, ct, (early, 7), (early.AddMinutes(50), 0));
        await CountsAsync(host, second.Id, ct, (late, 7), (late.AddMinutes(50), 0));

        var page = await PageAsync(host, "days=7", ct);

        Assert.Equal(7, page.Peaks.MostPeopleAtOnce!.Value);
        Assert.Equal(early, page.Peaks.MostPeopleAtOnce.At, TimeSpan.FromSeconds(1));
        Assert.Equal(first.Id, page.Peaks.BusiestInstance!.Id);
    }

    /// <summary>
    /// "Nothing was recorded" and "the peak was nought" are different statements, and a range with
    /// nothing in it has only the first to make. It must not be an error either.
    /// </summary>
    [Fact]
    public async Task ARangeWithNothingInIt_AnswersEmpty_RatherThanThrowing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await ManagedGroupAsync(host, ct);

        var page = await PageAsync(host, "days=7", ct);

        Assert.Null(page.Peaks.MostPeopleAtOnce);
        Assert.Null(page.Peaks.MostInstancesAtOnce);
        Assert.Null(page.Peaks.BusiestDay);
        Assert.Null(page.Peaks.BusiestHour);
        Assert.Null(page.Peaks.BusiestInstance);
        Assert.Empty(page.Peaks.MostPeopleAtOncePerDay);
        Assert.Equal(7, page.Peaks.Coverage.WindowDays);
        Assert.Equal(0, page.Peaks.Coverage.InstancesOpen);
        Assert.False(page.Peaks.Coverage.Thin);
    }

    /// <summary>
    /// The failure the whole coverage block exists for: an instance open all evening that Modbot
    /// only started counting at the end of it. The peak is real and it is not the evening's.
    /// </summary>
    [Fact]
    public async Task CountingOnlyTheEndOfAnEvening_IsMarkedThin()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await ManagedGroupAsync(host, ct);

        var opened = host.Clock.UtcNow.AddHours(-10);
        var closed = host.Clock.UtcNow.AddHours(-1);
        var instance = await PlacesFixtures.InstanceAsync(host, World, "1", opened, closed, closed, ct);

        // Nine hours open; the first head count lands in the last half hour of it.
        await CountsAsync(host, instance.Id, ct, (closed.AddMinutes(-30), 12));

        var page = await PageAsync(host, "days=7", ct);

        Assert.True(page.Peaks.Coverage.Thin);
        Assert.Equal(1, page.Peaks.Coverage.InstancesOpen);
        Assert.Equal(1, page.Peaks.Coverage.InstancesCounted);
        Assert.True(page.Peaks.Coverage.MinutesCounted < page.Peaks.Coverage.MinutesInstancesWereOpen / 2);

        // The peak itself is still reported. It is marked, not hidden.
        Assert.Equal(12, page.Peaks.MostPeopleAtOnce!.Value);
    }

    /// <summary>An instance nobody ever read is open time with nothing counted against it.</summary>
    [Fact]
    public async Task AnInstanceNeverRead_CountsAsOpenAndNotCounted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await ManagedGroupAsync(host, ct);

        var opened = host.Clock.UtcNow.AddHours(-3);
        await PlacesFixtures.InstanceAsync(host, World, "1", opened, host.Clock.UtcNow, null, ct);

        var page = await PageAsync(host, "days=7", ct);

        Assert.Equal(1, page.Peaks.Coverage.InstancesOpen);
        Assert.Equal(0, page.Peaks.Coverage.InstancesCounted);
        Assert.Equal(0m, page.Peaks.Coverage.MinutesCounted);
        Assert.Null(page.Peaks.MostPeopleAtOnce);
    }

    /// <summary>
    /// People-minutes and not the peak decide the busiest day, so a long steady evening outweighs a
    /// short rush on another day.
    /// </summary>
    [Fact]
    public async Task TheBusiestDay_IsPeopleTime_AndNotTheHighestPeak()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await ManagedGroupAsync(host, ct);

        // Midday on two consecutive days, so neither evening crosses midnight.
        var rushDay = DateOnly.FromDateTime(host.Clock.UtcNow.UtcDateTime).AddDays(-3);
        var steadyDay = rushDay.AddDays(1);

        var rushAt = new DateTimeOffset(rushDay.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
        var steadyAt = new DateTimeOffset(steadyDay.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);

        var rush = await PlacesFixtures.InstanceAsync(host, World, "1", rushAt, rushAt.AddMinutes(20), rushAt.AddMinutes(20), ct);
        var steady = await PlacesFixtures.InstanceAsync(host, World, "2", steadyAt, steadyAt.AddHours(5), steadyAt.AddHours(5), ct);

        await CountsAsync(host, rush.Id, ct, (rushAt, 40), (rushAt.AddMinutes(10), 0));
        await CountsAsync(host, steady.Id, ct, (steadyAt, 10), (steadyAt.AddHours(4), 0));

        var page = await PageAsync(host, "days=7", ct);

        Assert.Equal(40, page.Peaks.MostPeopleAtOnce!.Value);
        Assert.NotNull(page.Peaks.BusiestDay);
        Assert.Equal(steadyDay, page.Peaks.BusiestDay.Day);
        Assert.Equal(10, page.Peaks.BusiestDay.MostPeopleAtOnce);

        Assert.NotNull(page.Peaks.BusiestHour);
        Assert.Equal(steadyAt, page.Peaks.BusiestHour.StartedAt, TimeSpan.FromSeconds(1));
    }

    /// <summary>The line the page draws: a staircase of totals, each a number VRChat reported.</summary>
    [Fact]
    public async Task TheActivitySeries_AddsUpEveryInstanceAtEachMoment()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await ManagedGroupAsync(host, ct);

        var at = host.Clock.UtcNow.AddHours(-2);

        var first = await PlacesFixtures.InstanceAsync(host, World, "1", at, host.Clock.UtcNow, null, ct);
        var second = await PlacesFixtures.InstanceAsync(host, World, "2", at, host.Clock.UtcNow, null, ct);

        await CountsAsync(host, first.Id, ct, (at, 4));
        await CountsAsync(host, second.Id, ct, (at.AddMinutes(10), 6));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var series = await host.GetJsonAsync<InstanceActivitySeries>("/api/analytics/instances/activity?range=day", cookie, ct);

        Assert.Equal("day", series.Range);
        Assert.Equal(2, series.Points.Count);
        Assert.Equal(4, series.Points[0].People);
        Assert.Equal(1, series.Points[0].Instances);
        Assert.Equal(10, series.Points[1].People);
        Assert.Equal(2, series.Points[1].Instances);
    }

    /// <summary>
    /// A week is cut into half-hour steps. The evening's peak of six lasted three minutes in the
    /// middle of one, and the old thinning kept only each step's last reading, so the chart topped
    /// out at four under a tile that said six. Each step now keeps its highest reading and its last.
    /// </summary>
    [Fact]
    public async Task TheActivitySeries_KeepsEachStepsHighestReading_AndItsLast()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await ManagedGroupAsync(host, ct);

        host.Clock.UtcNow = new DateTimeOffset(2026, 9, 26, 12, 7, 0, TimeSpan.Zero);

        // 03:00 to 03:30 UTC is one half-hour step, counted from the epoch.
        var opened = new DateTimeOffset(2026, 9, 26, 3, 2, 0, TimeSpan.Zero);
        var closed = opened.AddMinutes(20);
        var instance = await PlacesFixtures.InstanceAsync(host, World, "1", opened, closed, closed, ct);

        await CountsAsync(host, instance.Id, ct,
            (opened, 1),
            (opened.AddMinutes(10), 6),
            (opened.AddMinutes(13), 4));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var series = await host.GetJsonAsync<InstanceActivitySeries>("/api/analytics/instances/activity?range=week", cookie, ct);
        var page = await PageAsync(host, "days=7", ct);

        Assert.Equal(1800, series.StepSeconds);

        // The peak is on the chart, at its own moment, and it is the peak the tile prints.
        var peak = Assert.Single(series.Points, p => p.People == 6);
        Assert.Equal(opened.AddMinutes(10), peak.At, TimeSpan.FromSeconds(1));
        Assert.Equal(page.Peaks.MostPeopleAtOnce!.Value, series.Points.Max(p => p.People));

        // And the step's last reading, so the line comes back down: the instance ended at 03:22.
        Assert.Equal(0, series.Points[^1].People);
        Assert.Equal(closed, series.Points[^1].At, TimeSpan.FromSeconds(1));

        // Nothing kept from the step but its highest (also its most instances) and its last.
        Assert.Equal(2, series.Points.Count);
    }

    /// <summary>
    /// The steps used to start at "now minus the range", so every reload cut the same readings
    /// differently. Loaded again seven minutes later, with nothing new counted, the chart is the same.
    /// </summary>
    [Fact]
    public async Task TheActivitySeries_DrawsTheSameStepsOnEveryLoad()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await ManagedGroupAsync(host, ct);

        var now = new DateTimeOffset(2026, 9, 26, 12, 7, 0, TimeSpan.Zero);
        host.Clock.UtcNow = now;

        var opened = now.AddDays(-2);
        var closed = opened.AddHours(5);
        var instance = await PlacesFixtures.InstanceAsync(host, World, "1", opened, closed, closed, ct);

        // A reading every minute for five hours, going up and down.
        await CountsAsync(host, instance.Id, ct,
            [.. Enumerable.Range(0, 300).Select(i => (opened.AddMinutes(i), 3 + (i * 7 % 11)))]);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var first = await host.GetJsonAsync<InstanceActivitySeries>("/api/analytics/instances/activity?range=week", cookie, ct);

        host.Clock.UtcNow = now.AddMinutes(7);
        var second = await host.GetJsonAsync<InstanceActivitySeries>("/api/analytics/instances/activity?range=week", cookie, ct);

        Assert.NotEmpty(first.Points);
        Assert.Equal(
            first.Points.Select(p => (p.At, p.People, p.Instances)),
            second.Points.Select(p => (p.At, p.People, p.Instances)));

        // Every point is in a step of its own or shares it with at most two others.
        Assert.All(
            first.Points.GroupBy(p => ReadingRange.StepOf(p.At, first.StepSeconds)),
            g => Assert.InRange(g.Count(), 1, 3));

        // And the highest reading of the evening is on the chart.
        Assert.Equal(13, first.Points.Max(p => p.People));
    }

    [Fact]
    public async Task TheActivitySeries_RefusesARangeItDoesNotKnow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var response = await host.GetAsync("/api/analytics/instances/activity?range=fortnight", cookie, ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>A window with no readings answers an empty series rather than failing.</summary>
    [Fact]
    public async Task TheActivitySeries_AnswersEmptyWhenNothingWasCounted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await ManagedGroupAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var series = await host.GetJsonAsync<InstanceActivitySeries>("/api/analytics/instances/activity?range=week", cookie, ct);

        Assert.Empty(series.Points);
    }

    private static async Task<InstancesAnalytics> PageAsync(ReadSurfaceTestHost host, string query, CancellationToken ct)
    {
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        return await host.GetJsonAsync<InstancesAnalytics>($"/api/analytics/instances?{query}", cookie, ct);
    }

    /// <summary>The group whose instances are the group's own. Without it nothing is counted.</summary>
    private static async Task ManagedGroupAsync(ReadSurfaceTestHost host, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await db.GetSettingsAsync(ct);
        settings.ManagedGroupId = Group;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Head counts, one row per change, the way <c>HeadCounts.Record</c> writes them.</summary>
    private static async Task CountsAsync(
        ReadSurfaceTestHost host,
        Guid instanceId,
        CancellationToken ct,
        params (DateTimeOffset At, int Count)[] counts)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        foreach (var (at, count) in counts)
        {
            db.InstanceHeadCounts.Add(new InstanceHeadCount
            {
                InstanceId = instanceId,
                CountedAt = at,
                HeadCount = count,
                UserCount = count,
                MemberCount = count,
                Source = HeadCounts.FromPage,
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
