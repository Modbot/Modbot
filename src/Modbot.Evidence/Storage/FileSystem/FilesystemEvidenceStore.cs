using Modbot.Evidence.Options;

namespace Modbot.Evidence.Storage.FileSystem;

/// <summary>
/// Evidence in a directory, sharded two levels deep by hash prefix (design section 4.2).
/// </summary>
/// <remarks>
/// <para>
/// For a group running Modbot on a machine in someone's house, where "go and sign up for object
/// storage" is a genuine barrier and the disk is right there. It needs a volume the operator
/// mounted deliberately: Modbot declares no <c>VOLUME</c> in its Dockerfile, because an anonymous
/// volume would make an unconfigured host appear to work and then lose everything the first time
/// the container is recreated — the silent failure of design section 8 with a coat of paint on it.
/// </para>
/// <para>
/// <strong>Every path this class builds is hex.</strong> The directory names come from
/// <see cref="EvidenceKeys"/>, which only accepts an <see cref="EvidenceHash"/> or an
/// <see cref="EvidenceUploadId"/>, so there is no filename, no extension and no user-supplied
/// segment anywhere under the root. Traversal is not defended against here because there is
/// nothing to defend: no call path can produce a <c>..</c>.
/// </para>
/// <para>
/// No presigned URLs, declared rather than thrown (<see cref="Capabilities"/>). Every read streams
/// through Modbot, which is fine at this scale.
/// </para>
/// </remarks>
public sealed class FilesystemEvidenceStore : IEvidenceStore
{
    private readonly string _root;

    public FilesystemEvidenceStore(FilesystemEvidenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Root))
            throw new ArgumentException("The filesystem backend needs a root directory.", nameof(options));

        _root = Path.GetFullPath(options.Root);
    }

    /// <summary>
    /// A rename is a server-side copy in every sense that matters: the bytes do not move and they
    /// do not cross the application.
    /// </summary>
    public EvidenceStoreCapabilities Capabilities
        => EvidenceStoreCapabilities.RangeRead | EvidenceStoreCapabilities.ServerSideCopy;

    public string Description => $"filesystem at '{_root}'";

    public async Task<StagedObject> StageAsync(
        EvidenceUploadId uploadId,
        Stream body,
        long maxBytes,
        long? declaredLength = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        var staging = StagingPath(uploadId);
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);

        // Written beside the staging key and renamed onto it, so a crashed transfer never leaves a
        // half-file at a key a retry would then believe in.
        var partial = staging + ".partial";

        try
        {
            await using var counting = new CountingHashStream(body, maxBytes);

            await using (var file = new FileStream(
                partial, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                await counting.CopyToAsync(file, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(partial, staging, overwrite: true);
            return new StagedObject(counting.Hash, counting.BytesRead);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    public Task<CommitOutcome> CommitAsync(
        EvidenceUploadId uploadId, EvidenceHash hash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var staging = StagingPath(uploadId);
        var final = ObjectPath(hash);

        if (File.Exists(final))
        {
            // Append-only: an existing key is never overwritten. With content addressing the bytes
            // are by definition the same ones, so there is nothing to reconcile.
            return Task.FromResult(CommitOutcome.AlreadyPresent);
        }

        if (!File.Exists(staging))
            throw new EvidenceStagingNotFoundException($"Nothing staged under upload {uploadId}.");

        Directory.CreateDirectory(Path.GetDirectoryName(final)!);

        try
        {
            File.Move(staging, final, overwrite: false);
            return Task.FromResult(CommitOutcome.Created);
        }
        catch (IOException) when (File.Exists(final))
        {
            // Another commit of the same bytes won the race. Both callers wanted the same object
            // to exist, and it does.
            return Task.FromResult(CommitOutcome.AlreadyPresent);
        }
    }

    public Task<Stream?> OpenReadAsync(
        EvidenceHash hash, ByteRange? range = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Open(ObjectPath(hash), range));
    }

    public Task<Stream?> OpenStagedAsync(EvidenceUploadId uploadId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Open(StagingPath(uploadId), null));
    }

    public Task<ObjectStat?> StatAsync(EvidenceHash hash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var info = new FileInfo(ObjectPath(hash));
        return Task.FromResult(info.Exists ? new ObjectStat(info.Length) : null);
    }

    public Task DeleteAsync(EvidenceHash hash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        TryDelete(ObjectPath(hash));
        return Task.CompletedTask;
    }

    public Task DeleteStagedAsync(EvidenceUploadId uploadId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        TryDelete(StagingPath(uploadId));
        TryDelete(StagingPath(uploadId) + ".partial");
        return Task.CompletedTask;
    }

    /// <summary>Always null: this store does not declare the capability.</summary>
    public Task<Uri?> TryCreatePresignedReadAsync(
        EvidenceHash hash, TimeSpan ttl, PresignedReadOptions? options = null, CancellationToken ct = default)
        => Task.FromResult<Uri?>(null);

    /// <summary>Always null: this store does not declare the capability.</summary>
    public Task<Uri?> TryCreatePresignedWriteAsync(
        EvidenceUploadId uploadId, TimeSpan ttl, string? contentType = null, CancellationToken ct = default)
        => Task.FromResult<Uri?>(null);

    public async Task WriteStoreMarkerAsync(StoreMarker marker, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(marker);

        Directory.CreateDirectory(_root);

        var path = Path.Combine(_root, EvidenceKeys.StoreMarkerKey);
        var partial = path + ".partial";

        await File.WriteAllBytesAsync(partial, marker.Serialise(), ct).ConfigureAwait(false);
        File.Move(partial, path, overwrite: true);
    }

    public async Task<StoreProbe> ProbeAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(_root, EvidenceKeys.StoreMarkerKey);

        try
        {
            if (!File.Exists(path))
                return StoreProbe.Absent(_root);

            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var marker = StoreMarker.TryParse(bytes);

            return marker is null ? StoreProbe.Malformed(_root) : StoreProbe.Present(marker, _root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A directory that will not answer is not a directory that has answered "empty".
            // Conflating the two is what turns a permissions mistake into a false report of loss.
            return StoreProbe.Unreachable(_root, e);
        }
    }

    private static Stream? Open(string path, ByteRange? range)
    {
        if (!File.Exists(path))
            return null;

        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);

        if (range is not { } r)
            return file;

        if (r.First >= file.Length)
        {
            file.Dispose();
            return new MemoryStream([], writable: false);
        }

        file.Seek(r.First, SeekOrigin.Begin);

        var available = file.Length - r.First;
        var wanted = r.Length is { } length ? Math.Min(length, available) : available;

        return new BoundedReadStream(file, wanted);
    }

    private string ObjectPath(EvidenceHash hash)
        => Path.Combine(_root, EvidenceKeys.ForObject(hash).Replace('/', Path.DirectorySeparatorChar));

    private string StagingPath(EvidenceUploadId uploadId)
        => Path.Combine(_root, EvidenceKeys.ForStaging(uploadId).Replace('/', Path.DirectorySeparatorChar));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
            // Already gone, which is the state the caller asked for.
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
