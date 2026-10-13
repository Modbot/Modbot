using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Api.Features.Analytics;
using Modbot.Api.Features.Lists;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Appeals;

/// <summary>
/// One calendar year of moderation work, and of appeals (batch 3, step G).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The moderation numbers are the Team page's.</strong> Warnings, kicks, bans and unbans are
/// summed from the same daily totals (<c>moderator.*</c>) the Team page adds up, so the two cannot
/// disagree about how many bans a year held. Nothing is recounted from facts.
/// </para>
/// <para>
/// <strong>Years are UTC days</strong>, as every analytics page counts them: the daily totals are
/// whole UTC days, and a second day boundary would be spec 5.4's stated bug farm. An appeal is
/// counted in the year it was <em>received</em>; upheld and turned down are counted in the year they
/// were <em>decided</em>, so a year's three appeal numbers are what happened in it, and an appeal
/// received in December and answered in January is in both years once.
/// </para>
/// </remarks>
public sealed class YearlyReportQuery(ModbotContext db, IModbotClock clock)
{
    /// <summary>The numbers taken from the daily totals, in the order the report lists them.</summary>
    private static readonly (string Key, string Metric, string Label)[] Counted =
    [
        ("warns", DailyTotalMetrics.ModeratorWarns, "Warnings"),
        ("instance-kicks", DailyTotalMetrics.ModeratorInstanceKicks, "Instance kicks"),
        ("removals", DailyTotalMetrics.ModeratorRemovals, "Removed from group"),
        ("bans", DailyTotalMetrics.ModeratorBans, "Bans"),
        ("unbans", DailyTotalMetrics.ModeratorUnbans, "Unbans"),
    ];

    private static readonly PageSources Sources = PageSources.Of(Counted.Select(c => c.Metric).ToList(), []);

    public async Task<YearlyReport> RunAsync(int? year, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var thisYear = now.UtcDateTime.Year;
        var picked = year ?? thisYear;

        AppealService.CheckYear(picked, now);

        var from = new DateOnly(picked, 1, 1);
        var to = new DateOnly(picked, 12, 31);
        var start = AnalyticsSql.DayStart(from);
        var end = AnalyticsSql.DayEnd(to);

        var totals = await new AnalyticsSql(db).DailyTotalsAsync(from, to, Counted.Select(c => c.Metric).ToList(), ct);

        var lines = Counted
            .Select(c => new ReportLine(c.Key, c.Label, (long)totals.Where(r => r.Metric == c.Metric).Sum(r => r.Value)))
            .ToList();

        var appeals = db.Appeals.AsNoTracking();

        lines.Add(new ReportLine(
            "appeals-received",
            "Appeals received",
            await appeals.CountAsync(a => a.ReceivedOn >= from && a.ReceivedOn <= to, ct)));

        lines.Add(new ReportLine(
            "appeals-upheld",
            "Appeals upheld",
            await appeals.CountAsync(a => a.Outcome == AppealOutcome.Upheld && a.DecidedAt >= start && a.DecidedAt < end, ct)));

        lines.Add(new ReportLine(
            "appeals-turned-down",
            "Appeals turned down",
            await appeals.CountAsync(a => a.Outcome == AppealOutcome.TurnedDown && a.DecidedAt >= start && a.DecidedAt < end, ct)));

        var countedSince = await AnalyticsCoverageQuery.FirstDayAsync(db, Sources, ct);
        var firstAppeal = await appeals.MinAsync(a => (DateOnly?)a.ReceivedOn, ct);

        var first = new[] { countedSince?.Year, firstAppeal?.Year }.Where(y => y is not null).Select(y => y!.Value)
            .DefaultIfEmpty(thisYear)
            .Min();

        var years = Enumerable.Range(Math.Max(first, AppealService.FirstYear), thisYear - Math.Max(first, AppealService.FirstYear) + 1)
            .Reverse()
            .ToList();

        return new YearlyReport(picked, lines, years, countedSince, now);
    }

    /// <summary>The report as a CSV file: one row per number. Cells that could be read as a formula are defused.</summary>
    public static string Csv(YearlyReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var text = new StringBuilder("Year,Number,Count\r\n");

        foreach (var line in report.Lines)
        {
            text.Append(report.Year.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(ListEndpoints.CsvCell(line.Label)).Append(',')
                .Append(line.Count.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        return text.ToString();
    }
}
