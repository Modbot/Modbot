using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Data.Migrations;
using Modbot.TestSupport;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// The data half of the <c>ReadTheHeadCountFromUserCount</c> migration: page readings take
/// <c>userCount</c> as their head count and keep <c>n_users</c> beside it, and the instances' current
/// counts and peaks follow. Then back again.
/// </summary>
/// <remarks>
/// Every suite migrates an empty database, so the SQL runs over nothing everywhere else. Here the
/// migration's SQL is taken off it and run over readings shaped the way the sync wrote them before,
/// in a database of the test's own: the SQL touches every row in the table.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class HeadCountMigrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 20, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IsolatedDatabase _database = null!;

    public async ValueTask InitializeAsync() => _database = await IsolatedDatabase.CreateAsync(fixture, Ct);

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    /// <summary>The club: n_users ran ahead of userCount all evening.</summary>
    private static readonly Guid Club = Guid.NewGuid();

    /// <summary>A page reading stored without a userCount: its n_users stays, and is unsure.</summary>
    private static readonly Guid NoUserCount = Guid.NewGuid();

    /// <summary>Read from the page, then from the group's list, which still holds the count.</summary>
    private static readonly Guid NowFromList = Guid.NewGuid();

    /// <summary>Its highest reading has no userCount; a lower one does.</summary>
    private static readonly Guid UnsureHigh = Guid.NewGuid();

    /// <summary>No readings at all: its peak is left alone.</summary>
    private static readonly Guid NoReadings = Guid.NewGuid();

    [Fact]
    public async Task Up_TakesUserCount_KeepsNUsers_AndPutsCountsAndPeaksRight()
    {
        await SeedAsync();

        await RunAsync(new ReadTheHeadCountFromUserCount().UpOperations);

        await using var db = _database.NewContext();

        Assert.Equal(
            [(38, 40), (51, 80), (55, 60)],
            await ReadingsAsync(db, Club));

        var club = await InstanceAsync(db, Club);
        Assert.Equal((55, false, 55, false), (club.HeadCount, club.HeadCountUnsure, club.PeakUserCount, club.PeakUnsure));

        // The list's reading keeps its number and gets no n_users; the page reading keeps n_users.
        Assert.Equal([(5, null), (30, 30)], await ReadingsAsync(db, NoUserCount));

        var noUserCount = await InstanceAsync(db, NoUserCount);
        Assert.Equal((30, true, 30, true), (noUserCount.HeadCount, noUserCount.HeadCountUnsure, noUserCount.PeakUserCount, noUserCount.PeakUnsure));

        // The list's count is what it shows now, so it is left as it is. Its peak comes down.
        Assert.Equal([(12, 20), (9, null)], await ReadingsAsync(db, NowFromList));

        var fromList = await InstanceAsync(db, NowFromList);
        Assert.Equal((9, false, 12, false), (fromList.HeadCount, fromList.HeadCountUnsure, fromList.PeakUserCount, fromList.PeakUnsure));

        var unsureHigh = await InstanceAsync(db, UnsureHigh);
        Assert.Equal((50, false, 70, true), (unsureHigh.HeadCount, unsureHigh.HeadCountUnsure, unsureHigh.PeakUserCount, unsureHigh.PeakUnsure));

        var noReadings = await InstanceAsync(db, NoReadings);
        Assert.Equal((77, false), (noReadings.PeakUserCount, noReadings.PeakUnsure));
    }

    [Fact]
    public async Task Down_PutsTheOldNumbersBack()
    {
        await SeedAsync();

        await RunAsync(new ReadTheHeadCountFromUserCount().UpOperations);
        await RunAsync(new ReadTheHeadCountFromUserCount().DownOperations);

        await using var db = _database.NewContext();

        Assert.Equal([40, 80, 60], (await ReadingsAsync(db, Club)).Select(r => r.HeadCount));
        Assert.Equal([5, 30], (await ReadingsAsync(db, NoUserCount)).Select(r => r.HeadCount));
        Assert.Equal([20, 9], (await ReadingsAsync(db, NowFromList)).Select(r => r.HeadCount));

        Assert.Equal((60, 80), Counts(await InstanceAsync(db, Club)));
        Assert.Equal((30, 30), Counts(await InstanceAsync(db, NoUserCount)));
        Assert.Equal((9, 20), Counts(await InstanceAsync(db, NowFromList)));
        Assert.Equal((50, 70), Counts(await InstanceAsync(db, UnsureHigh)));
        Assert.Equal(77, (await InstanceAsync(db, NoReadings)).PeakUserCount);

        static (int?, int?) Counts(VRChatInstance i) => (i.HeadCount, i.PeakUserCount);
    }

    /// <summary>
    /// Readings the way the sync stored them before: a page reading's head count was n_users, and
    /// n_users was not kept on its own. Each instance's count is its latest reading's.
    /// </summary>
    private async Task SeedAsync()
    {
        await using var db = _database.NewContext();

        Instance(db, Club, headCount: 60, source: HeadCounts.FromPage, peak: 80);
        Page(db, Club, T0, headCount: 40, userCount: 38);
        Page(db, Club, T0.AddMinutes(10), headCount: 80, userCount: 51);
        Page(db, Club, T0.AddMinutes(20), headCount: 60, userCount: 55);

        Instance(db, NoUserCount, headCount: 30, source: HeadCounts.FromPage, peak: 30);
        List(db, NoUserCount, T0, headCount: 5);
        Page(db, NoUserCount, T0.AddMinutes(10), headCount: 30, userCount: null);

        Instance(db, NowFromList, headCount: 9, source: HeadCounts.FromList, peak: 20);
        Page(db, NowFromList, T0, headCount: 20, userCount: 12);
        List(db, NowFromList, T0.AddMinutes(10), headCount: 9);

        Instance(db, UnsureHigh, headCount: 50, source: HeadCounts.FromPage, peak: 70);
        Page(db, UnsureHigh, T0, headCount: 70, userCount: null);
        Page(db, UnsureHigh, T0.AddMinutes(10), headCount: 50, userCount: 50);

        Instance(db, NoReadings, headCount: null, source: null, peak: 77);

        await db.SaveChangesAsync(Ct);
    }

    private static void Instance(ModbotContext db, Guid id, int? headCount, string? source, int peak)
        => db.VRChatInstances.Add(new VRChatInstance
        {
            Id = id,
            Location = $"wrld_migration:{id:N}~group(grp_test)",
            WorldId = "wrld_migration",
            VRChatInstanceId = id.ToString("N"),
            GroupId = "grp_test",
            OpenedAt = T0,
            LastSeenAt = T0.AddHours(1),
            HeadCount = headCount,
            HeadCountSource = source,
            PeakUserCount = peak,
            SeenInGroupList = true,
        });

    private static void Page(ModbotContext db, Guid instance, DateTimeOffset at, int headCount, int? userCount)
        => db.InstanceHeadCounts.Add(new InstanceHeadCount
        {
            InstanceId = instance,
            CountedAt = at,
            HeadCount = headCount,
            UserCount = userCount,
            MemberCount = 2,
            Source = HeadCounts.FromPage,
        });

    private static void List(ModbotContext db, Guid instance, DateTimeOffset at, int headCount)
        => db.InstanceHeadCounts.Add(new InstanceHeadCount
        {
            InstanceId = instance,
            CountedAt = at,
            HeadCount = headCount,
            MemberCount = headCount,
            Source = HeadCounts.FromList,
        });

    /// <summary>The migration's SQL, in order. The column changes are already in place: the database is migrated.</summary>
    private async Task RunAsync(IEnumerable<MigrationOperation> operations)
    {
        await using var db = _database.NewContext();

        foreach (var sql in operations.OfType<SqlOperation>())
            await db.Database.ExecuteSqlRawAsync(sql.Sql, Ct);
    }

    private static async Task<List<(int HeadCount, int? NUsers)>> ReadingsAsync(ModbotContext db, Guid instance)
        => (await db.InstanceHeadCounts.AsNoTracking()
                .Where(h => h.InstanceId == instance)
                .OrderBy(h => h.CountedAt)
                .Select(h => new { h.HeadCount, h.NUsers })
                .ToListAsync(Ct))
            .Select(h => (h.HeadCount, h.NUsers))
            .ToList();

    private static Task<VRChatInstance> InstanceAsync(ModbotContext db, Guid id)
        => db.VRChatInstances.AsNoTracking().SingleAsync(i => i.Id == id, Ct);
}
