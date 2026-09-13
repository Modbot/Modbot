using Npgsql;

namespace Modbot.Evidence.Storage.Database;

/// <summary>
/// Creates the three tables the in-database backend needs, idempotently, at runtime.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This should eventually be a migration.</strong> It is not one today for the same reason
/// <c>EventPartitionMaintainer</c> is not: the schema objects belong to a subsystem that is
/// selected at runtime and may never be selected at all, and adding tables to every deployment's
/// database for a backend design section 4.3 actively discourages would be paying the cost
/// everywhere for a choice a minority makes. The precedent is already set — Modbot has one other
/// place that manages its own schema objects with idempotent DDL under an advisory lock — and this
/// follows it deliberately rather than inventing a second pattern.
/// </para>
/// <para>
/// The advisory lock matters for the same reason it does there: <c>CREATE TABLE IF NOT EXISTS</c>
/// still races two instances into a duplicate-table error, and a Modbot that crash-loops on
/// startup because two replicas booted together is a miserable thing to debug.
/// </para>
/// </remarks>
internal static class DatabaseEvidenceSchema
{
    public const string ChunkTable = "modbot_evidence_chunk";
    public const string StagingTable = "modbot_evidence_staging";
    public const string StoreMarkerTable = "modbot_evidence_store";

    /// <summary>"MOD" "EVID" — distinct from the fact log's partition maintenance lock.</summary>
    private const long SchemaLockKey = 0x4D4F44_45564944;

    /// <summary>
    /// The DDL. Fixed text: nothing here is interpolated and nothing is caller-supplied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SET STORAGE EXTERNAL</c> is TOAST without compression, and it is a deliberate choice
    /// rather than a default. Compression buys almost nothing on media that is already compressed —
    /// a PNG, a JPEG and an MP4 are all compressed streams — and it makes a range read decompress
    /// the whole value, which breaks video seeking (design section 10.5). Paying nothing for
    /// compression that costs seeking is a bad trade twice over.
    /// </para>
    /// <para>
    /// Chunking is what keeps any single row from approaching PostgreSQL's 1 GB limit on a value,
    /// and it is also what makes a range read possible at all.
    /// </para>
    /// </remarks>
    private const string Ddl = $"""
        CREATE TABLE IF NOT EXISTS {ChunkTable} (
            hash    bytea not null,
            ordinal int   not null,
            bytes   bytea not null,
            PRIMARY KEY (hash, ordinal)
        );

        ALTER TABLE {ChunkTable} ALTER COLUMN bytes SET STORAGE EXTERNAL;

        CREATE TABLE IF NOT EXISTS {StagingTable} (
            upload_id text  not null,
            ordinal   int   not null,
            bytes     bytea not null,
            PRIMARY KEY (upload_id, ordinal)
        );

        ALTER TABLE {StagingTable} ALTER COLUMN bytes SET STORAGE EXTERNAL;

        CREATE TABLE IF NOT EXISTS {StoreMarkerTable} (
            id      int   not null PRIMARY KEY,
            payload bytea not null,
            CONSTRAINT ck_modbot_evidence_store_singleton CHECK (id = 1)
        );
        """;

    public static async Task EnsureAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var locking = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
        {
            locking.Parameters.AddWithValue("key", SchemaLockKey);
            await locking.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var ddl = new NpgsqlCommand(Ddl, connection, transaction))
            await ddl.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }
}
