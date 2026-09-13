namespace Modbot.Evidence.Storage;

/// <summary>
/// Stops a read after a fixed number of bytes, so a range request cannot read past its range.
/// </summary>
/// <remarks>
/// Wrapped around a seeked file handle rather than copying the slice out, because a range read of
/// a twenty-minute video exists precisely so that nobody has to materialise the rest of it.
/// </remarks>
internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private long _remaining;

    public BoundedReadStream(Stream inner, long length)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        _remaining = length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_remaining <= 0)
            return 0;

        var read = _inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
        _remaining -= read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_remaining <= 0)
            return 0;

        var read = await _inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], ct)
            .ConfigureAwait(false);

        _remaining -= read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
