using Modbot.Evidence.Storage;
using Modbot.Evidence.Tests.Fakes;

namespace Modbot.Evidence.Tests.Storage;

/// <summary>
/// Design section 9.3's second check: the cap is enforced while the bytes stream, never after
/// buffering them.
/// </summary>
public class CountingHashStreamTests
{
    [Fact]
    public async Task ItHashesAndCountsWhatPassedThrough()
    {
        var content = SampleMedia.Png(4096);

        await using var counting = new CountingHashStream(new MemoryStream(content), content.Length);
        await counting.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken);

        Assert.Equal(content.Length, counting.BytesRead);
        Assert.Equal(EvidenceHash.Compute(content), counting.Hash);
    }

    /// <summary>
    /// The important property: it stops <em>during</em> the read, so the destination never receives
    /// more than the cap and nothing has been held in memory to discover it afterwards.
    /// </summary>
    [Fact]
    public async Task ItAbortsTheMomentTheCapIsPassed()
    {
        var content = SampleMedia.Png(64 * 1024);
        var sink = new MemoryStream();

        await using var counting = new CountingHashStream(new MemoryStream(content), 8 * 1024);

        await Assert.ThrowsAsync<EvidenceTooLargeException>(
            () => counting.CopyToAsync(sink, 4096, TestContext.Current.CancellationToken));

        Assert.True(sink.Length <= 8 * 1024 + 4096, $"{sink.Length} bytes reached the destination.");
        Assert.True(counting.BytesRead <= 8 * 1024 + 4096);
    }

    [Fact]
    public async Task ACapEqualToTheContentIsNotExceeded()
    {
        var content = SampleMedia.Jpeg(1000);

        await using var counting = new CountingHashStream(new MemoryStream(content), 1000);
        await counting.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken);

        Assert.Equal(1000, counting.BytesRead);
    }

    [Fact]
    public async Task OneByteOverTheCapIsRefused()
    {
        var content = SampleMedia.Jpeg(1001);

        await using var counting = new CountingHashStream(new MemoryStream(content), 1000);

        await Assert.ThrowsAsync<EvidenceTooLargeException>(
            () => counting.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A source that hands back fewer bytes than were asked for — every network stream — must not
    /// be counted by the request size.
    /// </summary>
    [Fact]
    public async Task ADribblingSourceIsCountedByWhatItActuallyGave()
    {
        var content = SampleMedia.Gif(5000);

        await using var counting = new CountingHashStream(new DribblingStream(content), 10_000);
        await counting.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken);

        Assert.Equal(content.Length, counting.BytesRead);
        Assert.Equal(EvidenceHash.Compute(content), counting.Hash);
    }

    /// <summary>Hands back at most seven bytes a time, like a slow connection.</summary>
    private sealed class DribblingStream : Stream
    {
        private readonly byte[] _content;
        private int _position;

        public DribblingStream(byte[] content) => _content = content;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var take = Math.Min(Math.Min(7, buffer.Length), _content.Length - _position);
            _content.AsSpan(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => ValueTask.FromResult(Read(buffer.Span));

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
