using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Storage.FileSystem;
using Modbot.Evidence.Tests.Fakes;

namespace Modbot.Evidence.Tests.Storage;

/// <summary>The conformance suite against a directory, plus what is specific to a directory.</summary>
public class FilesystemStoreTests : EvidenceStoreConformanceTests
{
    private readonly List<string> _roots = [];

    protected override Task<IEvidenceStore> CreateStoreAsync(CancellationToken ct)
    {
        var root = Path.Combine(Path.GetTempPath(), "modbot-evidence-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);

        return Task.FromResult<IEvidenceStore>(
            new FilesystemEvidenceStore(new FilesystemEvidenceOptions { Root = root }));
    }

    protected override ValueTask CleanUpAsync()
    {
        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not a test failure.
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The layout is part of the contract: a sweep, a backup script and an operator with a
    /// terminal all read it.
    /// </summary>
    [Fact]
    public async Task ObjectsLandInATwoLevelShardUnderTheirFullHash()
    {
        var content = SampleMedia.Png(1024);
        var (uploadId, staged) = await StageAsync(content);
        await Store.CommitAsync(uploadId, staged.Hash, Ct);

        var hex = staged.Hash.Hex;
        var expected = Path.Combine(_roots[0], "sha256", hex[..2], hex[2..4], hex);

        Assert.True(File.Exists(expected), $"Expected the object at {expected}.");
    }

    /// <summary>
    /// Nothing under the root carries a filename, an extension, or anything a user chose. The
    /// store marker is the one exception and it is Modbot's own name.
    /// </summary>
    [Fact]
    public async Task EveryPathUnderTheRootIsHexOrTheStoreMarker()
    {
        var (uploadId, staged) = await StageAsync(SampleMedia.Jpeg(700));
        await Store.CommitAsync(uploadId, staged.Hash, Ct);
        await StageAsync(SampleMedia.Gif(700));
        await Store.WriteStoreMarkerAsync(new StoreMarker(Guid.NewGuid(), DateTimeOffset.UnixEpoch, null), Ct);

        foreach (var file in Directory.EnumerateFiles(_roots[0], "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(_roots[0], file).Replace('\\', '/');

            var acceptable = relative == EvidenceKeys.StoreMarkerKey
                             || (relative.StartsWith("sha256/", StringComparison.Ordinal)
                                 && EvidenceKeys.TryReadObjectKey(relative, out _))
                             || (relative.StartsWith("staging/", StringComparison.Ordinal)
                                 && EvidenceUploadId.TryParse(relative["staging/".Length..], out _));

            Assert.True(acceptable, $"'{relative}' is not a hex key or the store marker.");
        }
    }

    /// <summary>
    /// An unreadable directory is "the store did not answer", not "the store is empty". Locking
    /// on the second would report loss where there is only a permissions problem.
    /// </summary>
    [Fact]
    public async Task AMangledStoreMarkerIsAFindingOfItsOwn()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_roots[0], EvidenceKeys.StoreMarkerKey), "{ not a store marker", Ct);

        Assert.Equal(StoreProbeOutcome.Malformed, (await Store.ProbeAsync(Ct)).Outcome);
    }

    /// <summary>
    /// The exact silent-loss scenario: the volume was never mounted, so the directory Modbot
    /// writes to is empty on the next boot.
    /// </summary>
    [Fact]
    public async Task AWipedDirectoryProbesAsAbsentEvenAfterCommissioning()
    {
        await Store.WriteStoreMarkerAsync(new StoreMarker(Guid.NewGuid(), DateTimeOffset.UnixEpoch, null), Ct);
        Assert.Equal(StoreProbeOutcome.Present, (await Store.ProbeAsync(Ct)).Outcome);

        Directory.Delete(_roots[0], recursive: true);
        Directory.CreateDirectory(_roots[0]);

        Assert.Equal(StoreProbeOutcome.Absent, (await Store.ProbeAsync(Ct)).Outcome);
    }

    [Fact]
    public void ItDeclaresNoPresigning()
    {
        Assert.False(Store.Capabilities.HasFlag(EvidenceStoreCapabilities.PresignedRead));
        Assert.False(Store.Capabilities.HasFlag(EvidenceStoreCapabilities.PresignedWrite));
    }

    /// <summary>A half-written transfer must not leave something a retry would believe in.</summary>
    [Fact]
    public async Task AnAbortedTransferLeavesNoStagingObject()
    {
        var uploadId = EvidenceUploadId.New();
        using var body = new MemoryStream(SampleMedia.Mp4(size: 40_000));

        await Assert.ThrowsAsync<EvidenceTooLargeException>(
            () => Store.StageAsync(uploadId, body, 4_000, null, Ct));

        Assert.Null(await Store.OpenStagedAsync(uploadId, Ct));
    }
}
