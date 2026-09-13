using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Storage.Database;
using Modbot.Evidence.Tests.Fakes;
using Modbot.TestSupport;
using Npgsql;

namespace Modbot.Evidence.Tests.Storage;

/// <summary>
/// The conformance suite against real PostgreSQL, plus the chunking that only this backend has.
/// </summary>
/// <remarks>
/// Real Postgres rather than a substitute provider, because what is being tested is <c>bytea</c>
/// storage, <c>SET STORAGE EXTERNAL</c>, and idempotent DDL under an advisory lock — none of which
/// an in-memory provider implements.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class DatabaseStoreTests : EvidenceStoreConformanceTests
{
    /// <summary>
    /// Deliberately tiny. The production default is about 1 MiB, but a chunk smaller than the test
    /// files is what actually exercises multi-row objects and ranges that cross a row boundary.
    /// </summary>
    private const int TestChunkBytes = 8192;

    private readonly PostgresFixture _db;

    private string _connectionString = string.Empty;

    public DatabaseStoreTests(PostgresFixture db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    protected override async Task<IEvidenceStore> CreateStoreAsync(CancellationToken ct)
    {
        var name = $"modbot_evidence_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(_db.ConnectionString))
        {
            await admin.OpenAsync(ct);

            // A fresh GUID with a fixed prefix; nothing here is caller-supplied.
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync(ct);
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_db.ConnectionString) { Database = name }
            .ConnectionString;

        if (_connectionString.Length == 0)
            _connectionString = connectionString;

        return StoreFor(connectionString);
    }

    [Fact]
    public void ItDeclaresNoPresigning()
    {
        Assert.False(Store.Capabilities.HasFlag(EvidenceStoreCapabilities.PresignedRead));
        Assert.False(Store.Capabilities.HasFlag(EvidenceStoreCapabilities.PresignedWrite));
    }

    /// <summary>
    /// Chunking is what keeps any single value away from PostgreSQL's 1 GB limit, and what makes a
    /// range read possible at all.
    /// </summary>
    [Fact]
    public async Task AnObjectIsSplitAcrossRowsAndNoRowExceedsTheChunkSize()
    {
        var content = SampleMedia.Mp4(size: TestChunkBytes * 3 + 17);
        var (uploadId, staged) = await StageAsync(content);
        await Store.CommitAsync(uploadId, staged.Hash, Ct);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Ct);

        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*), MAX(LENGTH(bytes)), SUM(LENGTH(bytes)) FROM modbot_evidence_chunk WHERE hash = @hash",
            connection);

        command.Parameters.AddWithValue("hash", staged.Hash.ToArray());

        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));

        Assert.Equal(4, reader.GetInt64(0));
        Assert.Equal(TestChunkBytes, reader.GetInt32(1));
        Assert.Equal(content.Length, reader.GetInt64(2));
    }

    /// <summary>
    /// TOAST without compression. Compression buys nothing on already-compressed media and makes a
    /// range read decompress the whole value, which is exactly what breaks video seeking.
    /// </summary>
    [Fact]
    public async Task TheBytesColumnIsStoredExternalRatherThanCompressed()
    {
        // Touch the store so the schema exists.
        await Store.ProbeAsync(Ct);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Ct);

        await using var command = new NpgsqlCommand(
            """
            SELECT a.attstorage
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            WHERE c.relname = 'modbot_evidence_chunk' AND a.attname = 'bytes'
            """,
            connection);

        Assert.Equal('e', (char)(await command.ExecuteScalarAsync(Ct))!);
    }

    /// <summary>
    /// The interesting range: one that begins inside one row and ends inside another.
    /// </summary>
    [Fact]
    public async Task ARangeSpanningAChunkBoundaryIsAssembledCorrectly()
    {
        var content = SampleMedia.Webm(TestChunkBytes * 2 + 500);
        var (uploadId, staged) = await StageAsync(content);
        await Store.CommitAsync(uploadId, staged.Hash, Ct);

        var first = TestChunkBytes - 100;
        var last = TestChunkBytes + 199;

        await using var slice = await Store.OpenReadAsync(staged.Hash, new ByteRange(first, last), Ct);
        Assert.NotNull(slice);
        Assert.Equal(content[first..(last + 1)], await DrainAsync(slice));
    }

    /// <summary>
    /// The DDL runs on every store instance against the same database, so it has to be safe to run
    /// twice and safe to run at once. Two replicas booting together is normal, and a Modbot that
    /// crash-loops because both tried to create the same table would be a miserable thing to debug.
    /// </summary>
    [Fact]
    public async Task TheSchemaCanBeEnsuredConcurrentlyOnOneDatabase()
    {
        var racers = Enumerable.Range(0, 4).Select(_ => StoreFor(_connectionString)).ToList();

        await Task.WhenAll(racers.Select(store => store.ProbeAsync(Ct)));

        foreach (var store in racers)
            Assert.Equal(StoreProbeOutcome.Absent, (await store.ProbeAsync(Ct)).Outcome);
    }

    private static IEvidenceStore StoreFor(string connectionString)
        => new DatabaseEvidenceStore(
            new ConnectionStringEvidenceConnectionFactory(connectionString),
            new DatabaseEvidenceOptions { ChunkBytes = TestChunkBytes });
}
