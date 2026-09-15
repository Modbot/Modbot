using Microsoft.EntityFrameworkCore;
using Modbot.AI.Alerts;
using Modbot.AI.Tests.Insights;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.AI.Tests.Alerts;

/// <summary>
/// A private migrated database, a fake clock and a scripted model, plus the few things a watcher
/// needs: facts at a moment, a watcher turned on, and one pass of the checker.
/// </summary>
public abstract class AlertTestBase : InsightTestBase
{
    protected AlertTestBase(PostgresFixture fixture) : base(fixture) { }

    /// <summary>
    /// Twenty minutes before <see cref="InsightTestBase.Start"/>, so a fact here is inside the
    /// window whether the pass runs at Start, a quarter-hour later, or half an hour later.
    /// </summary>
    protected static DateTimeOffset InWindow(int daysAgo = 0) => Start.AddMinutes(-20).AddDays(-daysAgo);

    protected AlertChecker NewChecker(Modbot.Core.Data.ModbotContext context)
        => new(
            context,
            new AlertFigureReader(context),
            NewClients(context),
            NewUsage(context),
            new FactWriter(context, Clock),
            new EventPartitionMaintainer(context, Clock),
            Clock);

    /// <summary>One fact of each of these types, at <paramref name="at"/>, with ids of their own.</summary>
    protected async Task AddFactsAsync(DateTimeOffset at, string type, int count, string prefix = "usr_")
    {
        if (count == 0)
            return;

        await using var context = NewContext();
        await new EventPartitionMaintainer(context, Clock).EnsureForAsync(at, Ct);

        await new FactWriter(context, Clock).WriteManyAsync(
            Enumerable.Range(0, count).Select(i => new FactRecord
            {
                Type = type,
                OccurredAt = at.AddSeconds(i),
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = $"{prefix}{at.Ticks}_{i}",
                Source = FactSource.AuditLog,
            }),
            Ct);
    }

    /// <summary>The same count in the matching window on each of the last fourteen days.</summary>
    protected async Task AddHistoryAsync(string type, int perDay)
    {
        for (var day = 1; day <= AlertFigureReader.HistoryDays; day++)
            await AddFactsAsync(InWindow(day), type, perDay);
    }

    /// <summary>How many different people were active on one day, as the daily totals record it.</summary>
    protected async Task AddActiveAsync(DateOnly day, int people, string prefix)
    {
        await using var context = NewContext();

        for (var person = 0; person < people; person++)
        {
            context.DailyTotals.Add(new DailyTotal
            {
                Day = day,
                Metric = Modbot.Analytics.DailyTotals.DailyTotalMetrics.DiscordMemberMessages,
                Dimension = $"discord:{prefix}{person}",
                Value = 3,
                Origin = DailyTotalOrigin.Computed,
            });
        }

        await context.SaveChangesAsync(Ct);
    }

    protected async Task SetWatchAsync(string watcher, string sensitivity)
    {
        await using var context = NewContext();

        var row = await context.AlertWatches.FirstOrDefaultAsync(w => w.Watcher == watcher, Ct);
        if (row is null)
        {
            row = new AlertWatch { Watcher = watcher };
            context.AlertWatches.Add(row);
        }

        row.Sensitivity = sensitivity;
        await context.SaveChangesAsync(Ct);
    }

    protected async Task SetAlertSettingsAsync(Action<AlertSettings> change)
    {
        await using var context = NewContext();

        var row = await context.AlertSettings.FirstOrDefaultAsync(s => s.Id == 1, Ct);
        if (row is null)
        {
            row = new AlertSettings { Id = 1 };
            context.AlertSettings.Add(row);
        }

        change(row);
        await context.SaveChangesAsync(Ct);
    }

    protected async Task<int> RunAtAsync(DateTimeOffset now)
    {
        Clock.UtcNow = now;
        await using var context = NewContext();
        return await NewChecker(context).RunDueAsync(Ct);
    }

    protected async Task<List<Alert>> AlertsAsync()
    {
        await using var context = NewContext();
        return await context.Alerts.AsNoTracking().OrderBy(a => a.At).ThenBy(a => a.Watcher).ToListAsync(Ct);
    }
}
