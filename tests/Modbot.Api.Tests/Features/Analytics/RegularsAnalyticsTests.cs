using Modbot.Api.Features.Analytics.Regulars;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Analytics;

/// <summary>
/// The counting rules of the Regulars tab. The clock is fixed on Wednesday 14 October 2026, so the
/// week under way began on Monday 12 October and the latest whole weeks are 14 September to 11
/// October.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class RegularsAnalyticsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _db;

    public RegularsAnalyticsTests(PostgresFixture db) => _db = db;

    private static DateTimeOffset At(int month, int day, int hour = 20) => new(2026, month, day, hour, 0, 0, TimeSpan.Zero);

    private static Task JoinAsync(ReadSurfaceTestHost host, string who, DateTimeOffset at, string world = "wrld_a", string instance = "1", string? name = null)
        => host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, who, at, world, instance, name: name), TestContext.Current.CancellationToken);

    private async Task<RegularsAnalytics> ReadAsync(ReadSurfaceTestHost host, string query)
    {
        var ct = TestContext.Current.CancellationToken;
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);

        return await host.GetJsonAsync<RegularsAnalytics>($"/api/analytics/regulars?{query}", cookie, ct);
    }

    /// <summary>
    /// The week of Monday 31 August: five visitors. Two came back within the four weeks after it.
    /// One came back in the fifth week, which is too late; one visited twice in the same week, which
    /// is still one visitor; one was only seen leaving, which is not a visit.
    /// </summary>
    [Fact]
    public async Task ReturnRate_CountsAnyVisitToAnyGroupInstance_WithinFourWeeks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Now;

        // Back in the fourth week after, in another world: counts.
        await JoinAsync(host, "usr_a", At(9, 1));
        await JoinAsync(host, "usr_a", At(9, 28), world: "wrld_b", instance: "7");

        // Back in the fifth week after: too late.
        await JoinAsync(host, "usr_b", At(9, 2));
        await JoinAsync(host, "usr_b", At(10, 5));

        // Never back.
        await JoinAsync(host, "usr_c", At(9, 3));

        // Twice in one week is one visitor, and not a return.
        await JoinAsync(host, "usr_d", At(9, 1));
        await JoinAsync(host, "usr_d", At(9, 4));

        // Seen already there the week after: an arrival like any other.
        await JoinAsync(host, "usr_e", At(9, 2));
        await host.WriteFactAsync(PresenceFact(FactType.InstancePresenceObserved, "usr_e", At(9, 8), "wrld_a", "2"), ct);

        // Only seen leaving: not a visit.
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_f", At(9, 2), "wrld_a", "1"), ct);

        var page = await ReadAsync(host, "from=2026-08-31&to=2026-09-06");

        var week = Assert.Single(page.Weeks);
        Assert.Equal(new DateOnly(2026, 8, 31), week.WeekStart);
        Assert.Equal(5, week.Visitors);
        Assert.Equal(2, week.CameBack);
    }

    /// <summary>
    /// Sunday 23:59 and Monday 00:00 UTC are different weeks, so a person seen at both came back.
    /// </summary>
    [Fact]
    public async Task Weeks_RunMondayToSunday_InUtc()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Now;

        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_a", new DateTimeOffset(2026, 9, 6, 23, 59, 0, TimeSpan.Zero), "wrld_a", "1"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_a", new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero), "wrld_a", "1"), ct);

        var page = await ReadAsync(host, "from=2026-08-31&to=2026-09-13");

        Assert.Collection(
            page.Weeks,
            w =>
            {
                Assert.Equal(new DateOnly(2026, 8, 31), w.WeekStart);
                Assert.Equal(1, w.Visitors);
                Assert.Equal(1, w.CameBack);
            },
            w =>
            {
                Assert.Equal(new DateOnly(2026, 9, 7), w.WeekStart);
                Assert.Equal(1, w.Visitors);
                Assert.Equal(0, w.CameBack);
            });
    }

    /// <summary>
    /// A week whose four weeks after it are not over has no count of people who came back, however
    /// many have already: it would read low and then climb. Its visitors are still counted. The week
    /// of 7 September ends its four weeks on 11 October, so it is settled on the 12th and not before.
    /// </summary>
    [Fact]
    public async Task ReturnRate_IsLeftOut_UntilTheFourWeeksAfterAreOver()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Now;

        await JoinAsync(host, "usr_a", At(9, 8));
        await JoinAsync(host, "usr_a", At(9, 15));
        await JoinAsync(host, "usr_b", At(9, 16));

        var page = await ReadAsync(host, "from=2026-09-07&to=2026-09-20");

        Assert.Collection(
            page.Weeks,
            w =>
            {
                Assert.Equal(new DateOnly(2026, 9, 7), w.WeekStart);
                Assert.Equal(1, w.Visitors);
                Assert.Equal(1, w.CameBack);
            },
            w =>
            {
                Assert.Equal(new DateOnly(2026, 9, 14), w.WeekStart);
                Assert.Equal(2, w.Visitors);
                Assert.Null(w.CameBack);
            });
    }

    [Fact]
    public async Task Weeks_WithNobodyInThem_AreStillListed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Now;

        await JoinAsync(host, "usr_a", At(8, 11));

        var page = await ReadAsync(host, "from=2026-08-03&to=2026-08-23");

        Assert.Equal(3, page.Weeks.Count);
        Assert.Equal([0, 1, 0], page.Weeks.Select(w => w.Visitors).ToArray());
    }

    /// <summary>
    /// Regulars joined in at least three of the latest four whole weeks (14 September to 11 October).
    /// The week under way does not count, so a person seen in two of the four and this week is not
    /// one; the list is the most days first, and each one carries the latest name.
    /// </summary>
    [Fact]
    public async Task Regulars_JoinedInThreeOfTheLatestFourWholeWeeks_MostDaysFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Now;

        // Every week, two days in the first: four weeks, five days.
        await JoinAsync(host, "usr_every", At(9, 15), name: "Every");
        await JoinAsync(host, "usr_every", At(9, 17), name: "Every");
        await JoinAsync(host, "usr_every", At(9, 22), name: "Every");
        await JoinAsync(host, "usr_every", At(9, 29), name: "Every");
        await JoinAsync(host, "usr_every", At(10, 6), name: "Every");

        // Three weeks, three days.
        await JoinAsync(host, "usr_three", At(9, 16), name: "Three");
        await JoinAsync(host, "usr_three", At(9, 30), name: "Three");
        await JoinAsync(host, "usr_three", At(10, 7), name: "Three");

        // Seen in three weeks, but the third is the one under way, which does not count: two.
        await JoinAsync(host, "usr_late", At(9, 16));
        await JoinAsync(host, "usr_late", At(10, 7));
        await JoinAsync(host, "usr_late", At(10, 13));

        // Two weeks only.
        await JoinAsync(host, "usr_two", At(9, 16));
        await JoinAsync(host, "usr_two", At(9, 23));

        // Three weeks, but one of them before the latest four.
        await JoinAsync(host, "usr_old", At(9, 8));
        await JoinAsync(host, "usr_old", At(9, 16));
        await JoinAsync(host, "usr_old", At(9, 23));

        var page = await ReadAsync(host, "days=7");

        Assert.Equal(new DateOnly(2026, 9, 14), page.RegularsFrom);
        Assert.Equal(new DateOnly(2026, 10, 11), page.RegularsTo);
        Assert.Equal(2, page.RegularsTotal);
        Assert.Collection(
            page.Regulars,
            r =>
            {
                Assert.Equal("usr_every", r.UserId);
                Assert.Equal("Every", r.Name);
                Assert.Equal(4, r.Weeks);
                Assert.Equal(5, r.Days);
            },
            r =>
            {
                Assert.Equal("usr_three", r.UserId);
                Assert.Equal(3, r.Weeks);
                Assert.Equal(3, r.Days);
            });
    }

    [Theory]
    [InlineData(2026, 10, 14, 2026, 10, 12)]
    [InlineData(2026, 10, 12, 2026, 10, 12)]
    [InlineData(2026, 10, 18, 2026, 10, 12)]
    [InlineData(2026, 10, 19, 2026, 10, 19)]
    public void WeekStart_IsTheMonday(int y, int m, int d, int wy, int wm, int wd)
        => Assert.Equal(new DateOnly(wy, wm, wd), RegularsAnalyticsQuery.WeekStart(new DateOnly(y, m, d)));

    [Theory]
    [InlineData(2026, 10, 11, false)]
    [InlineData(2026, 10, 12, true)]
    [InlineData(2026, 10, 14, true)]
    public void AWeek_IsSettled_WhenTheFourWeeksAfterItAreOver(int y, int m, int d, bool settled)
        => Assert.Equal(settled, RegularsAnalyticsQuery.IsSettled(new DateOnly(2026, 9, 7), new DateOnly(y, m, d)));
}
