using System.Globalization;
using Modbot.Core.Time;
using Modbot.Evidence;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Storage.Database;

namespace Modbot.Api.Features.Evidence;

/// <summary>
/// The store the rest of Modbot holds, which can be rebuilt when the operator changes the
/// configuration it was built from.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> <c>AddModbotEvidence</c> resolves its options once, at
/// registration, and builds a singleton store from them — the right shape for a store that holds a
/// connection pool and an HTTP client. But evidence configuration lives in the database and is
/// changed from a settings page at runtime, so a store fixed at registration would mean an
/// operator who selects a backend is told it worked and then finds uploads still refused until
/// somebody redeploys.
/// </para>
/// <para>
/// That is not merely inconvenient. Design §8.4 refuses to shut Modbot down over a lost store
/// specifically because <em>"the only way to change the storage backend is the settings page —
/// inside the application that is refusing to start"</em>. A settings page whose changes need a
/// restart to take effect gives back most of what that reasoning was protecting: the operator
/// staring at a latched banner fixes the bucket, saves, and nothing happens.
/// </para>
/// <para>
/// So the store behind <see cref="IEvidenceStore"/> is a holder, and <see cref="Reload"/> rebuilds
/// what is inside it from the options singleton — which the settings endpoint has just written the
/// saved row onto. Everything else in the process keeps the same reference and never learns that
/// anything moved.
/// </para>
/// <para>
/// <strong>Nothing is rebuilt unless the configuration actually changed.</strong> An operator who
/// presses save twice, or who edits only the per-file cap, keeps the store that is already open —
/// which matters because replacing an S3 store disposes the previous one, and a download streaming
/// out of it at that instant is cut off. That is the correct behaviour when the store has genuinely
/// been repointed (the bytes are no longer there to send) and pure damage when it has not.
/// </para>
/// </remarks>
public sealed class ReloadableEvidenceStore : IEvidenceStore, IDisposable
{
    private readonly EvidenceOptions _options;
    private readonly IModbotClock _clock;
    private readonly IEvidenceConnectionFactory? _connections;
    private readonly Lock _gate = new();

    private IEvidenceStore _inner;
    private string _fingerprint;

    /// <param name="options">The singleton the settings endpoint writes the saved row onto.</param>
    /// <param name="clock">Never a machine clock. S3 request signing needs the time.</param>
    /// <param name="connections">
    /// Optional. A host that already owns a connection pool can keep the in-database backend on it
    /// rather than opening a second one, exactly as <c>AddModbotEvidence</c> allows.
    /// </param>
    public ReloadableEvidenceStore(
        EvidenceOptions options,
        IModbotClock clock,
        IEvidenceConnectionFactory? connections = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _options = options;
        _clock = clock;
        _connections = connections;
        _fingerprint = Fingerprint(options);
        _inner = Build();
    }

    /// <summary>
    /// The connection factory a candidate store should be built with, so a store the settings page
    /// is testing behaves the same way as the one it is about to become.
    /// </summary>
    public IEvidenceConnectionFactory? Connections => _connections;

    /// <summary>The store currently in use. Swapped by <see cref="Reload"/>.</summary>
    public IEvidenceStore Inner
    {
        get
        {
            lock (_gate)
                return _inner;
        }
    }

    /// <summary>
    /// Rebuilds the inner store from the options as they now stand, if they have changed.
    /// </summary>
    /// <returns>Whether anything was actually rebuilt.</returns>
    public bool Reload()
    {
        IEvidenceStore? replaced;

        lock (_gate)
        {
            var fingerprint = Fingerprint(_options);
            if (fingerprint == _fingerprint)
                return false;

            replaced = _inner;
            _inner = Build();
            _fingerprint = fingerprint;
        }

        // Disposed after the swap, never before: a caller that grabbed the old store a moment ago
        // has already got its reference, and the window is as small as it can be made.
        (replaced as IDisposable)?.Dispose();
        return true;
    }

    public EvidenceStoreCapabilities Capabilities => Inner.Capabilities;

    public string Description => Inner.Description;

    public Task<StagedObject> StageAsync(
        EvidenceUploadId uploadId,
        Stream body,
        long maxBytes,
        long? declaredLength = null,
        CancellationToken ct = default)
        => Inner.StageAsync(uploadId, body, maxBytes, declaredLength, ct);

    public Task<CommitOutcome> CommitAsync(
        EvidenceUploadId uploadId, EvidenceHash hash, CancellationToken ct = default)
        => Inner.CommitAsync(uploadId, hash, ct);

    public Task<Stream?> OpenReadAsync(
        EvidenceHash hash, ByteRange? range = null, CancellationToken ct = default)
        => Inner.OpenReadAsync(hash, range, ct);

    public Task<Stream?> OpenStagedAsync(EvidenceUploadId uploadId, CancellationToken ct = default)
        => Inner.OpenStagedAsync(uploadId, ct);

    public Task<ObjectStat?> StatAsync(EvidenceHash hash, CancellationToken ct = default)
        => Inner.StatAsync(hash, ct);

    public Task DeleteAsync(EvidenceHash hash, CancellationToken ct = default)
        => Inner.DeleteAsync(hash, ct);

    public Task DeleteStagedAsync(EvidenceUploadId uploadId, CancellationToken ct = default)
        => Inner.DeleteStagedAsync(uploadId, ct);

    public Task<Uri?> TryCreatePresignedReadAsync(
        EvidenceHash hash,
        TimeSpan ttl,
        PresignedReadOptions? options = null,
        CancellationToken ct = default)
        => Inner.TryCreatePresignedReadAsync(hash, ttl, options, ct);

    public Task<Uri?> TryCreatePresignedWriteAsync(
        EvidenceUploadId uploadId,
        TimeSpan ttl,
        string? contentType = null,
        CancellationToken ct = default)
        => Inner.TryCreatePresignedWriteAsync(uploadId, ttl, contentType, ct);

    public Task WriteSentinelAsync(StoreSentinel sentinel, CancellationToken ct = default)
        => Inner.WriteSentinelAsync(sentinel, ct);

    public Task<StoreProbe> ProbeAsync(CancellationToken ct = default)
        => Inner.ProbeAsync(ct);

    public void Dispose()
    {
        IEvidenceStore inner;
        lock (_gate)
            inner = _inner;

        (inner as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Builds a store from options, honouring a host-supplied connection factory for the
    /// in-database backend.
    /// </summary>
    /// <remarks>
    /// <c>EvidenceServiceCollectionExtensions.CreateStore</c> is public precisely so that a
    /// candidate store can be built from unsaved values; this uses it for the same reason one step
    /// later, when the values have been saved and the live store has to catch up.
    /// </remarks>
    public static IEvidenceStore Create(
        EvidenceOptions options, IModbotClock clock, IEvidenceConnectionFactory? connections = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Backend is EvidenceBackend.Database && connections is not null
            ? new DatabaseEvidenceStore(connections, options.Database)
            : EvidenceServiceCollectionExtensions.CreateStore(options, clock);
    }

    private IEvidenceStore Build() => Create(_options, _clock, _connections);

    /// <summary>
    /// Everything a store is built from, and nothing a store is not.
    /// </summary>
    /// <remarks>
    /// Caps, grace periods and the direct-delivery toggle are read per call from the same options
    /// object, so changing one of those needs no new store. Only the fields that decide which
    /// object is constructed appear here.
    /// </remarks>
    private static string Fingerprint(EvidenceOptions options) => string.Join(
        '',
        options.Backend.ToString(),
        options.Filesystem.Root,
        options.S3.Bucket,
        options.S3.Endpoint,
        options.S3.AccessKeyId,
        options.S3.SecretAccessKey,
        options.S3.Region,
        options.S3.Prefix ?? string.Empty,
        options.S3.UsePathStyle.ToString(),
        options.Database.ConnectionString ?? string.Empty,
        options.Database.ChunkBytes.ToString(CultureInfo.InvariantCulture));
}
