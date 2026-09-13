using Modbot.Evidence.Storage;
using Modbot.Evidence.Tests.Fakes;

namespace Modbot.Evidence.Tests.Storage;

/// <summary>
/// One suite, three backends (design section 13.3).
/// </summary>
/// <remarks>
/// These assert the <em>contract</em> rather than any implementation: content addressing,
/// idempotent writes, the append-only rule, range reads where the capability is declared, the
/// capability biconditional, and the store marker probe's four-valued result — including the transient
/// case, which is the one that is never exercised by accident.
/// </remarks>
public abstract class EvidenceStoreConformanceTests : IAsyncLifetime
{
    private readonly List<IEvidenceStore> _created = [];

    protected IEvidenceStore Store { get; private set; } = null!;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Builds a store that shares nothing with any other this test has made.</summary>
    protected abstract Task<IEvidenceStore> CreateStoreAsync(CancellationToken ct);

    protected virtual ValueTask CleanUpAsync() => ValueTask.CompletedTask;

    public async ValueTask InitializeAsync() => Store = await NewStoreAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _created)
        {
            if (store is IDisposable disposable)
                disposable.Dispose();
        }

        await CleanUpAsync();
        GC.SuppressFinalize(this);
    }

    protected async Task<IEvidenceStore> NewStoreAsync()
    {
        var store = await CreateStoreAsync(Ct);
        _created.Add(store);
        return store;
    }

    [Fact]
    public async Task StagedBytesAreCommittedUnderTheirHash()
    {
        var content = SampleMedia.Png(3000);
        var (uploadId, staged) = await StageAsync(content);

        Assert.Equal(EvidenceHash.Compute(content), staged.Hash);
        Assert.Equal(content.Length, staged.ByteSize);

        Assert.Equal(CommitOutcome.Created, await Store.CommitAsync(uploadId, staged.Hash, Ct));

        var stat = await Store.StatAsync(staged.Hash, Ct);
        Assert.NotNull(stat);
        Assert.Equal(content.Length, stat.ByteSize);
        Assert.Equal(content, await ReadAllAsync(staged.Hash));
    }

    /// <summary>
    /// Design section 6: an existing key is never overwritten. With content addressing this is
    /// nearly tautological, and stating it as a rule is what stops a future "re-upload to fix the
    /// rotation" feature being built on a silent replace.
    /// </summary>
    [Fact]
    public async Task CommittingOntoAnExistingKeyIsANoOpRatherThanAReplacement()
    {
        var content = SampleMedia.Jpeg(2048);
        var (first, staged) = await StageAsync(content);
        await Store.CommitAsync(first, staged.Hash, Ct);

        var (second, again) = await StageAsync(content);
        Assert.Equal(CommitOutcome.AlreadyPresent, await Store.CommitAsync(second, again.Hash, Ct));

        Assert.Equal(content, await ReadAllAsync(staged.Hash));
    }

    /// <summary>
    /// Deduplication: two moderators attaching the same clip to two reports store one object. The
    /// consequence for deletion is refcounting, which lives a layer up.
    /// </summary>
    [Fact]
    public async Task TheSameBytesTwiceProduceOneObject()
    {
        var content = SampleMedia.Webm(4096);

        var (firstUpload, first) = await StageAsync(content);
        var firstOutcome = await Store.CommitAsync(firstUpload, first.Hash, Ct);

        var (secondUpload, second) = await StageAsync(content);
        var secondOutcome = await Store.CommitAsync(secondUpload, second.Hash, Ct);

        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(CommitOutcome.Created, firstOutcome);
        Assert.Equal(CommitOutcome.AlreadyPresent, secondOutcome);
    }

    /// <summary>
    /// Design section 9.1: the staging key is derived from the upload id so a retry overwrites
    /// rather than accumulating — which is what keeps a moderator on a bad connection from leaving
    /// five copies of a video behind.
    /// </summary>
    [Fact]
    public async Task RetryingAnUploadOverwritesItsStagingObject()
    {
        var uploadId = EvidenceUploadId.New();
        var abandoned = SampleMedia.Gif(6000);
        var real = SampleMedia.Png(1500);

        await StageAsync(abandoned, uploadId);
        var staged = await StageAsync(real, uploadId);

        Assert.Equal(EvidenceHash.Compute(real), staged.Item2.Hash);

        await using var reread = await Store.OpenStagedAsync(uploadId, Ct);
        Assert.NotNull(reread);
        Assert.Equal(real, await DrainAsync(reread));
    }

    [Fact]
    public async Task AnObjectThatWasNeverStoredReadsAsNothing()
    {
        var missing = EvidenceHash.Compute(SampleMedia.Noise(64));

        Assert.Null(await Store.StatAsync(missing, Ct));
        Assert.Null(await Store.OpenReadAsync(missing, null, Ct));
    }

    [Fact]
    public async Task CommittingWithNothingStagedSaysSo()
    {
        var hash = EvidenceHash.Compute(SampleMedia.Noise(128));

        await Assert.ThrowsAsync<EvidenceStagingNotFoundException>(
            () => Store.CommitAsync(EvidenceUploadId.New(), hash, Ct));
    }

    [Fact]
    public async Task DeleteRemovesTheObjectAndToleratesOneThatIsAlreadyGone()
    {
        var content = SampleMedia.Mp4(size: 2500);
        var (uploadId, staged) = await StageAsync(content);
        await Store.CommitAsync(uploadId, staged.Hash, Ct);

        await Store.DeleteAsync(staged.Hash, Ct);
        Assert.Null(await Store.StatAsync(staged.Hash, Ct));

        await Store.DeleteAsync(staged.Hash, Ct);
    }

    [Fact]
    public async Task DeletingStagingToleratesAnUploadThatNeverHappened()
        => await Store.DeleteStagedAsync(EvidenceUploadId.New(), Ct);

    /// <summary>
    /// Video seeking is HTTP range requests, and a backend that cannot answer one produces a clip
    /// that plays only from the start.
    /// </summary>
    [Fact]
    public async Task RangeReadsReturnExactlyTheSliceAsked()
    {
        Assert.True(
            Store.Capabilities.HasFlag(EvidenceStoreCapabilities.RangeRead),
            "Every backend declares range reads; a new one that cannot must change this test deliberately.");

        var content = SampleMedia.Mp4(size: 20_000);
        var (uploadId, staged) = await StageAsync(content);
        await Store.CommitAsync(uploadId, staged.Hash, Ct);

        await using (var middle = await Store.OpenReadAsync(staged.Hash, new ByteRange(5_000, 5_999), Ct))
        {
            Assert.NotNull(middle);
            Assert.Equal(content[5_000..6_000], await DrainAsync(middle));
        }

        await using var openEnded = await Store.OpenReadAsync(staged.Hash, new ByteRange(19_500, null), Ct);
        Assert.NotNull(openEnded);
        Assert.Equal(content[19_500..], await DrainAsync(openEnded));
    }

    /// <summary>
    /// A range that starts at the first byte must not be special-cased into a whole-object read.
    /// </summary>
    [Fact]
    public async Task ARangeStartingAtZeroIsStillARange()
    {
        var content = SampleMedia.Webp(9_000);
        var (uploadId, staged) = await StageAsync(content);
        await Store.CommitAsync(uploadId, staged.Hash, Ct);

        await using var slice = await Store.OpenReadAsync(staged.Hash, new ByteRange(0, 99), Ct);
        Assert.NotNull(slice);
        Assert.Equal(content[..100], await DrainAsync(slice));
    }

    /// <summary>
    /// The rule callers rely on: a presign returns non-null if and only if the capability is
    /// declared. Nothing throws, so a deployment cannot discover the difference in production.
    /// </summary>
    [Fact]
    public async Task ThePresignCapabilityIsABiconditional()
    {
        var content = SampleMedia.Png(512);
        var (uploadId, staged) = await StageAsync(content);
        await Store.CommitAsync(uploadId, staged.Hash, Ct);

        var read = await Store.TryCreatePresignedReadAsync(
            staged.Hash, TimeSpan.FromMinutes(5), new PresignedReadOptions("image/png", "shot.png"), Ct);

        var write = await Store.TryCreatePresignedWriteAsync(
            EvidenceUploadId.New(), TimeSpan.FromMinutes(5), "image/png", Ct);

        Assert.Equal(Store.Capabilities.HasFlag(EvidenceStoreCapabilities.PresignedRead), read is not null);
        Assert.Equal(Store.Capabilities.HasFlag(EvidenceStoreCapabilities.PresignedWrite), write is not null);
    }

    [Fact]
    public async Task TheCapIsEnforcedRatherThanCheckedAfterwards()
    {
        var content = SampleMedia.Mp4(size: 40_000);

        await Assert.ThrowsAsync<EvidenceTooLargeException>(
            async () => await StageAsync(content, maxBytes: 8_000));
    }

    /// <summary>
    /// Design section 8.2: absence fires even when the store is empty, which is the only time the
    /// mistake can be fixed for free.
    /// </summary>
    [Fact]
    public async Task AStoreNotYetSetUpProbesAsAbsent()
    {
        var probe = await Store.ProbeAsync(Ct);

        Assert.Equal(StoreProbeOutcome.Absent, probe.Outcome);
        Assert.Null(probe.Marker);
    }

    [Fact]
    public async Task AProbeFindsTheStoreMarkerThatWasWritten()
    {
        var id = Guid.NewGuid();
        await Store.WriteStoreMarkerAsync(new StoreMarker(id, DateTimeOffset.UnixEpoch, "test"), Ct);

        var probe = await Store.ProbeAsync(Ct);

        Assert.Equal(StoreProbeOutcome.Present, probe.Outcome);
        Assert.Equal(id, probe.Marker!.StoreId);
    }

    /// <summary>
    /// Pointing at a store that belongs to a different Modbot is a distinct finding from an empty
    /// one, and both are distinct from a store that did not answer.
    /// </summary>
    [Fact]
    public async Task TwoStoresDoNotShareAStoreMarker()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();

        await Store.WriteStoreMarkerAsync(new StoreMarker(mine, DateTimeOffset.UnixEpoch, "mine"), Ct);

        var other = await NewStoreAsync();
        await other.WriteStoreMarkerAsync(new StoreMarker(theirs, DateTimeOffset.UnixEpoch, "theirs"), Ct);

        Assert.Equal(mine, (await Store.ProbeAsync(Ct)).Marker!.StoreId);
        Assert.Equal(theirs, (await other.ProbeAsync(Ct)).Marker!.StoreId);
    }

    protected async Task<(EvidenceUploadId, StagedObject)> StageAsync(
        byte[] content, EvidenceUploadId? uploadId = null, long maxBytes = 1024 * 1024)
    {
        var id = uploadId ?? EvidenceUploadId.New();
        using var body = new MemoryStream(content, writable: false);

        return (id, await Store.StageAsync(id, body, maxBytes, content.Length, Ct));
    }

    protected async Task<byte[]> ReadAllAsync(EvidenceHash hash)
    {
        await using var stream = await Store.OpenReadAsync(hash, null, Ct);
        Assert.NotNull(stream);
        return await DrainAsync(stream);
    }

    protected static async Task<byte[]> DrainAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, Ct);
        return buffer.ToArray();
    }
}
