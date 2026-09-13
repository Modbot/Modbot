using Modbot.Analytics.Facts;
using Modbot.Analytics.Rollups;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Rollups;

/// <summary>
/// The invariant the whole analytics design rests on: <strong>rollups are always recomputable
/// from facts</strong> (spec 5.2).
/// </summary>
/// <remarks>
/// <para>
/// Over randomised fact sequences -- out of order, some imprecise, some straddling midnight,
/// arriving in unpredictable batches -- the rollups built up incrementally must equal the ones a
/// rebuild from scratch produces, row for row and digit for digit. If that ever stops holding,
/// then a rebuild is not a fix for an aggregation bug, it is a second bug, and the promise that a
/// wrong metric is "a re-run, not lost data" is empty.
/// </para>
/// <para>
/// Randomised with fixed seeds rather than a live random source: a property test that cannot be
/// re-run identically after it fails is a rumour, not a test. Each seed is a separate case, so a
/// failure names the sequence that broke it.
/// </para>
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class RollupRecomputationPropertyTests : AnalyticsTestBase
{
    public RollupRecomputationPropertyTests(PostgresFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(98765)]
    public async Task IncrementalRunsEqualAFullRebuild(int seed)
    {
        var random = new Random(seed);
        var facts = Generate(random, count: 60, spanDays: 8);

        // Arrival order is independent of when things happened: the audit log is read backwards,
        // sync diffs land late, and a client reconnecting reports an hour of history at once.
        Shuffle(facts, random);

        var written = 0;
        while (written < facts.Count)
        {
            var batch = Math.Min(random.Next(1, 12), facts.Count - written);
            await WriteAsync([.. facts.Skip(written).Take(batch)]);
            written += batch;

            await using var context = Database.NewContext();
            await NewJob(context).RunIncrementalAsync(Ct);

            // Time passes between batches, which is what moves the incremental watermark.
            Clock.Advance(TimeSpan.FromMinutes(random.Next(1, 90)));
        }

        var incremental = await SnapshotAsync();

        await using (var context = Database.NewContext())
            await NewJob(context).RebuildAsync(Ct);

        var rebuilt = await SnapshotAsync();

        // A pair of empty snapshots would satisfy the equality and prove nothing.
        Assert.NotEmpty(incremental);
        Assert.Contains(incremental, row => row.Contains(RollupMetrics.MembersTotal, StringComparison.Ordinal));
        Assert.Equal(incremental, rebuilt);
    }

    /// <summary>
    /// The same, with the fact log built up first and the incremental run only catching up at the
    /// end -- the shape a backfill or an import takes.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(2024)]
    public async Task AFirstRunOverAnExistingLogEqualsARebuild(int seed)
    {
        var random = new Random(seed);
        var facts = Generate(random, count: 40, spanDays: 5);
        Shuffle(facts, random);

        foreach (var fact in facts)
        {
            await WriteAsync(fact);
            Clock.Advance(TimeSpan.FromSeconds(random.Next(1, 600)));
        }

        await using (var context = Database.NewContext())
            await NewJob(context).RunIncrementalAsync(Ct);

        var incremental = await SnapshotAsync();

        await using (var context = Database.NewContext())
            await NewJob(context).RebuildAsync(Ct);

        Assert.NotEmpty(incremental);
        Assert.Equal(incremental, await SnapshotAsync());
    }

    private static List<FactRecord> Generate(Random random, int count, int spanDays)
    {
        var actors = new[] { "alice", "bob", "carol", null };
        var types = new[]
        {
            FactType.MemberJoined,
            FactType.MemberJoined,
            FactType.MemberLeft,
            FactType.MemberBanned,
            FactType.MemberKicked,
            FactType.RoleGranted,
            FactType.RoleRevoked,

            // Presence: no metric counts it today. It still has to be harmless -- and it is what
            // catches a rebuild that widens its day range on facts it does not aggregate.
            FactType.InstanceJoined,
        };

        var facts = new List<FactRecord>(count);

        for (var i = 0; i < count; i++)
        {
            var occurredAt = Start
                .AddDays(random.Next(spanDays))
                .AddMinutes(random.Next(-720, 720))
                .AddSeconds(random.Next(60));

            // A third of facts are inferences with a window. Most windows are the minutes a sync
            // interval covers; a few are long enough to cross midnight, which is the case that
            // splits a fact across two days and produces fractional values.
            DateTimeOffset? occurredBefore = random.Next(3) == 0
                ? occurredAt.AddMinutes(random.Next(3) == 0 ? random.Next(60, 900) : random.Next(1, 10))
                : null;

            facts.Add(Fact(
                types[random.Next(types.Length)],
                occurredAt,
                occurredBefore,
                actorId: actors[random.Next(actors.Length)],
                source: occurredBefore is null ? FactSource.AuditLog : FactSource.SyncDiff));
        }

        return facts;
    }

    private static void Shuffle(List<FactRecord> facts, Random random)
    {
        for (var i = facts.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (facts[i], facts[j]) = (facts[j], facts[i]);
        }
    }
}
