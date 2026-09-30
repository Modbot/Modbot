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
        await _store.WriteStoreMarkerAsync(new StoreMarker(_storeId, _clock.UtcNow, "test"), Ct);

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

        var hash = (await _uploads.CommitAsync(ticket.UploadId, null, Ct)).Hash;

        // The step the API makes after a commit: the file goes on the case file.
        _metadata.PutOn(hash, reportId);

        return hash;
    }

    /// <summary>
    /// The failure this exists to prevent: one moderator's evidence vanishing because somebody
    /// tidied up an unrelated case file.
    /// </summary>
    [Fact]
    public async Task DestroyingAFileTwoCaseFilesShareIsRefusedAndBothAreNamed()
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
    public async Task DestroyingProceedsOnceTheLastCaseFileHasLetGo()
    {
        var content = SampleMedia.Mp4(size: 2048);

        var hash = await AttachAsync(content, "report-1");
        await AttachAsync(content, "report-2");

        _metadata.TakeOff(hash, "report-1");
        Assert.False((await _destroyer.DestroyAsync(hash, "Administrator", "cleanup", Ct)).Destroyed);

        _metadata.TakeOff(hash, "report-2");
        var result = await _destroyer.DestroyAsync(hash, "Administrator", "cleanup", Ct);

        Assert.True(result.Destroyed);
        Assert.Null(await _store.StatAsync(hash, Ct));
    }

    /// <summary>
    /// Destroying from a case file: that case file's hold does not stand in the way, but another
    /// case file's does, and nothing is taken off or deleted while it does.
    /// </summary>
    [Fact]
    public async Task DestroyingFromOneCaseFileIsStillStoppedByAnotherAndTakesNothingOff()
    {
        var content = SampleMedia.Mp4(size: 2048);

        var hash = await AttachAsync(content, "report-1");
        await AttachAsync(content, "report-2");

        var tookOff = false;
        var result = await _destroyer.DestroyAsync(
            hash,
            "Administrator",
            "cleanup",
            Ct,
            ignoreReports: ["report-1"],
            beforeDelete: _ =>
            {
                tookOff = true;
                return Task.CompletedTask;
            });

        Assert.False(result.Destroyed);
        Assert.Equal(["report-2"], result.BlockedByReports);
        Assert.False(tookOff);
        Assert.NotNull(await _store.StatAsync(hash, Ct));
        Assert.Empty(_metadata.Destroyed);
    }

    /// <summary>
    /// Destroying from the only case file that holds the file is one act: it is taken off, with the
    /// bytes still in the store, and only then deleted.
    /// </summary>
    [Fact]
    public async Task DestroyingFromTheOnlyCaseFileTakesItOffBeforeTheBytesGo()
    {
        var hash = await AttachAsync(SampleMedia.Png(1024), "report-1");

        ObjectStat? presentWhenTakenOff = null;
        var result = await _destroyer.DestroyAsync(
            hash,
            "Administrator",
            "cleanup",
            Ct,
            ignoreReports: ["report-1"],
            beforeDelete: async token =>
            {
                presentWhenTakenOff = await _store.StatAsync(hash, token);
                _metadata.TakeOff(hash, "report-1");
            });

        Assert.True(result.Destroyed);
        Assert.NotNull(presentWhenTakenOff);
        Assert.Null(await _store.StatAsync(hash, Ct));
        Assert.Empty(await _metadata.ReferencesAsync(hash, Ct));
    }

    /// <summary>
    /// A destroy that stops after the bytes are gone and before the file is marked is finished by
    /// trying again: deleting what is already gone is fine, and the file is marked once, with its
    /// one record written in the same step.
    /// </summary>
    [Fact]
    public async Task ADestroyThatStoppedAfterTheDeleteIsFinishedByTryingAgain()
    {
        var hash = await AttachAsync(SampleMedia.Png(1024), "report-1");
        _metadata.TakeOff(hash, "report-1");

        var wrote = 0;
        Task Within(Func<CancellationToken, Task<bool>> mark, CancellationToken token)
            => WriteAfterMarkAsync(mark, token, () => wrote++);

        _metadata.FailNextMark = true;

        await Assert.ThrowsAsync<IOException>(
            () => _destroyer.DestroyAsync(hash, "Gunner24", "cleanup", Ct, markWithin: Within));

        // The bytes are gone and nothing says so yet: no mark, and no record.
        Assert.Null(await _store.StatAsync(hash, Ct));
        Assert.Empty(_metadata.Destroyed);
        Assert.Equal(0, wrote);

        var retried = await _destroyer.DestroyAsync(hash, "Gunner24", "cleanup", Ct, markWithin: Within);

        Assert.True(retried.Destroyed);
        Assert.Single(_metadata.Destroyed);
        Assert.Equal(1, wrote);
    }

    /// <summary>
    /// Two destroys of the same file: only the one that marked it writes the record, so the log has
    /// one line for one destruction.
    /// </summary>
    [Fact]
    public async Task OnlyTheDestroyThatMarkedTheFileWritesItsRecord()
    {
        var hash = await AttachAsync(SampleMedia.Png(1024), "report-1");
        _metadata.TakeOff(hash, "report-1");

        var wrote = 0;
        Task Within(Func<CancellationToken, Task<bool>> mark, CancellationToken token)
            => WriteAfterMarkAsync(mark, token, () => wrote++);

        await _destroyer.DestroyAsync(hash, "first", "cleanup", Ct, markWithin: Within);
        await _destroyer.DestroyAsync(hash, "second", "cleanup", Ct, markWithin: Within);

        Assert.Equal(1, wrote);
        Assert.Equal("first", Assert.Single(_metadata.Destroyed).Actor);
    }

    private static async Task WriteAfterMarkAsync(
        Func<CancellationToken, Task<bool>> mark, CancellationToken token, Action write)
    {
        if (await mark(token))
            write();
    }

    /// <summary>
    /// Everything except the bytes survives. "This case had a video and an administrator deleted it
    /// on 4 March" has to remain answerable forever.
    /// </summary>
    [Fact]
    public async Task DestructionRecordsWhoDidItAndWhy()
    {
        var hash = await AttachAsync(SampleMedia.Png(1024), "report-1");
        _metadata.TakeOff(hash, "report-1");

        await _destroyer.DestroyAsync(hash, "Gunner24", "the subject asked and the case is closed", Ct);

        var record = Assert.Single(_metadata.Destroyed);
        Assert.Equal(hash.Hex, record.Hash);
        Assert.Equal("Gunner24", record.Actor);
        Assert.Equal("the subject asked and the case is closed", record.Reason);

        // The blob record itself is untouched: the hash, size and type are still answerable.
        Assert.Single(_metadata.Records);
    }


    /// <summary>
    /// Deleting on the authority of a database whose relationship to the store is in doubt is how
    /// a recoverable misconfiguration becomes an unrecoverable one.
    /// </summary>
    [Fact]
    public async Task NothingIsDestroyedWhileTheStoreStateIsUnresolved()
    {
        var hash = await AttachAsync(SampleMedia.Png(1024), "report-1");
        _metadata.TakeOff(hash, "report-1");

        File.Delete(Path.Combine(_root, EvidenceKeys.StoreMarkerKey));
        await _monitor.CheckAsync(Ct);

        await Assert.ThrowsAsync<EvidenceStoreUnavailableException>(
            () => _destroyer.DestroyAsync(hash, "Administrator", "cleanup", Ct));

        Assert.Empty(_metadata.Destroyed);
    }
}
