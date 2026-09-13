using Modbot.Evidence.Options;
using Npgsql;
using NpgsqlTypes;

namespace Modbot.Evidence.Storage.Database;

/// <summary>
/// Evidence as chunked rows in PostgreSQL. <strong>Supported, and not recommended</strong>
/// (design section 4.3).
/// </summary>
/// <remarks>
/// <para>
/// The reasons against it are worth having in the type itself, because the type is what somebody
/// reads when they are deciding whether to reach for it:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <strong><c>pg_dump</c> grows by the full evidence volume.</strong> A database that was 240 MB a
/// year becomes a 20 GB dump the first time somebody attaches a dozen clips. Backup windows,
/// restore times and transfer costs all scale with it, and a restore that takes four hours is not
/// a restore anyone can perform during an incident. On managed Postgres the backup size is often
/// also what you are billed for.
/// </description></item>
/// <item><description>
/// <strong>It evicts the working set.</strong> Reading a 100 MB video pulls 100 MB through the
/// connection pool and through <c>shared_buffers</c>, displacing the fact-log pages that make
/// subject-profile queries fast. The cost of the evidence backend is paid by analytics that have
/// nothing to do with it.
/// </description></item>
/// <item><description>
/// <strong>WAL amplification.</strong> Every byte is written to the WAL and then to the heap,
/// shipped to any replica, and retained by any PITR window — stored three times over.
/// </description></item>
/// <item><description>
/// <strong>A single value cannot exceed 1 GB</strong>, and a <c>bytea</c> that large is unworkable
/// long before it is illegal. Hence chunking, which is exactly the complexity object storage
/// exists to absorb.
/// </description></item>
/// </list>
/// <para>
/// Its one genuine virtue, which is not nothing: a single backup covers everything. No second
/// credential, no second service, no second thing to forget when handing the deployment to the
/// next volunteer. For a small group with a handful of screenshots and no appetite for object
/// storage that is a defensible trade — and refusing to support it would push those operators onto
/// the filesystem backend without a volume, which is strictly worse.
/// </para>
/// </remarks>
public sealed class DatabaseEvidenceStore : IEvidenceStore
{
    private readonly IEvidenceConnectionFactory _connections;
    private readonly int _chunkBytes;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);

    private bool _schemaReady;

    public DatabaseEvidenceStore(IEvidenceConnectionFactory connections, DatabaseEvidenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);

        if (options.ChunkBytes < 4096)
            throw new ArgumentException("A chunk smaller than 4 KiB is all overhead.", nameof(options));

        _connections = connections;
        _chunkBytes = options.ChunkBytes;
    }

    /// <summary>
    /// Range reads are the whole reason for chunking. There is no presigning: the browser cannot
    /// be handed a URL to a table.
    /// </summary>
    public EvidenceStoreCapabilities Capabilities
        => EvidenceStoreCapabilities.RangeRead | EvidenceStoreCapabilities.ServerSideCopy;

    public string Description => "the Modbot database";

    public async Task<StagedObject> StageAsync(
        EvidenceUploadId uploadId,
        Stream body,
        long maxBytes,
        long? declaredLength = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // A retry of the same upload replaces its staging rows rather than appending to them.
        await ExecuteAsync(
            connection, transaction,
            $"DELETE FROM {DatabaseEvidenceSchema.StagingTable} WHERE upload_id = @upload",
            command => command.Parameters.AddWithValue("upload", uploadId.Value),
            ct).ConfigureAwait(false);

        await using var counting = new CountingHashStream(body, maxBytes);

        var buffer = new byte[_chunkBytes];
        var ordinal = 0;

        while (true)
        {
            var read = await counting.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct)
                .ConfigureAwait(false);

            if (read == 0)
                break;

            var chunk = read == buffer.Length ? buffer : buffer[..read];

            await ExecuteAsync(
                connection, transaction,
                $"""
                 INSERT INTO {DatabaseEvidenceSchema.StagingTable} (upload_id, ordinal, bytes)
                 VALUES (@upload, @ordinal, @bytes)
                 """,
                command =>
                {
                    command.Parameters.AddWithValue("upload", uploadId.Value);
                    command.Parameters.AddWithValue("ordinal", ordinal);
                    command.Parameters.AddWithValue("bytes", NpgsqlDbType.Bytea, chunk);
                },
                ct).ConfigureAwait(false);

            ordinal++;

            if (read < buffer.Length)
                break;
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return new StagedObject(counting.Hash, counting.BytesRead);
    }

    public async Task<CommitOutcome> CommitAsync(
        EvidenceUploadId uploadId, EvidenceHash hash, CancellationToken ct = default)
    {
        var digest = hash.ToArray();

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var present = await ScalarAsync<bool>(
            connection, transaction,
            $"SELECT EXISTS (SELECT 1 FROM {DatabaseEvidenceSchema.ChunkTable} WHERE hash = @hash)",
            command => command.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, digest),
            ct).ConfigureAwait(false);

        if (present)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return CommitOutcome.AlreadyPresent;
        }

        // Server-side in the only sense available here, and the important one: the bytes move
        // inside PostgreSQL and never travel to the application and back.
        var moved = await ExecuteAsync(
            connection, transaction,
            $"""
             INSERT INTO {DatabaseEvidenceSchema.ChunkTable} (hash, ordinal, bytes)
             SELECT @hash, ordinal, bytes
             FROM {DatabaseEvidenceSchema.StagingTable}
             WHERE upload_id = @upload
             ON CONFLICT (hash, ordinal) DO NOTHING
             """,
            command =>
            {
                command.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, digest);
                command.Parameters.AddWithValue("upload", uploadId.Value);
            },
            ct).ConfigureAwait(false);

        if (moved == 0)
            throw new EvidenceStagingNotFoundException($"Nothing staged under upload {uploadId}.");

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return CommitOutcome.Created;
    }

    public async Task<Stream?> OpenReadAsync(
        EvidenceHash hash, ByteRange? range = null, CancellationToken ct = default)
    {
        var digest = hash.ToArray();
        var chunks = await IndexAsync(
            DatabaseEvidenceSchema.ChunkTable, "hash", NpgsqlDbType.Bytea, digest, ct).ConfigureAwait(false);

        if (chunks.Count == 0)
            return null;

        var total = chunks[^1].End;
        var start = range?.First ?? 0;

        if (start >= total)
            return new MemoryStream([], writable: false);

        var length = range?.Length is { } wanted ? Math.Min(wanted, total - start) : total - start;

        return new ChunkedReadStream(_connections, digest, chunks, start, length);
    }

    public async Task<Stream?> OpenStagedAsync(EvidenceUploadId uploadId, CancellationToken ct = default)
    {
        // Staging rows are keyed by upload id rather than hash, so they get their own tiny reader
        // rather than sharing the range-capable one — nothing ever range-reads a staged object.
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            $"""
             SELECT bytes FROM {DatabaseEvidenceSchema.StagingTable}
             WHERE upload_id = @upload
             ORDER BY ordinal
             """,
            connection);

        command.Parameters.AddWithValue("upload", uploadId.Value);

        var assembled = new MemoryStream();
        var any = false;

        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                any = true;
                var chunk = await reader.GetFieldValueAsync<byte[]>(0, ct).ConfigureAwait(false);
                await assembled.WriteAsync(chunk, ct).ConfigureAwait(false);
            }
        }

        if (!any)
        {
            await assembled.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        assembled.Position = 0;
        return assembled;
    }

    public async Task<ObjectStat?> StatAsync(EvidenceHash hash, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            $"""
             SELECT COUNT(*)::bigint, COALESCE(SUM(LENGTH(bytes)), 0)::bigint
             FROM {DatabaseEvidenceSchema.ChunkTable}
             WHERE hash = @hash
             """,
            connection);

        command.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, hash.ToArray());

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var rows = reader.GetInt64(0);
        return rows == 0 ? null : new ObjectStat(reader.GetInt64(1));
    }

    public async Task DeleteAsync(EvidenceHash hash, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        await ExecuteAsync(
            connection, null,
            $"DELETE FROM {DatabaseEvidenceSchema.ChunkTable} WHERE hash = @hash",
            command => command.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, hash.ToArray()),
            ct).ConfigureAwait(false);
    }

    public async Task DeleteStagedAsync(EvidenceUploadId uploadId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        await ExecuteAsync(
            connection, null,
            $"DELETE FROM {DatabaseEvidenceSchema.StagingTable} WHERE upload_id = @upload",
            command => command.Parameters.AddWithValue("upload", uploadId.Value),
            ct).ConfigureAwait(false);
    }

    /// <summary>Always null: this store does not declare the capability.</summary>
    public Task<Uri?> TryCreatePresignedReadAsync(
        EvidenceHash hash, TimeSpan ttl, PresignedReadOptions? options = null, CancellationToken ct = default)
        => Task.FromResult<Uri?>(null);

    /// <summary>Always null: this store does not declare the capability.</summary>
    public Task<Uri?> TryCreatePresignedWriteAsync(
        EvidenceUploadId uploadId, TimeSpan ttl, string? contentType = null, CancellationToken ct = default)
        => Task.FromResult<Uri?>(null);

    public async Task WriteStoreMarkerAsync(StoreMarker marker, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(marker);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        await ExecuteAsync(
            connection, null,
            $"""
             INSERT INTO {DatabaseEvidenceSchema.StoreMarkerTable} (id, payload)
             VALUES (1, @payload)
             ON CONFLICT (id) DO UPDATE SET payload = EXCLUDED.payload
             """,
            command => command.Parameters.AddWithValue("payload", NpgsqlDbType.Bytea, marker.Serialise()),
            ct).ConfigureAwait(false);
    }

    public async Task<StoreProbe> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);

            await using var command = new NpgsqlCommand(
                $"SELECT payload FROM {DatabaseEvidenceSchema.StoreMarkerTable} WHERE id = 1", connection);

            if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not byte[] payload)
                return StoreProbe.Absent(Description);

            var marker = StoreMarker.TryParse(payload);
            return marker is null ? StoreProbe.Malformed(Description) : StoreProbe.Present(marker, Description);
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException or IOException)
        {
            return StoreProbe.Unreachable(Description, e);
        }
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);

        if (_schemaReady)
            return connection;

        await _schemaGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_schemaReady)
            {
                await DatabaseEvidenceSchema.EnsureAsync(connection, ct).ConfigureAwait(false);
                _schemaReady = true;
            }
        }
        finally
        {
            _schemaGate.Release();
        }

        return connection;
    }

    private async Task<IReadOnlyList<ChunkSpan>> IndexAsync(
        string table, string keyColumn, NpgsqlDbType keyType, object key, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            $"SELECT ordinal, LENGTH(bytes) FROM {table} WHERE {keyColumn} = @key ORDER BY ordinal",
            connection);

        command.Parameters.AddWithValue("key", keyType, key);

        var spans = new List<ChunkSpan>();
        long offset = 0;

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var length = reader.GetInt32(1);
            spans.Add(new ChunkSpan(reader.GetInt32(0), offset, offset + length));
            offset += length;
        }

        return spans;
    }

    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        Action<NpgsqlCommand> parameterise,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        parameterise(command);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        Action<NpgsqlCommand> parameterise,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        parameterise(command);

        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return (T)value!;
    }
}
