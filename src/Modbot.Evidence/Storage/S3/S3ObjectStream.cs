using Amazon.S3.Model;

namespace Modbot.Evidence.Storage.S3;

/// <summary>
/// Hands the caller the object's bytes while keeping the S3 response alive behind them.
/// </summary>
/// <remarks>
/// The response owns the HTTP connection its stream reads from, so returning the bare stream and
/// disposing the response would close the download halfway through a video.
/// </remarks>
internal sealed class S3ObjectStream : Stream
{
    private readonly GetObjectResponse _response;
    private readonly Stream _inner;

    public S3ObjectStream(GetObjectResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        _response = response;
        _inner = response.ResponseStream;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _response.ContentLength;

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => _inner.ReadAsync(buffer, ct);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => _inner.ReadAsync(buffer, offset, count, ct);

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _response.Dispose();

        base.Dispose(disposing);
    }
}
