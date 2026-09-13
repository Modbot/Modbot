using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Npgsql;

namespace Modbot.Analytics.Retention;

/// <summary>
/// Enforces tiered retention on the fact log by destroying whole partitions.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.5: moderation facts are kept forever by default, presence facts for ninety days, rollups
/// forever. All three are configurable from <see cref="Settings"/>; a retention of zero days means
/// "keep forever".
/// </para>
///
/// <para><strong>Why a DROP and never a DELETE.</strong></para>
/// <para>
/// At peak presence volume ninety days is on the order of 10^8 rows. Deleting those is hours of
/// work, a table twice its own size in dead tuples, and an autovacuum that never catches up.
/// Dropping the table they live in is a catalogue update. The monthly partitioning exists for this
/// one job, and nothing in here may ever fall back to a mass DELETE.
/// </para>
///
/// <para><strong>The rule, and the problem the rule has.</strong></para>
/// <para>
/// A partition can only be dropped when <em>every</em> fact in it is past retention. A monthly
/// partition holds both classes mixed together, so the naive reading of that rule is "governed by
/// the longest retention of any class present" -- and with moderation left at forever, that means
/// no partition is ever droppable and presence facts are never pruned at all. The window an
/// operator configured would be a comment rather than a behaviour. The rule is right; monthly-only
/// partitioning is what cannot satisfy it.
/// </para>
/// <para>
/// So a partition is handled in one of three ways, decided from its upper bound:
/// </para>
/// <list type="number">
/// <item>
/// Every class in it is past retention -- <c>DROP TABLE</c>. Nothing in it was worth keeping.
/// </item>
/// <item>
/// Some class is past retention and another is not, and the partition actually holds rows of the
/// expired class -- <strong>evacuate</strong>: in one transaction, detach the partition, put an
/// empty one in its place, copy the surviving classes across, and drop the old table. The expired
/// rows -- the volume -- are destroyed by a <c>DROP TABLE</c>, exactly as the design requires;
/// what is copied is the low-volume class the spec sizes at hundreds a day.
/// </item>
/// <item>Otherwise, leave it alone.</item>
/// </list>
/// <para>
/// The evacuation happens once per partition per class boundary: once the presence rows are gone,
/// the partition holds nothing expired and step 3 skips it forever after. The alternative --
/// sub-partitioning every month by retention class -- makes the steady state cleaner and was
/// rejected for M0 because it means a partition key column the writer has to set, a rewrite of the
/// fact table, and a default sub-partition to catch fact types nobody has classified yet, whose
/// failure mode is silent ingest loss. If presence volume ever makes the monthly copy expensive,
/// that is the change to make, and this class is where it lands.
/// </para>
/// <para>
/// Partitions are only ever acted on whole: a partition is touched when its upper bound is past
/// the cutoff, never when the cutoff falls inside it. Half a month of presence facts survives a
/// few extra weeks rather than being picked out row by row.
/// </para>
/// </remarks>
public sealed partial class RetentionPruner
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;

    public RetentionPruner(
        ModbotContext db,
        IModbotClock clock,
        IFactWriter facts,
        EventPartitionMaintainer partitions)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);

        _db = db;
        _clock = clock;
        _facts = facts;
        _partitions = partitions;
    }

    public async Task<RetentionResult> PruneAsync(CancellationToken ct = default)
    {
        var cutoffs = await CutoffsAsync(ct);

        // Nothing expires: every class is set to keep forever. Not an error -- it is the default
        // for moderation facts, and a deployment may well choose it for everything.
        if (cutoffs.Values.All(c => c is null))
            return new RetentionResult([], []);

        var dropped = new List<string>();
        var evacuated = new List<string>();

        foreach (var partition in await PartitionsAsync(ct))
        {
            var expired = FactRetention.All
                .Where(c => cutoffs[c] is { } cutoff && partition.UpperBound <= cutoff)
                .ToList();

            if (expired.Count == 0)
                continue;

            if (expired.Count == FactRetention.All.Count)
            {
                await DropAsync(partition, ct);
                dropped.Add(partition.Name);
                continue;
            }

            var expiredTypes = expired.SelectMany(FactRetention.TypesIn).ToList();
            if (!await ContainsAnyAsync(partition, expiredTypes, ct))
                continue;

            await EvacuateAsync(partition, expired, ct);
            evacuated.Add(partition.Name);
        }

        await RecordAsync(dropped, evacuated, ct);

        return new RetentionResult(dropped, evacuated);
    }

    /// <summary>
    /// The instant before which each class is past retention, or null for "keep forever".
    /// </summary>
    private async Task<IReadOnlyDictionary<RetentionClass, DateTimeOffset?>> CutoffsAsync(
        CancellationToken ct)
    {
        // Read, never created. Retention can run before onboarding has written the row, and the
        // defaults on the entity are the documented policy.
        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct)
            ?? new Settings();

        var now = _clock.UtcNow;

        return new Dictionary<RetentionClass, DateTimeOffset?>
        {
            [RetentionClass.Moderation] = Cutoff(settings.ModerationFactRetentionDays),
            [RetentionClass.Presence] = Cutoff(settings.PresenceFactRetentionDays),
        };

        DateTimeOffset? Cutoff(int days) => days > 0 ? now.AddDays(-days) : null;
    }

    /// <summary>
    /// The fact log's partitions, with the range each covers.
    /// </summary>
    /// <remarks>
    /// The bounds come from the partition's name, which <see cref="EventPartitionMaintainer"/>
    /// derives from the UTC month -- the naming is a contract between the two jobs. Anything
    /// attached to the table whose name does not match that pattern is left strictly alone:
    /// whoever created a partition by hand knows something this job does not, and dropping a table
    /// on the strength of a guess about its contents is not a thing to do.
    /// </remarks>
    private async Task<IReadOnlyList<Partition>> PartitionsAsync(CancellationToken ct)
    {
        var names = await _db.Database
            .SqlQuery<string>($"""
                SELECT child.relname AS "Value"
                FROM pg_inherits i
                JOIN pg_class child ON child.oid = i.inhrelid
                JOIN pg_class parent ON parent.oid = i.inhparent
                WHERE parent.relname = 'modbot_event'
                ORDER BY child.relname
                """)
            .ToListAsync(ct);

        var partitions = new List<Partition>();

        foreach (var name in names)
        {
            var match = MonthlyPartitionName().Match(name);
            if (!match.Success)
                continue;

            var year = int.Parse(match.Groups["year"].ValueSpan, CultureInfo.InvariantCulture);
            var month = int.Parse(match.Groups["month"].ValueSpan, CultureInfo.InvariantCulture);
            var lower = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);

            partitions.Add(new Partition(name, lower, lower.AddMonths(1)));
        }

        return partitions;
    }

    private async Task<bool> ContainsAnyAsync(
        Partition partition,
        IReadOnlyList<FactType> types,
        CancellationToken ct)
    {
        var found = await QueryAsync<bool>(
            $"SELECT EXISTS (SELECT 1 FROM {partition.Name} WHERE type = ANY(@types)) AS \"Value\"",
            [new NpgsqlParameter("types", types.Select(t => (short)t).ToArray())],
            ct);

        return found.Count > 0 && found[0];
    }

    private async Task DropAsync(Partition partition, CancellationToken ct)
        => await ExecuteAsync($"DROP TABLE {partition.Name}", ct);

    /// <summary>
    /// Replaces a partition with one holding only the classes that are still in retention.
    /// </summary>
    /// <remarks>
    /// One transaction, so a crash half way through leaves the old partition attached and intact
    /// rather than a month of moderation history in a table nobody is looking at. PostgreSQL runs
    /// DDL transactionally, which is what makes this safe to write as five statements.
    /// </remarks>
    private async Task EvacuateAsync(
        Partition partition,
        IReadOnlyList<RetentionClass> expired,
        CancellationToken ct)
    {
        var keptTypes = FactRetention.All
            .Except(expired)
            .SelectMany(FactRetention.TypesIn)
            .Select(t => (short)t)
            .ToArray();

        var evacuating = partition.Name + "_evacuating";
        var lower = Bound(partition.LowerBound);
        var upper = Bound(partition.UpperBound);

        var ownsTransaction = _db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await _db.Database.BeginTransactionAsync(ct) : null;

        try
        {
            await ExecuteAsync($"ALTER TABLE modbot_event DETACH PARTITION {partition.Name}", ct);
            await ExecuteAsync($"ALTER TABLE {partition.Name} RENAME TO {evacuating}", ct);

            await ExecuteAsync(
                $"""
                CREATE TABLE {partition.Name}
                    PARTITION OF modbot_event
                    FOR VALUES FROM ('{lower}') TO ('{upper}')
                """,
                ct);

            await ExecuteAsync(
                $"INSERT INTO modbot_event SELECT * FROM {evacuating} WHERE type = ANY(@types)",
                ct,
                new NpgsqlParameter("types", keptTypes));

            // The expired rows die here, with the table, and not one at a time.
            await ExecuteAsync($"DROP TABLE {evacuating}", ct);

            if (transaction is not null)
                await transaction.CommitAsync(ct);
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    /// <summary>
    /// Writes the operational record of what was destroyed (spec 5.9.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Through the fact writer like everything else -- there is no second audit system. It is
    /// skipped when nothing happened, because a daily "pruned nothing" entry is the kind of noise
    /// that buries the entries worth reading.
    /// </para>
    /// <para>
    /// The current month's partition is ensured first. Pruning runs on a schedule of its own and
    /// can be the first thing to write a fact after a month rolls over, and a partition drop that
    /// reported failure because its <em>audit entry</em> had nowhere to go would be a confusing
    /// way to lose an afternoon.
    /// </para>
    /// </remarks>
    private async Task RecordAsync(
        IReadOnlyList<string> dropped,
        IReadOnlyList<string> evacuated,
        CancellationToken ct)
    {
        if (dropped.Count == 0 && evacuated.Count == 0)
            return;

        await _partitions.EnsureForAsync(_clock.UtcNow, ct);

        await _facts.WriteAsync(
            new FactRecord
            {
                Type = FactType.RetentionPruned,
                OccurredAt = _clock.UtcNow,
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = "modbot_event",
                Source = FactSource.Modbot,
                Data = new System.Text.Json.Nodes.JsonObject
                {
                    ["dropped"] = string.Join(",", dropped),
                    ["evacuated"] = string.Join(",", evacuated),
                },
            },
            ct);
    }

    private static string Bound(DateTimeOffset instant)
        => instant.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "+00";

    // EF1002 is knowingly suppressed below. Identifiers and partition bounds cannot be parameters
    // in DDL, so these statements are assembled as text -- from partition names that matched
    // MonthlyPartitionName() and from bounds formatted out of a DateTimeOffset. Every value that
    // came from data travels as an NpgsqlParameter.

    private async Task<int> ExecuteAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
#pragma warning disable EF1002
        return await _db.Database.ExecuteSqlRawAsync(sql, parameters, ct);
#pragma warning restore EF1002
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        IReadOnlyList<NpgsqlParameter> parameters,
        CancellationToken ct)
    {
#pragma warning disable EF1002
        return await _db.Database.SqlQueryRaw<T>(sql, parameters.ToArray()).ToListAsync(ct);
#pragma warning restore EF1002
    }

    [GeneratedRegex(@"^modbot_event_(?<year>\d{4})_(?<month>\d{2})$")]
    private static partial Regex MonthlyPartitionName();

    private sealed record Partition(string Name, DateTimeOffset LowerBound, DateTimeOffset UpperBound);
}

/// <param name="Dropped">Partitions destroyed outright: everything in them was past retention.</param>
/// <param name="Evacuated">
/// Partitions rebuilt without their expired classes, because something in them was still in
/// retention.
/// </param>
public sealed record RetentionResult(IReadOnlyList<string> Dropped, IReadOnlyList<string> Evacuated);
