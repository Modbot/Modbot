using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Messages;
using Modbot.Analytics.Reviews;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.TestSupport;

namespace Modbot.Demo.Tests;

/// <summary>
/// The seeder and the history writer over one real database, built by hand.
/// </summary>
/// <remarks>
/// No dependency injection container: the demo's two halves take everything they need through
/// their constructors, and building them by hand keeps a test honest about what they actually
/// touch. Real PostgreSQL, because the fact log is partitioned and the daily totals take an
/// advisory lock, and neither exists in a substitute provider.
/// </remarks>
internal sealed class DemoSeedHost : IAsyncDisposable
{
    private readonly ModbotContext _db;

    private DemoSeedHost(ModbotContext db, FakeClock clock, ISecretProtector protector)
    {
        _db = db;
        Clock = clock;
        Protector = protector;
    }

    public FakeClock Clock { get; }

    public ISecretProtector Protector { get; }

    public ModbotContext Db => _db;

    public static async Task<DemoSeedHost> StartAsync(PostgresFixture fixture, CancellationToken ct)
    {
        var db = fixture.NewContext();
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));
        var protector = await AesGcmSecretProtector.CreateAsync(db);

        var host = new DemoSeedHost(db, clock, protector);
        await host.EmptyAsync(ct);
        return host;
    }

    public DemoSeeder Seeder() => new(_db, Clock, Protector);

    public DemoHistory History()
    {
        var facts = new FactWriter(_db, Clock);
        var partitions = new EventPartitionMaintainer(_db, Clock);

        return new DemoHistory(
            _db,
            facts,
            partitions,
            new MessagePartitionMaintainer(_db),
            new DailyTotalsJob(_db, Clock),
            new ReviewJob(_db, new ReviewFacts(facts, partitions, Clock), Clock),
            new DailyTotalCounter(_db, Clock),
            Clock);
    }

    /// <summary>Puts the database back to "nothing has ever happened here".</summary>
    public Task EmptyAsync(CancellationToken ct) => Seeder().WipeAsync(ct);

    /// <summary>
    /// A few facts about the seeded people, for the tests that only need the log not to be empty.
    /// </summary>
    public async Task WriteSomeFactsAsync(int count, CancellationToken ct)
    {
        var writer = new FactWriter(_db, Clock);
        await new EventPartitionMaintainer(_db, Clock).EnsureAsync(ct);

        var people = await _db.VRChatUsers.Select(u => u.UserId).Take(count).ToListAsync(ct);

        foreach (var person in people)
        {
            await writer.WriteAsync(
                new FactRecord
                {
                    Type = FactType.MemberJoined,
                    OccurredAt = Clock.UtcNow.AddHours(-1),
                    SubjectPlatform = FactPlatform.VRChat,
                    SubjectId = person,
                    Source = FactSource.AuditLog,
                },
                ct);
        }
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
