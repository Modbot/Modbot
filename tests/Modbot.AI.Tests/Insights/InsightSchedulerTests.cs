using Microsoft.EntityFrameworkCore;
using Modbot.AI.Insights;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.AI.Tests.Insights;

/// <summary>Scheduled insights against a fake clock: once per moment, never early, never twice.</summary>
[Collection(nameof(PostgresCollection))]
public class InsightSchedulerTests : InsightTestBase
{
    public InsightSchedulerTests(PostgresFixture fixture) : base(fixture) { }

    /// <summary>Monday 12 March 2029, 09:00 UTC.</summary>
    private static readonly DateTimeOffset Monday9 = new(2029, 3, 12, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A weekly group insight on Mondays at 9, saved the day before, with daily totals up to date.</summary>
    private async Task SaveWeeklyScheduleAsync(DateTimeOffset savedAt, string? timeZone = null, DateTimeOffset? totalsThrough = null)
    {
        await using var context = NewContext();

        var schedule = new InsightSchedule
        {
            Kind = InsightKinds.Group,
            Enabled = true,
            Every = InsightKinds.EveryWeek,
            Hour = 9,
            Weekday = (int)DayOfWeek.Monday,
            DiscordChannelId = "777",
        };

        InsightScheduler.MarkPastMomentsHandled([schedule], timeZone, savedAt);

        context.InsightSchedules.Add(schedule);
        context.InsightSettings.Add(new InsightSettings { TimeZone = timeZone });
        context.DailyTotalsState.Add(new DailyTotalsState { ObservedThrough = totalsThrough ?? Monday9.AddDays(30) });
        await context.SaveChangesAsync(Ct);
    }

    private async Task<int> RunAtAsync(DateTimeOffset now)
    {
        Clock.UtcNow = now;
        await using var context = NewContext();
        return await NewScheduler(context).RunDueAsync(Ct);
    }

    private async Task<List<Insight>> InsightsAsync()
    {
        await using var context = NewContext();
        return await context.Insights.AsNoTracking().OrderBy(i => i.CreatedAt).ToListAsync(Ct);
    }

    [Fact]
    public async Task NothingIsWrittenBeforeTheTime_ThenOnceAtIt_ThenNotAgainUntilNextWeek()
    {
        await TurnAiOnAsync();
        await SaveWeeklyScheduleAsync(Monday9.AddDays(-1));

        Assert.Equal(0, await RunAtAsync(Monday9.AddMinutes(-1)));
        Assert.Empty(Model.Requests);

        Assert.Equal(1, await RunAtAsync(Monday9));
        Assert.Equal(0, await RunAtAsync(Monday9.AddMinutes(1)));
        Assert.Equal(0, await RunAtAsync(Monday9.AddDays(6)));
        Assert.Equal(1, await RunAtAsync(Monday9.AddDays(7).AddMinutes(3)));

        var insights = await InsightsAsync();
        Assert.Equal(2, insights.Count);
        Assert.Equal(2, Model.Requests.Count);

        Assert.Equal(new DateOnly(2029, 3, 5), insights[0].FirstDay);
        Assert.Equal(new DateOnly(2029, 3, 11), insights[0].LastDay);
        Assert.Equal(new DateOnly(2029, 3, 18), insights[1].LastDay);
        Assert.All(insights, i => Assert.Equal("777", i.DiscordChannelId));
        Assert.All(insights, i => Assert.Equal(InsightKinds.StartedBySchedule, i.StartedBy));
    }

    /// <summary>Turning it on at 3 pm with a 9 am time must not write one there and then.</summary>
    [Fact]
    public async Task SavingAfterTheTimeWaitsForTheNextOne()
    {
        await TurnAiOnAsync();
        await SaveWeeklyScheduleAsync(Monday9.AddHours(6));

        Assert.Equal(0, await RunAtAsync(Monday9.AddHours(6).AddMinutes(1)));
        Assert.Empty(Model.Requests);
    }

    [Fact]
    public async Task AServerThatWasDownForWeeksWritesOneNotMany()
    {
        await TurnAiOnAsync();
        await SaveWeeklyScheduleAsync(Monday9.AddDays(-1));

        Assert.Equal(1, await RunAtAsync(Monday9.AddDays(22)));
        Assert.Equal(0, await RunAtAsync(Monday9.AddDays(22).AddMinutes(1)));

        var insight = Assert.Single(await InsightsAsync());
        Assert.Equal(new DateOnly(2029, 4, 1), insight.LastDay);
    }

    [Fact]
    public async Task ItWaitsForDailyTotalsToCoverTheLastDay_ButNotForMoreThanAnHour()
    {
        await TurnAiOnAsync();
        await SaveWeeklyScheduleAsync(Monday9.AddDays(-1), totalsThrough: new DateTimeOffset(2029, 3, 11, 23, 50, 0, TimeSpan.Zero));

        Assert.Equal(0, await RunAtAsync(Monday9));
        Assert.Equal(0, await RunAtAsync(Monday9.AddMinutes(59)));
        Assert.Equal(1, await RunAtAsync(Monday9.AddHours(1)));
    }

    [Fact]
    public async Task TheTimeIsReadInTheScheduleTimeZone()
    {
        await TurnAiOnAsync();

        // 9 am in Chicago on that Monday is 14:00 UTC.
        await SaveWeeklyScheduleAsync(Monday9.AddDays(-1), timeZone: "America/Chicago");

        Assert.Equal(0, await RunAtAsync(Monday9.AddHours(5).AddMinutes(-1)));
        Assert.Equal(1, await RunAtAsync(Monday9.AddHours(5)));
    }

    /// <summary>A provider that is down is not asked again every minute until the next moment.</summary>
    [Fact]
    public async Task AFailedCallIsNotRetriedUntilTheNextTime()
    {
        await TurnAiOnAsync();
        await SaveWeeklyScheduleAsync(Monday9.AddDays(-1));
        Answer = _ => Refused("No credit left");

        await RunAtAsync(Monday9);
        await RunAtAsync(Monday9.AddMinutes(1));
        await RunAtAsync(Monday9.AddHours(3));

        Assert.Single(Model.Requests);
        var failed = Assert.Single(await InsightsAsync());
        Assert.Equal("llm.test answered 400: No credit left", failed.Error);
    }

    [Fact]
    public async Task WithAiOff_TheTimePassesWithoutWriting_AndTurningAiOnLaterDoesNotCatchUp()
    {
        await TurnAiOnAsync(on: false);
        await SaveWeeklyScheduleAsync(Monday9.AddDays(-1));

        Assert.Equal(0, await RunAtAsync(Monday9));

        await TurnAiOnAsync();
        Assert.Equal(0, await RunAtAsync(Monday9.AddHours(2)));

        Assert.Empty(Model.Requests);
        Assert.Empty(await InsightsAsync());
    }

    [Fact]
    public async Task AKindThatIsOffIsNeverWritten()
    {
        await TurnAiOnAsync();
        await SaveWeeklyScheduleAsync(Monday9.AddDays(-1));

        await using (var context = NewContext())
        {
            await context.InsightSchedules.ExecuteUpdateAsync(u => u.SetProperty(s => s.Enabled, false), Ct);
        }

        Assert.Equal(0, await RunAtAsync(Monday9.AddMinutes(5)));
        Assert.Empty(Model.Requests);
    }
}
