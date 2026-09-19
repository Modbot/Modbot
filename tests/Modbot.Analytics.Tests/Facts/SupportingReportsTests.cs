using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Facts;

/// <summary>
/// Who else reported a fact. Deduplication keeps one fact per event and used to throw the other
/// reports away entirely, so nothing could say that two independent clients agreed -- which is
/// better evidence than one client saying so. The extras are kept beside the fact, never as one.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class SupportingReportsTests : FactTestBase
{
    public SupportingReportsTests(PostgresFixture db) : base(db) { }

    private static readonly Guid AdasPc = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid BensPc = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid CidsPc = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    private async Task<long> ReportAsync(string subject, double jitterSeconds, Guid device, CancellationToken ct)
    {
        await using var context = Db.NewContext();
        var writer = NewWriter(context, new FakeClock(Now.AddSeconds(jitterSeconds)));
        var result = await writer.WriteAsync(Presence(subject, Now.AddSeconds(jitterSeconds), device: device), ct);
        return result.Id;
    }

    private async Task<List<EventReport>> ReportsAsync(long factId, CancellationToken ct)
    {
        await using var context = Db.NewContext();
        return await context.EventReports.AsNoTracking()
            .Where(r => r.FactId == factId)
            .OrderBy(r => r.ReportedAt)
            .ToListAsync(ct);
    }

    [Fact]
    public async Task OneClientReportingOneJoin_LeavesNoSupportingReports()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = UniqueId("usr");

        var factId = await ReportAsync(subject, 0, AdasPc, ct);

        // The one client that reported it is named on the fact itself; there is nothing to support.
        Assert.Empty(await ReportsAsync(factId, ct));
    }

    [Fact]
    public async Task ThreeClientsReportingOneJoin_LeaveOneFactAndTwoSupportingReports()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = UniqueId("usr");

        var factId = await ReportAsync(subject, 0, AdasPc, ct);
        Assert.Equal(factId, await ReportAsync(subject, 0.6, BensPc, ct));
        Assert.Equal(factId, await ReportAsync(subject, 1.4, CidsPc, ct));

        await using var context = Db.NewContext();
        Assert.Equal(1, await context.Events.CountAsync(e => e.SubjectId == subject, ct));

        var reports = await ReportsAsync(factId, ct);
        Assert.Equal([BensPc, CidsPc], reports.Select(r => r.DeviceId));

        // Dated at the fact's own occurred_at, not the arriving report's: it is what retention
        // prunes these rows by, and the two can fall either side of a month boundary.
        Assert.All(reports, r => Assert.Equal(Now, r.OccurredAt));
    }

    /// <summary>
    /// A client that retries a batch resends reports the server already has. Recording those would
    /// make one client saying a thing twice look like two clients agreeing, which is the whole
    /// value of the row.
    /// </summary>
    [Fact]
    public async Task TheSameClientReportingTwice_LeavesNoSupportingReport()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = UniqueId("usr");

        var factId = await ReportAsync(subject, 0, AdasPc, ct);
        Assert.Equal(factId, await ReportAsync(subject, 1.1, AdasPc, ct));

        Assert.Empty(await ReportsAsync(factId, ct));
    }

    [Fact]
    public async Task AClientReportingTwiceAfterSomebodyElse_IsRecordedOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = UniqueId("usr");

        var factId = await ReportAsync(subject, 0, AdasPc, ct);
        await ReportAsync(subject, 0.7, BensPc, ct);
        await ReportAsync(subject, 1.9, BensPc, ct);

        var report = Assert.Single(await ReportsAsync(factId, ct));
        Assert.Equal(BensPc, report.DeviceId);
    }

    /// <summary>
    /// A genuine second event is a second fact, and its reporters are its own. Nothing about this
    /// may make one arrival look like two, or two look like one.
    /// </summary>
    [Fact]
    public async Task AGenuineRejoin_KeepsItsOwnReportsApart()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = UniqueId("usr");

        var first = await ReportAsync(subject, 0, AdasPc, ct);
        await ReportAsync(subject, 0.5, BensPc, ct);

        var second = await ReportAsync(subject, 60, AdasPc, ct);
        await ReportAsync(subject, 60.4, BensPc, ct);

        Assert.NotEqual(first, second);

        await using var context = Db.NewContext();
        Assert.Equal(2, await context.Events.CountAsync(e => e.SubjectId == subject, ct));

        Assert.Equal(BensPc, Assert.Single(await ReportsAsync(first, ct)).DeviceId);
        Assert.Equal(BensPc, Assert.Single(await ReportsAsync(second, ct)).DeviceId);
    }

    /// <summary>
    /// A client report with no device on its payload -- a shape no companion sends, but the writer
    /// must not invent a reporter for it.
    /// </summary>
    [Fact]
    public async Task AReportNamingNoClient_LeavesNoSupportingReport()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = UniqueId("usr");

        var factId = await ReportAsync(subject, 0, AdasPc, ct);

        await using (var context = Db.NewContext())
        {
            var writer = NewWriter(context, new FakeClock(Now));
            await writer.WriteAsync(Presence(subject, Now.AddSeconds(1)), ct);
        }

        Assert.Empty(await ReportsAsync(factId, ct));
    }
}
