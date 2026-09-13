using Modbot.Evidence.Health;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Storage.FileSystem;
using Modbot.Evidence.Tests.Fakes;
using Modbot.Evidence.Upload;
using Modbot.TestSupport;

namespace Modbot.Evidence.Tests.Upload;

/// <summary>
/// Design section 6: destruction is refcounted, final, and recorded — and the reason it must be
/// refcounted is deduplication.
/// </summary>
public sealed class DestroyTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "modbot-destroy-tests", Guid.NewGuid().ToString("N"));

    private readonly FakeClock _clock = new();
    private readonly InMemoryEvidenceMetadata _metadata = new();
    private readonly InMemoryEvidenceUploadRegistry _registry = new();
    private readonly Guid _storeId = Guid.NewGuid();

    private FaultInjectingStore _store = null!;
    private EvidenceStoreMonitor _monitor = null!;
    private EvidenceUploadService _uploads = null!;
    private EvidenceDestroyer _destroyer = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var options = new EvidenceOptions
        {
            Backend = EvidenceBackend.Filesystem,
            StoreId = _storeId,
            Filesystem = new FilesystemEvidenceOptions { Root = _root },
        };

        _store = new FaultInjectingStore(new FilesystemEvidenceStore(options.Filesystem));
        await _store.WriteSentinelAsync(new StoreSentinel(_storeId, _clock.UtcNow, "test"), Ct);

        _monitor = new EvidenceStoreMonitor(_store, options, _clock);
        await _monitor.CheckAsync(Ct);

        _uploads = new EvidenceUploadService(_store, _monitor, _registry, _metadata, options, _clock);
        _destroyer = new EvidenceDestroyer(_store, _metadata, _monitor);
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

    private async Task<EvidenceHash> AttachAsync(byte[] content, string reportId)
    {
        var ticket = await _uploads.BeginAsync(
            new BeginUploadRequest("clip.mp4", "video/mp4", null, reportId, "Gunner24"), Ct);

        using var body = new MemoryStream(content, writable: false);
        await _uploads.ReceiveAsync(ticket.UploadId, body, content.Length, Ct);

        return (await _uploads.CommitAsync(ticket.UploadId, null, Ct)).Hash;
    }

    /// <summary>
    /// The failure this exists to prevent: one moderator's evidence vanishing because somebody
    /// tidied up an unrelated report.
    /// </summary>
    [Fact]
    public async Task DestroyingAFileTwoReportsShareIsRefusedAndBothAreNamed()
    {
        var content = SampleMedia.Mp4(size: 2048);

        var hash = await AttachAsync(content, "report-1");
        await AttachAsync(content, "report-2");

        var result = await _destroyer.DestroyAsync(hash, "Administrator", "requested by the subject", Ct);

        Assert.False(result.Destroyed);
        Assert.Equal(["report-1", "report-2"], result.BlockedByReports);
        Assert.Contains("report-1", result.Message, StringComparison.Ordinal);
        Assert.Contains("report-2", result.Message, StringComparison.Ordinal);

        // And the bytes are still there, which is the whole point.
        Assert.NotNull(await _store.StatAsync(hash, Ct));
    }

    [Fact]
    public async Task DestroyingProceedsOnceTheLastAttachmentIsGone()
    {
        var content = SampleMedia.Mp4(size: 2048);

        var hash = await AttachAsync(content, "report-1");
        await AttachAsync(content, "report-2");

        _metadata.Detach(hash, "report-1");
        Assert.False((await _destroyer.DestroyAsync(hash, "Administrator", "cleanup", Ct)).Destroyed);

        _metadata.Detach(hash, "report-2");
        var result = await _destroyer.DestroyAsync(hash, "Administrator", "cleanup", Ct);

        Assert.True(result.Destroyed);
        Assert.Null(await _store.StatAsync(hash, Ct));
    }

    /// <summary>
    /// Everything except the bytes survives. "This case had a video and an administrator deleted it
    /// on 4 March" has to remain answerable forever.
    /// </summary>
    [Fact]
    public async Task DestructionRecordsWhoDidItAndWhy()
    {
        var hash = await AttachAsync(SampleMedia.Png(1024), "report-1");
        _metadata.Detach(hash, "report-1");

        await _destroyer.DestroyAsync(hash, "Gunner24", "the subject asked and the case is closed", Ct);

        var record = Assert.Single(_metadata.Destroyed);
        Assert.Equal(hash.Hex, record.Hash);
        Assert.Equal("Gunner24", record.Actor);
        Assert.Equal("the subject asked and the case is closed", record.Reason);

        // The blob record itself is untouched: the hash, size and type are still answerable.
        Assert.Single(_metadata.Records);
    }

    [Fact]
    public async Task TheMessageSaysThereIsNoUndo()
    {
        var content = SampleMedia.Mp4(size: 512);
        var hash = await AttachAsync(content, "report-1");

        var blocked = await _destroyer.DestroyAsync(hash, "Administrator", "cleanup", Ct);
        Assert.Contains("cannot be undone", blocked.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Deleting on the authority of a database whose relationship to the store is in doubt is how
    /// a recoverable misconfiguration becomes an unrecoverable one.
    /// </summary>
    [Fact]
    public async Task NothingIsDestroyedWhileTheStoreStateIsUnresolved()
    {
        var hash = await AttachAsync(SampleMedia.Png(1024), "report-1");
        _metadata.Detach(hash, "report-1");

        File.Delete(Path.Combine(_root, EvidenceKeys.SentinelKey));
        await _monitor.CheckAsync(Ct);

        await Assert.ThrowsAsync<EvidenceStoreUnavailableException>(
            () => _destroyer.DestroyAsync(hash, "Administrator", "cleanup", Ct));

        Assert.Empty(_metadata.Destroyed);
    }
}
