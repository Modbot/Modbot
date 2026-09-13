using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Rollups;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Npgsql;

namespace Modbot.Analytics.Retention;

/// <summary>
/// Erases every fact about one subject, on request.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.5. Presence data -- who was where, for how long, wearing what -- is genuinely personal
/// information even though the group could have watched it happen, and the defensible posture is
/// that it lives on the group's own server, retention is configurable, and <em>deletion works</em>.
/// This is the part that has to actually work.
/// </para>
/// <para>
/// <strong>This is the one place a DELETE is right.</strong> Retention prunes by dropping
/// partitions because it removes everything below a date; a purge removes one person's rows from
/// every partition at once, and there is no partitioning scheme that makes that a DROP. It is also
/// rare and small: one person's facts, on request.
/// </para>
/// </remarks>
public interface IUserPurger
{
    Task<PurgeResult> PurgeAsync(FactPlatform platform, string subjectId, CancellationToken ct = default);
}

/// <param name="FactsDeleted">Facts erased.</param>
/// <param name="CountedRollupsDeleted">
/// Counted-only rollup rows erased -- the per-user aggregates that have no fact behind them and so
/// would otherwise survive the purge (spec 5.2.1).
/// </param>
/// <param name="DaysRecomputed">Rollup days rebuilt so that no aggregate still includes the facts.</param>
public sealed record PurgeResult(int FactsDeleted, int CountedRollupsDeleted, int DaysRecomputed);

/// <inheritdoc />
public sealed class UserPurger : IUserPurger
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly RollupJob _rollups;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;

    public UserPurger(
        ModbotContext db,
        IModbotClock clock,
        RollupJob rollups,
        IFactWriter facts,
        EventPartitionMaintainer partitions)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(rollups);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);

        _db = db;
        _clock = clock;
        _rollups = rollups;
        _facts = facts;
        _partitions = partitions;
    }

    /// <summary>
    /// Erases the subject's facts, the per-user counts that only exist as aggregates, and rebuilds
    /// the rollups for the days involved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Facts where this person was the <em>actor</em> are kept.</strong> A moderator's ban
    /// is a record about the person who was banned, and erasing it on the moderator's request
    /// would delete someone else's moderation history and destroy the accountability spec 5.8 is
    /// built on. A moderator leaving the group is not grounds to unwrite what they did while they
    /// were in it.
    /// </para>
    /// <para>
    /// Computed rollups are recomputed rather than deleted. What survives is only what the
    /// surviving facts still say, which is the invariant (spec 5.2) -- deleting derived rows that
    /// a rebuild would put straight back would be theatre. Counted-only rows dimensioned on this
    /// person <em>are</em> deleted: nothing can recompute them, so they are the only copy, and for
    /// a per-user message count that copy is exactly the personal data being erased.
    /// </para>
    /// <para>
    /// One transaction. A purge that deleted the facts and then failed to recompute would leave
    /// aggregates that still counted them, which is the failure that turns "deletion works" into a
    /// claim nobody should have made.
    /// </para>
    /// </remarks>
    public async Task<PurgeResult> PurgeAsync(
        FactPlatform platform,
        string subjectId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectId);

        var ownsTransaction = _db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await _db.Database.BeginTransactionAsync(ct) : null;

        try
        {
            // Collected before the delete: afterwards there is nothing left to ask.
            var days = await _rollups.DaysTouchedBySubjectAsync(platform, subjectId, ct);

            var factsDeleted = await ExecuteAsync(
                """
                DELETE FROM modbot_event
                WHERE subject_platform = @platform AND subject_id = @subject
                """,
                ct,
                new NpgsqlParameter("platform", (short)platform),
                new NpgsqlParameter("subject", subjectId));

            var rollupsDeleted = await ExecuteAsync(
                """
                DELETE FROM modbot_rollup_daily
                WHERE origin = @origin AND dimension = ANY(@dimensions)
                """,
                ct,
                new NpgsqlParameter("origin", (short)RollupOrigin.Counted),
                new NpgsqlParameter("dimensions", Dimensions(platform, subjectId)));

            if (days.Count > 0)
                await _rollups.RecomputeDaysAsync(days, ct);

            await RecordAsync(factsDeleted, rollupsDeleted, days.Count, ct);

            if (transaction is not null)
                await transaction.CommitAsync(ct);

            return new PurgeResult(factsDeleted, rollupsDeleted, days.Count);
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    /// <summary>
    /// The dimension strings a per-user rollup might have been written under.
    /// </summary>
    /// <remarks>
    /// The platform-qualified form is what the rollup job produces and what callers of
    /// <see cref="IRollupCounter"/> should use, so that a Discord snowflake and a VRChat id cannot
    /// merge into one person. The bare id is included because a counter somewhere may have written
    /// one, and a purge that missed rows on a formatting technicality would be the worst possible
    /// place to be strict.
    /// </remarks>
    private static string[] Dimensions(FactPlatform platform, string subjectId)
        => [RollupDimensions.ForUser(platform, subjectId), subjectId];

    /// <summary>
    /// Records that a purge happened -- and deliberately not who it was about.
    /// </summary>
    /// <remarks>
    /// An erasure log that names the erased person is not an erasure. What is worth keeping is
    /// that data was destroyed and how much, which is what makes the deletion auditable without
    /// undoing it.
    /// </remarks>
    private async Task RecordAsync(int facts, int rollups, int days, CancellationToken ct)
    {
        await _partitions.EnsureForAsync(_clock.UtcNow, ct);

        await _facts.WriteAsync(
            new FactRecord
            {
                Type = FactType.UserPurged,
                OccurredAt = _clock.UtcNow,
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = "purge",
                Source = FactSource.Modbot,
                Data = new JsonObject
                {
                    ["facts"] = facts,
                    ["countedRollups"] = rollups,
                    ["daysRecomputed"] = days,
                },
            },
            ct);
    }

    private async Task<int> ExecuteAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
#pragma warning disable EF1002 // Constant SQL; every value is a parameter.
        return await _db.Database.ExecuteSqlRawAsync(sql, parameters, ct);
#pragma warning restore EF1002
    }
}
