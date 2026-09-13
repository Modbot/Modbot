using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Rollups;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Rollups;

/// <summary>
/// A private database, a fake clock, and the shorthand for putting facts into the log.
/// </summary>
public abstract class RollupTestBase : IAsyncLifetime
{
    /// <summary>
    /// Fixed, like the fact tests': rollups are keyed by day, and a suite that quietly depends on
    /// today's date starts failing at a month boundary for reasons nobody will connect to it.
    /// </summary>
    protected static readonly DateTimeOffset Start = new(2029, 3, 10, 12, 0, 0, TimeSpan.Zero);

    protected RollupTestBase(PostgresFixture fixture) => Fixture = fixture;

    protected PostgresFixture Fixture { get; }

    protected IsolatedDatabase Database { get; private set; } = null!;

    protected FakeClock Clock { get; } = new(Start);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
        => Database = await IsolatedDatabase.CreateAsync(Fixture, Ct);

    public ValueTask DisposeAsync() => Database.DisposeAsync();

    protected RollupJob NewJob(ModbotContext context) => new(context, Clock);

    /// <summary>
    /// Writes facts through the real writer, creating whatever partitions they need first.
    /// </summary>
    /// <remarks>
    /// Through <see cref="FactWriter"/> and not straight into the table, so that the rollups are
    /// computed over exactly the rows production would have produced -- including the
    /// server-stamped <c>observed_at</c> the incremental run keys off.
    /// </remarks>
    protected async Task WriteAsync(params FactRecord[] facts)
    {
        await using var context = Database.NewContext();
        var partitions = new EventPartitionMaintainer(context, Clock);

        foreach (var fact in facts)
        {
            await partitions.EnsureForAsync(fact.OccurredAt, Ct);
            await partitions.EnsureForAsync(fact.OccurredBefore ?? fact.OccurredAt, Ct);
        }

        var writer = new FactWriter(context, Clock);
        await writer.WriteManyAsync(facts, Ct);
    }

    protected static FactRecord Fact(
        FactType type,
        DateTimeOffset occurredAt,
        DateTimeOffset? occurredBefore = null,
        string? subjectId = null,
        string? actorId = null,
        FactSource source = FactSource.AuditLog)
        => new()
        {
            Type = type,
            OccurredAt = occurredAt,
            OccurredBefore = occurredBefore,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = subjectId ?? $"usr_{Guid.NewGuid():N}",
            ActorPlatform = actorId is null ? null : FactPlatform.VRChat,
            ActorId = actorId,
            Source = source,
        };

    /// <summary>Every rollup row, ordered, as comparable text.</summary>
    protected async Task<IReadOnlyList<string>> SnapshotAsync(string? metric = null)
    {
        await using var context = Database.NewContext();

        var rows = await context.RollupDaily
            .AsNoTracking()
            .Where(r => metric == null || r.Metric == metric)
            .OrderBy(r => r.Day).ThenBy(r => r.Metric).ThenBy(r => r.Dimension)
            .ToListAsync(Ct);

        return rows
            .Select(r => $"{r.Day:yyyy-MM-dd} {r.Metric} [{r.Dimension}] {r.Origin} {Normalise(r.Value)}")
            .ToList();
    }

    protected async Task<decimal?> ValueAsync(DateOnly day, string metric, string dimension = "")
    {
        await using var context = Database.NewContext();

        var row = await context.RollupDaily
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Day == day && r.Metric == metric && r.Dimension == dimension, Ct);

        return row?.Value;
    }

    protected static DateOnly DayOf(DateTimeOffset instant) => DateOnly.FromDateTime(instant.UtcDateTime);

    /// <summary>
    /// Trailing zeros differ between a value that was summed and one that was written whole;
    /// <c>numeric</c> keeps the scale it was given. The number is what is being compared.
    /// </summary>
    private static string Normalise(decimal value) => (value / 1.000000000000000000000000000000000m)
        .ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture);
}
