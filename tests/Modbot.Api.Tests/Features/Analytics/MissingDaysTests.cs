using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.DailyTotals;
using Modbot.Api.Features.Analytics;
using Modbot.Api.Features.Analytics.Group;
using Modbot.Api.Features.Analytics.Instances;
using Modbot.Api.Features.Analytics.Server;
using Modbot.Api.Features.Analytics.Team;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Api.Tests.Features.Places;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Analytics;

/// <summary>
/// The days a chart marks as having no data, and the day it marks as not over yet: worked out per
/// source, from what that source recorded, and never guessed.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class MissingDaysTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 6, 15);

    private readonly PostgresFixture _db;

    public MissingDaysTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly[] Range(DateOnly from, DateOnly to) => MissingDays.Days(from, to).ToArray();

    // ── The rules, without a database ──────────────────────────────────────────────────────

    [Fact]
    public void Today_IsTheWindowsLastDay_OnlyWhenThatDayIsTodayByTheServersClock()
    {
        Assert.Equal(Today, MissingDays.Today(Today, Now));
        Assert.Null(MissingDays.Today(Today.AddDays(-1), Now));

        // One second before midnight is still the same day; one second after is the next.
        Assert.Equal(Today, MissingDays.Today(Today, new DateTimeOffset(2026, 6, 15, 23, 59, 59, TimeSpan.Zero)));
        Assert.Null(MissingDays.Today(Today, new DateTimeOffset(2026, 6, 16, 0, 0, 1, TimeSpan.Zero)));
    }

    [Fact]
    public void Before_MarksTheDaysBeforeASourcesFirstDay_AndEveryDayWhenItNeverRecordedAnything()
    {
        var from = Today.AddDays(-6);

        Assert.Equal(Range(from, Today.AddDays(-4)), MissingDays.Before(from, Today, Today.AddDays(-3)));
        Assert.Empty(MissingDays.Before(from, Today, from));
        Assert.Empty(MissingDays.Before(from, Today, from.AddDays(-100)));
        Assert.Equal(Range(from, Today), MissingDays.Before(from, Today, null));
    }

    /// <summary>
    /// Before anything is known: missing. From the first fact to the first reading: carried, not
    /// missing. From the first reading on: a day with no reading is missing.
    /// </summary>
    [Fact]
    public void WithoutReadings_SplitsTheWindowIntoNothingKnown_Carried_AndRead()
    {
        var from = Today.AddDays(-9);
        var firstFact = Today.AddDays(-7);
        var firstReading = Today.AddDays(-4);
        var read = new HashSet<DateOnly> { Today.AddDays(-4), Today.AddDays(-3), Today.AddDays(-1), Today };

        var missing = MissingDays.WithoutReadings(from, Today, firstFact, firstReading, read);

        Assert.Equal([Today.AddDays(-9), Today.AddDays(-8), Today.AddDays(-2)], missing);
    }

    [Fact]
    public void WithoutReadings_WithNothingKnownAtAll_IsEveryDay_AndWithOnlyFacts_IsTheDaysBeforeThem()
    {
        var from = Today.AddDays(-3);

        Assert.Equal(Range(from, Today), MissingDays.WithoutReadings(from, Today, null, null, new HashSet<DateOnly>()));
        Assert.Equal([from], MissingDays.WithoutReadings(from, Today, Today.AddDays(-2), null, new HashSet<DateOnly>()));
    }

    // ── The audit log ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The first audit-log fact is where the record starts. A fact from another source earlier than
    /// it does not move the start: a sync diff is not the audit log.
    /// </summary>
    [Fact]
    public async Task AuditLogSeries_MarkTheDaysBeforeTheFirstAuditLogFact()
    {
        await using var host = await StartAsync();

        await host.WriteFactAsync(GroupInfoBaseline(100, Now.AddDays(-6)), Ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_a", Now.AddDays(-3)), Ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberLeft, "usr_b", Now.AddDays(-1)), Ct);
        await host.RebuildDailyTotalsAsync(Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);

        var group = await host.GetJsonAsync<GroupAnalytics>("/api/analytics/group?days=7", cookie, Ct);
        Assert.Equal(Range(Today.AddDays(-6), Today.AddDays(-4)), group.DaysWithoutAuditLog);
        Assert.Equal(Today, group.Today);

        var team = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?days=7", cookie, Ct);
        Assert.Equal(group.DaysWithoutAuditLog, team.DaysWithoutAuditLog);
    }

    [Fact]
    public async Task AuditLogSeries_WithNoAuditLogAtAll_MarkEveryDay_AndAWindowEndingYesterdayHasNoToday()
    {
        await using var host = await StartAsync();

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        var yesterday = Today.AddDays(-1);
        var page = await host.GetJsonAsync<GroupAnalytics>(
            $"/api/analytics/group?from={yesterday.AddDays(-2):yyyy-MM-dd}&to={yesterday:yyyy-MM-dd}", cookie, Ct);

        Assert.Equal(Range(yesterday.AddDays(-2), yesterday), page.DaysWithoutAuditLog);
        Assert.Null(page.Today);
    }

    /// <summary>
    /// A retention window deletes old facts and keeps the daily totals made from them. The record
    /// still reaches as far back as those totals do, and those days are not "no data".
    /// </summary>
    [Fact]
    public async Task AuditLogSeries_UnderARetentionWindow_ReachBackToTheDailyTotals()
    {
        await using var host = await StartAsync();

        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_old", Now.AddDays(-5)), Ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, "usr_new", Now.AddDays(-2)), Ct);
        await host.RebuildDailyTotalsAsync(Ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var settings = await db.GetSettingsAsync(Ct);
            settings.ModerationFactRetentionDays = 3;
            await db.SaveChangesAsync(Ct);

            // What the pruner would have done to the older fact.
            await db.Events.Where(e => e.SubjectId == "usr_old").ExecuteDeleteAsync(Ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        var page = await host.GetJsonAsync<GroupAnalytics>("/api/analytics/group?days=7", cookie, Ct);

        Assert.Equal(Range(Today.AddDays(-6), Today.AddDays(-6)), page.DaysWithoutAuditLog);
    }

    // ── Head counts and presence reports ───────────────────────────────────────────────────

    /// <summary>
    /// Three days: one an instance was open and counted and reported from, one an instance was open
    /// and neither counted nor reported from, and one nothing was open. Only the second is missing;
    /// the third is a quiet day and must not be called anything else.
    /// </summary>
    [Fact]
    public async Task HeadCountsAndPresence_AreMissingOnlyOnDaysSomethingWasOpen()
    {
        await using var host = await StartAsync();
        await ManagedGroupAsync(host);

        var counted = Now.AddDays(-3);
        var uncounted = Now.AddDays(-2);

        var seen = await PlacesFixtures.InstanceAsync(host, "wrld_a", "1", counted, counted.AddHours(2), counted.AddHours(2), Ct);
        await PlacesFixtures.InstanceAsync(host, "wrld_a", "2", uncounted, uncounted.AddHours(2), uncounted.AddHours(2), Ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.InstanceHeadCounts.Add(new InstanceHeadCount
            {
                InstanceId = seen.Id,
                CountedAt = counted.AddMinutes(5),
                HeadCount = 4,
                UserCount = 4,
                MemberCount = 4,
                Source = HeadCounts.FromPage,
            });
            await db.SaveChangesAsync(Ct);
        }

        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_a", counted.AddMinutes(10), "wrld_a", "1"), Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        var page = await host.GetJsonAsync<InstancesAnalytics>("/api/analytics/instances?days=7", cookie, Ct);

        Assert.Equal([Today.AddDays(-2)], page.DaysWithoutHeadCounts);
        Assert.Equal([Today.AddDays(-2)], page.DaysWithoutPresenceReports);

        var activity = await host.GetJsonAsync<InstanceActivitySeries>("/api/analytics/instances/activity?range=week", cookie, Ct);
        Assert.Equal([Today.AddDays(-2)], activity.DaysWithoutHeadCounts);
    }

    // ── The member count ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The chart used to splice facts and readings into one unmarked line. Now each point says
    /// which it is, and the days with neither are listed.
    /// </summary>
    [Fact]
    public async Task MemberCount_MarksFactPointsCarried_ReadingsNot_AndListsTheDaysWithNeither()
    {
        await using var host = await StartAsync();

        await host.WriteFactAsync(GroupInfoBaseline(14208, Now.AddDays(-10), online: 30), Ct);
        await host.WriteFactAsync(GroupInfoChange(14208, 14230, Now.AddDays(-6)), Ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.GroupMemberCounts.AddRange(
                new GroupMemberCount { GroupId = "grp_1", CountedAt = Now.AddDays(-3), MemberCount = 14240, OnlineMemberCount = 33 },
                new GroupMemberCount { GroupId = "grp_1", CountedAt = Now.AddDays(-1), MemberCount = 14241, OnlineMemberCount = 34 },
                new GroupMemberCount { GroupId = "grp_1", CountedAt = Now.AddHours(-1), MemberCount = 14242, OnlineMemberCount = 35 });
            await db.SaveChangesAsync(Ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        var series = await host.GetJsonAsync<GroupMemberCountSeries>("/api/analytics/group/member-count?range=month", cookie, Ct);

        Assert.Equal([true, true, false, false, false], series.Points.Select(p => p.Carried));
        Assert.Equal([14208, 14230, 14240, 14241, 14242], series.Points.Select(p => p.Members));

        // Before the baseline nothing is known; from it to the first reading the facts carry the
        // line; after the first reading, the one day without a reading is missing.
        var from = AnalyticsSql.DayOf(Now.AddDays(-30));
        Assert.Equal(
            [.. Range(from, Today.AddDays(-11)), Today.AddDays(-2)],
            series.DaysWithoutReadings);
    }

    // ── Discord ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The bot's first day is the first member count it kept. Messages can reach further back,
    /// because the bot reads history when it signs in.
    /// </summary>
    [Fact]
    public async Task DiscordSeries_MarkTheDaysBeforeTheBot_AndMessagesBeforeTheFirstMessage()
    {
        await using var host = await StartAsync();

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.DailyTotals.AddRange(
                new DailyTotal { Day = Today.AddDays(-2), Metric = DailyTotalMetrics.DiscordMembersCount, Value = 250, Origin = DailyTotalOrigin.Counted },
                new DailyTotal { Day = Today.AddDays(-4), Metric = DailyTotalMetrics.DiscordMessages, Value = 12, Origin = DailyTotalOrigin.Computed });
            await db.SaveChangesAsync(Ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        var page = await host.GetJsonAsync<ServerAnalytics>("/api/analytics/server?days=7", cookie, Ct);

        Assert.Equal(Range(Today.AddDays(-6), Today.AddDays(-3)), page.DaysWithoutBot);
        Assert.Equal(Range(Today.AddDays(-6), Today.AddDays(-5)), page.DaysWithoutMessages);
        Assert.Equal(Today, page.Today);
    }

    private async Task<ReadSurfaceTestHost> StartAsync()
    {
        var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        host.Clock.UtcNow = Now;
        return host;
    }

    private static async Task ManagedGroupAsync(ReadSurfaceTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await db.GetSettingsAsync(Ct);
        settings.ManagedGroupId = "grp_1";
        await db.SaveChangesAsync(Ct);
    }
}
