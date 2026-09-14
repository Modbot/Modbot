using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Storage;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Storage;

/// <summary>
/// The storage chart's history: one row a day, however many times a day gets recorded.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class StorageHistoryTests : AnalyticsTestBase
{
    public StorageHistoryTests(PostgresFixture fixture) : base(fixture) { }

    private StorageHistory NewHistory(Core.Data.ModbotContext context)
        => new(context, new StorageEstimator(context, Clock), Clock);

    /// <summary>
    /// A restart, an overlapping run, or a second call later in the day must update the day's row,
    /// never add another one beside it -- and the later measurement is the one kept.
    /// </summary>
    [Fact]
    public async Task RecordingTheSameDayTwiceLeavesOneRow()
    {
        await using (var context = Database.NewContext())
            await NewHistory(context).RecordTodayAsync(Ct);

        await WriteAsync(Enumerable
            .Range(0, 200)
            .Select(i => Fact(FactType.InstanceJoined, Clock.UtcNow, subjectId: $"usr_{i}"))
            .ToArray());

        Clock.UtcNow = Clock.UtcNow.AddHours(3);

        await using (var context = Database.NewContext())
            await NewHistory(context).RecordTodayAsync(Ct);

        await using var read = Database.NewContext();
        var rows = await read.StorageDays.AsNoTracking().ToListAsync(Ct);

        var row = Assert.Single(rows);
        Assert.Equal(DayOf(Start), row.Day);
        Assert.Equal(200, row.Facts);
        Assert.True(row.Bytes > 0);
    }

    [Fact]
    public async Task EachDayGetsItsOwnRowAndIsReadBackOldestFirst()
    {
        await using (var context = Database.NewContext())
        {
            await NewHistory(context).RecordTodayAsync(Ct);

            Clock.UtcNow = Clock.UtcNow.AddDays(1);
            await NewHistory(context).RecordTodayAsync(Ct);
        }

        await using var read = Database.NewContext();
        var history = NewHistory(read);

        Assert.True(await history.IsTodayRecordedAsync(Ct));
        Assert.Equal(
            [DayOf(Start), DayOf(Start).AddDays(1)],
            (await history.SinceAsync(DayOf(Start), Ct)).Select(d => d.Day));
        Assert.Equal(
            [DayOf(Start).AddDays(1)],
            (await history.SinceAsync(DayOf(Start).AddDays(1), Ct)).Select(d => d.Day));
    }

    [Fact]
    public async Task ADayWithNoRowIsReportedAsNotRecorded()
    {
        await using var context = Database.NewContext();

        Assert.False(await NewHistory(context).IsTodayRecordedAsync(Ct));
    }
}
