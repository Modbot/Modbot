using System.Security.Cryptography;

namespace Modbot.Evidence.Storage;

/// <summary>
/// Thrown the instant an upload passes its cap, with the bytes still in flight.
/// </summary>
public sealed class EvidenceTooLargeException : Exception
{
    public EvidenceTooLargeException(long maxBytes)
        : base($"The upload exceeded the {maxBytes:N0}-byte limit and was abandoned mid-transfer.")
        => MaxBytes = maxBytes;

    public EvidenceTooLargeException(string message) : base(message) { }

    public EvidenceTooLargeException(string message, Exception innerException)
        : base(message, innerException) { }

    public EvidenceTooLargeException() : base("The upload exceeded its limit.") { }

    public long MaxBytes { get; }
}

/// <summary>
/// A read-only pass-through that hashes and counts as bytes go past, and stops dead at the cap.
/// </summary>
/// <remarks>
/// <para>
/// This is design section 9.3's second check, and it is the only one that cannot be lied to.
/// The first check rejects a declared size before a byte moves; the third re-checks what was
/// actually stored. In between, <c>Content-Length</c> is a claim the client made and a chunked
/// body makes no claim at all — so the cap has to be enforced against bytes that have genuinely
/// been seen.
/// </para>
/// <para>
/// <strong>Never buffer and then check.</strong> Reading a hundred megabytes into memory to
/// discover it was too big is the failure this shape exists to avoid, and it is the shape a
/// hostile uploader is looking for. The count is compared after every read, and the read that
/// crosses the line throws rather than returning.
/// </para>
/// </remarks>
public sealed class CountingHashStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly long _maxBytes;
    private readonly bool _leaveOpen;

    private byte[]? _digest;

    /// <param name="inner">The source. Read forwards, once.</param>
    /// <param name="maxBytes">The cap. Zero or less means no cap, which callers should not do.</param>
    /// <param name="leaveOpen">Whether disposing this disposes the source.</param>
    public CountingHashStream(Stream inner, long maxBytes, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        _maxBytes = maxBytes;
        _leaveOpen = leaveOpen;
    }

    /// <summary>How many bytes have gone past so far.</summary>
    public long BytesRead { get; private set; }

    /// <summary>
    /// The digest of everything read. Valid only once the source has been read to its end.
    /// </summary>
    public EvidenceHash Hash => EvidenceHash.FromBytes(_digest ??= _hash.GetHashAndReset());

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        Account(buffer[..read]);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var read = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        Account(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void Account(ReadOnlySpan<byte> read)
    {
        if (read.Length == 0)
            return;

        BytesRead += read.Length;
        _hash.AppendData(read);

        // Checked after the read rather than before, because a source is free to hand back fewer
        // bytes than asked for and a cap that trusted the request size would be enforcing the
        // wrong number.
        if (_maxBytes > 0 && BytesRead > _maxBytes)
            throw new EvidenceTooLargeException(_maxBytes);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
            if (!_leaveOpen)
                _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
