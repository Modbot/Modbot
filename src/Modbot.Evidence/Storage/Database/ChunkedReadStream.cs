using Npgsql;
using NpgsqlTypes;

namespace Modbot.Evidence.Storage.Database;

/// <summary>
/// Reads an object back out of <c>modbot_evidence_chunk</c>, one row at a time.
/// </summary>
/// <remarks>
/// <para>
/// A connection is taken from the pool for each chunk and given straight back, rather than held
/// open for the length of the read. A moderator scrubbing through a twenty-minute clip on a slow
/// connection would otherwise hold a database connection for minutes — and the connection pool is
/// shared with everything else Modbot does, so the evidence backend nobody recommends would be
/// throttling the fact log that everybody depends on.
/// </para>
/// <para>
/// The chunk index — ordinals and their lengths — is read once up front, which is what lets a
/// range request start at the right row instead of reading from the beginning.
/// </para>
/// </remarks>
internal sealed class ChunkedReadStream : Stream
{
    private readonly IEvidenceConnectionFactory _connections;
    private readonly byte[] _hash;
    private readonly IReadOnlyList<ChunkSpan> _chunks;
    private readonly long _end;

    private long _position;
    private int _chunkIndex;
    private byte[]? _buffer;
    private int _bufferOffset;
    private long _bufferStart;

    public ChunkedReadStream(
        IEvidenceConnectionFactory connections,
        byte[] hash,
        IReadOnlyList<ChunkSpan> chunks,
        long start,
        long length)
    {
        _connections = connections;
        _hash = hash;
        _chunks = chunks;
        _position = start;
        _end = start + length;

        // Skip whole chunks that fall entirely before the requested range.
        while (_chunkIndex < _chunks.Count && _chunks[_chunkIndex].End <= start)
            _chunkIndex++;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _end;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_position >= _end || buffer.Length == 0)
            return 0;

        if (_buffer is null || _bufferOffset >= _buffer.Length)
        {
            if (_chunkIndex >= _chunks.Count)
                return 0;

            var chunk = _chunks[_chunkIndex++];
            _buffer = await FetchAsync(chunk.Ordinal, ct).ConfigureAwait(false);
            _bufferStart = chunk.Start;

            // The first chunk of a range usually starts mid-row.
            _bufferOffset = (int)Math.Max(0, _position - _bufferStart);

            if (_bufferOffset >= _buffer.Length)
                return await ReadAsync(buffer, ct).ConfigureAwait(false);
        }

        var available = _buffer.Length - _bufferOffset;
        var wanted = (int)Math.Min(Math.Min(buffer.Length, available), _end - _position);

        _buffer.AsSpan(_bufferOffset, wanted).CopyTo(buffer.Span);
        _bufferOffset += wanted;
        _position += wanted;

        return wanted;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private async Task<byte[]> FetchAsync(int ordinal, CancellationToken ct)
    {
        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            $"SELECT bytes FROM {DatabaseEvidenceSchema.ChunkTable} WHERE hash = @hash AND ordinal = @ordinal",
            connection);

        command.Parameters.AddWithValue("hash", NpgsqlDbType.Bytea, _hash);
        command.Parameters.AddWithValue("ordinal", ordinal);

        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        return value as byte[]
               ?? throw new IOException($"Chunk {ordinal} disappeared while the object was being read.");
    }
}

/// <param name="Ordinal">The row.</param>
/// <param name="Start">Where this chunk begins in the whole object.</param>
/// <param name="End">Where it ends, exclusive.</param>
internal readonly record struct ChunkSpan(int Ordinal, long Start, long End);
