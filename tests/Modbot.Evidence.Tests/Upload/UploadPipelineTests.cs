using Modbot.Evidence.Content;
using Modbot.Evidence.Health;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Storage.FileSystem;
using Modbot.Evidence.Tests.Fakes;
using Modbot.Evidence.Upload;
using Modbot.TestSupport;

namespace Modbot.Evidence.Tests.Upload;

/// <summary>
/// Design section 9: begin, transfer, commit — and nothing attached to a report until the third
/// one finishes.
/// </summary>
public sealed class UploadPipelineTests : IAsyncLifetime
{
    private const long Cap = 64 * 1024;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "modbot-upload-tests", Guid.NewGuid().ToString("N"));

    private readonly FakeClock _clock = new();
    private readonly InMemoryEvidenceMetadata _metadata = new();
    private readonly InMemoryEvidenceUploadRegistry _registry = new();
    private readonly Guid _storeId = Guid.NewGuid();

    private FaultInjectingStore _store = null!;
    private EvidenceStoreMonitor _monitor = null!;
    private EvidenceOptions _options = null!;
    private EvidenceUploadService _uploads = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        _options = new EvidenceOptions
        {
            Backend = EvidenceBackend.Filesystem,
            StoreId = _storeId,
            MaxFileBytes = Cap,
            Filesystem = new FilesystemEvidenceOptions { Root = _root },
        };

        _store = new FaultInjectingStore(new FilesystemEvidenceStore(_options.Filesystem));
        await _store.WriteStoreMarkerAsync(new StoreMarker(_storeId, _clock.UtcNow, "test"), Ct);

        _monitor = new EvidenceStoreMonitor(_store, _options, _clock);
        await _monitor.CheckAsync(Ct);

        _uploads = new EvidenceUploadService(_store, _monitor, _registry, _metadata, _options, _clock);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }

    private async Task<CommitResult> UploadAsync(
        byte[] content, string? reportId = "report-1", string? fileName = "clip.mp4", long? claimedLength = null)
    {
        var ticket = await _uploads.BeginAsync(
            new BeginUploadRequest(fileName, "video/mp4", claimedLength, reportId, "Gunner24"), Ct);

        using var body = new MemoryStream(content, writable: false);
        await _uploads.ReceiveAsync(ticket.UploadId, body, claimedLength ?? content.Length, Ct);

        return await _uploads.CommitAsync(ticket.UploadId, null, Ct);
    }

    /// <summary>Opens and closes, so an assertion never leaves a handle on the staging file.</summary>
    private async Task<bool> StagedExistsAsync(EvidenceUploadId uploadId)
    {
        var staged = await _store.OpenStagedAsync(uploadId, Ct);
        if (staged is null)
            return false;

        await staged.DisposeAsync();
        return true;
    }

    [Fact]
    public async Task AGoodUploadIsStoredUnderItsHashAndRecordedOnce()
    {
        var content = SampleMedia.Mp4(size: 4096);
        var result = await UploadAsync(content);

        Assert.Equal(EvidenceHash.Compute(content), result.Hash);
        Assert.Equal(content.Length, result.ByteSize);
        Assert.Equal(EvidenceContentType.Mp4, result.ContentType);

        var record = Assert.Single(_metadata.Records);
        Assert.Equal("clip.mp4", record.FileName);
        Assert.Equal("Gunner24", record.UploaderId);
        Assert.Equal(EvidenceBackend.Filesystem, record.Backend);
        Assert.Equal(EvidenceOrigin.Uploaded, record.Origin);
    }

    /// <summary>
    /// Design section 7.2: object first, metadata second. A row in the blob record is a claim that
    /// the bytes were confirmed present, and section 8 relies on that claim being true.
    /// </summary>
    [Fact]
    public async Task TheObjectIsPresentBeforeTheMetadataIsWritten()
    {
        ObjectStat? seen = null;
        _metadata.OnRecording = async record => seen = await _store.StatAsync(record.Hash, Ct);

        var content = SampleMedia.Png(2048);
        await UploadAsync(content, fileName: "shot.png");

        Assert.NotNull(seen);
        Assert.Equal(content.Length, seen.ByteSize);
    }

    /// <summary>
    /// The type is decided by content inspection, not by the client's claim and not by the
    /// filename. An SVG named <c>.png</c> is the canonical attempt.
    /// </summary>
    [Fact]
    public async Task AnSvgRenamedToPngIsRejectedAtCommitAndNothingIsAttached()
    {
        var ticket = await _uploads.BeginAsync(
            new BeginUploadRequest("harmless.png", "image/png", null, "report-1", "Gunner24"), Ct);

        using var body = new MemoryStream(SampleMedia.Svg());
        await _uploads.ReceiveAsync(ticket.UploadId, body, null, Ct);

        var rejected = await Assert.ThrowsAsync<EvidenceRejectedException>(
            () => _uploads.CommitAsync(ticket.UploadId, null, Ct));

        Assert.Equal(ContentRejection.ScriptableMarkup, rejected.Reason);
        Assert.Empty(_metadata.Records);

        // And the staging object goes immediately rather than waiting for the sweep.
        Assert.False(await StagedExistsAsync(ticket.UploadId));
    }

    /// <summary>
    /// <c>Content-Length</c> is a claim. The cap is enforced against bytes actually seen, and the
    /// transfer is abandoned mid-stream rather than buffered and measured.
    /// </summary>
    [Fact]
    public async Task AnUploadThatLiesAboutItsLengthIsStoppedMidStream()
    {
        var ticket = await _uploads.BeginAsync(
            new BeginUploadRequest("small.mp4", "video/mp4", 1024, "report-1", "Gunner24"), Ct);

        using var body = new MemoryStream(SampleMedia.Mp4(size: (int)Cap * 4));

        await Assert.ThrowsAsync<EvidenceTooLargeException>(
            () => _uploads.ReceiveAsync(ticket.UploadId, body, 1024, Ct));

        Assert.Empty(_metadata.Records);
        Assert.False(await StagedExistsAsync(ticket.UploadId));
    }

    /// <summary>The cheapest possible refusal: a declared size over the cap, before a byte moves.</summary>
    [Fact]
    public async Task ADeclaredSizeOverTheCapIsRefusedAtTheBeginning()
        => await Assert.ThrowsAsync<EvidenceTooLargeException>(
            () => _uploads.BeginAsync(new BeginUploadRequest("huge.mp4", "video/mp4", Cap + 1), Ct));

    [Fact]
    public async Task ADeclaredTypeOffTheAllowlistIsRefusedAtTheBeginning()
        => await Assert.ThrowsAsync<EvidenceRejectedException>(
            () => _uploads.BeginAsync(new BeginUploadRequest("drawing.svg", "image/svg+xml"), Ct));

    /// <summary>
    /// Deduplication: the same bytes on two reports are one object. The uploader is told nothing
    /// about it, because "you have already uploaded this file" reveals a case file they may have no
    /// right to see.
    /// </summary>
    [Fact]
    public async Task TheSameFileOnTwoReportsStoresOneObjectAndRecordsTwoAttachments()
    {
        var content = SampleMedia.Webm(3000);

        var first = await UploadAsync(content, reportId: "report-1");
        var second = await UploadAsync(content, reportId: "report-2");

        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(CommitOutcome.Created, first.Outcome);
        Assert.Equal(CommitOutcome.AlreadyPresent, second.Outcome);

        // Two attachments, one blob, and two reports now share the bytes.
        Assert.Equal(2, _metadata.Records.Count);
        Assert.Equal(2, (await _metadata.ReferencesAsync(first.Hash, Ct)).Count);
    }

    /// <summary>
    /// An object whose contents do not hash to its name is detectably wrong, and that is most of
    /// what content addressing is for.
    /// </summary>
    [Fact]
    public async Task ACommitWhoseHashDoesNotMatchIsRefused()
    {
        var ticket = await _uploads.BeginAsync(
            new BeginUploadRequest("clip.mp4", "video/mp4", null, "report-1", "Gunner24"), Ct);

        using var body = new MemoryStream(SampleMedia.Mp4(size: 2048));
        await _uploads.ReceiveAsync(ticket.UploadId, body, null, Ct);

        var somethingElse = EvidenceHash.Compute(SampleMedia.Png(2048));

        await Assert.ThrowsAsync<EvidenceRejectedException>(
            () => _uploads.CommitAsync(ticket.UploadId, somethingElse, Ct));

        Assert.Empty(_metadata.Records);
    }

    /// <summary>
    /// A store that goes away mid-upload leaves nothing attached, and the next attempt re-probes
    /// the store marker — design section 8.3's "first upload after any store failure".
    /// </summary>
    [Fact]
    public async Task AStoreThatFailsMidUploadAttachesNothingAndIsReprobed()
    {
        var ticket = await _uploads.BeginAsync(
            new BeginUploadRequest("clip.mp4", "video/mp4", null, "report-1", "Gunner24"), Ct);

        _store.Fault = new IOException("the volume went away");

        using var body = new MemoryStream(SampleMedia.Mp4(size: 2048));

        await Assert.ThrowsAsync<IOException>(
            () => _uploads.ReceiveAsync(ticket.UploadId, body, null, Ct));

        Assert.Empty(_metadata.Records);

        // The re-probe ran, and it found a store that is not answering rather than one that is
        // missing its store marker — so nothing latched on a transport failure.
        Assert.Equal(EvidenceStoreState.Unreachable, _monitor.Current.State);
    }

    /// <summary>
    /// Accepting an upload into a store that has just demonstrated it loses everything is worse
    /// than refusing it.
    /// </summary>
    [Fact]
    public async Task UploadsAreRefusedWhileTheStoreIsLatchedUnavailable()
    {
        File.Delete(Path.Combine(_root, EvidenceKeys.StoreMarkerKey));
        await _monitor.CheckAsync(Ct);

        await Assert.ThrowsAsync<EvidenceStoreUnavailableException>(
            () => _uploads.BeginAsync(new BeginUploadRequest("clip.mp4", "video/mp4"), Ct));
    }

    /// <summary>A retried commit returns the same answer rather than attaching the evidence twice.</summary>
    [Fact]
    public async Task CommitIsIdempotentOnTheUploadId()
    {
        var content = SampleMedia.Gif(1500);

        var ticket = await _uploads.BeginAsync(
            new BeginUploadRequest("reaction.gif", "image/gif", null, "report-1", "Gunner24"), Ct);

        using var body = new MemoryStream(content);
        await _uploads.ReceiveAsync(ticket.UploadId, body, content.Length, Ct);

        var first = await _uploads.CommitAsync(ticket.UploadId, null, Ct);
        var second = await _uploads.CommitAsync(ticket.UploadId, null, Ct);

        Assert.Equal(first.Hash, second.Hash);
        Assert.Single(_metadata.Records);
    }

    /// <summary>
    /// A moderator who closed the tab leaves a staging object and nothing else. Committing it
    /// after the sweep took it says so plainly instead of failing obscurely.
    /// </summary>
    [Fact]
    public async Task CommittingAnUploadWhoseBytesAreGoneSaysNothingWasAttached()
    {
        var ticket = await _uploads.BeginAsync(
            new BeginUploadRequest("clip.mp4", "video/mp4", null, "report-1", "Gunner24"), Ct);

        using var body = new MemoryStream(SampleMedia.Mp4(size: 1024));
        await _uploads.ReceiveAsync(ticket.UploadId, body, null, Ct);

        await _store.DeleteStagedAsync(ticket.UploadId, Ct);

        await Assert.ThrowsAsync<EvidenceStagingNotFoundException>(
            () => _uploads.CommitAsync(ticket.UploadId, null, Ct));

        Assert.Empty(_metadata.Records);
    }

    /// <summary>
    /// The capability branch, not a caught exception: a filesystem store hands out no presigned
    /// target and the caller is expected to send the bytes to Modbot instead.
    /// </summary>
    [Fact]
    public async Task AStoreWithoutPresignedWritesHandsOutNoTarget()
    {
        var ticket = await _uploads.BeginAsync(new BeginUploadRequest("clip.mp4", "video/mp4"), Ct);

        Assert.Null(ticket.PresignedTarget);
        Assert.Equal(Cap, ticket.MaxBytes);
        Assert.Equal(EvidenceContentType.Allowed, ticket.AcceptedTypes);
    }

    /// <summary>
    /// Design section 9.5: Modbot deletes its own garbage, because there is no bucket lifecycle
    /// rule to lean on.
    /// </summary>
    [Fact]
    public async Task TheSweepTakesStagingObjectsOlderThanTheGracePeriod()
    {
        var ticket = await _uploads.BeginAsync(new BeginUploadRequest("clip.mp4", "video/mp4"), Ct);

        using var body = new MemoryStream(SampleMedia.Mp4(size: 1024));
        await _uploads.ReceiveAsync(ticket.UploadId, body, null, Ct);

        var sweeper = new StagingSweeper(_store, _registry, _monitor, _options, _clock);

        // Nothing is swept while the upload is still young enough to be somebody's slow connection.
        Assert.Empty((await sweeper.SweepAsync(Ct)).Swept);
        Assert.True(await StagedExistsAsync(ticket.UploadId));

        _clock.Advance(_options.StagingGrace + TimeSpan.FromMinutes(1));

        Assert.Equal([ticket.UploadId], (await sweeper.SweepAsync(Ct)).Swept);
        Assert.False(await StagedExistsAsync(ticket.UploadId));
    }

    [Fact]
    public async Task TheSweepRefusesToRunWhileTheStoreIsUnresolved()
    {
        var ticket = await _uploads.BeginAsync(new BeginUploadRequest("clip.mp4", "video/mp4"), Ct);

        using var body = new MemoryStream(SampleMedia.Mp4(size: 1024));
        await _uploads.ReceiveAsync(ticket.UploadId, body, null, Ct);

        _clock.Advance(_options.StagingGrace + TimeSpan.FromMinutes(1));

        File.Delete(Path.Combine(_root, EvidenceKeys.StoreMarkerKey));
        await _monitor.CheckAsync(Ct);

        var result = await new StagingSweeper(_store, _registry, _monitor, _options, _clock).SweepAsync(Ct);

        Assert.Empty(result.Swept);
        Assert.NotNull(result.Skipped);
        Assert.True(await StagedExistsAsync(ticket.UploadId));
    }
}
