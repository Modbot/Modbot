using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Modbot.Core.Time;
using Modbot.Evidence.Options;
using S3ByteRange = Amazon.S3.Model.ByteRange;

namespace Modbot.Evidence.Storage.S3;

/// <summary>
/// Evidence in any S3-compatible bucket — Railway Buckets, Wasabi, Cloudflare R2, MinIO, AWS
/// (design section 4.1).
/// </summary>
/// <remarks>
/// <para>
/// The recommended backend, because object storage is the right shape for this data — write-once,
/// read-rarely, large, completely uninteresting to query — and because it is the only one of the
/// three that can hand the browser a URL and step out of the way.
/// </para>
/// <para>
/// <strong>Nothing here uses a feature that is not universal.</strong> No lifecycle rules, no
/// versioning, no object lock, no server-side encryption with customer keys. That list is exactly
/// the Railway Buckets gap (design section 4.1.1), and designing to it costs nothing while
/// designing past it would produce a store that worked on AWS and quietly leaked objects
/// everywhere else. Two consequences show up in this class: Modbot deletes its own staging
/// garbage because no bucket rule will, and <see cref="DeleteAsync"/> is final because there is no
/// previous version to restore.
/// </para>
/// <para>
/// Both URL styles are supported through <see cref="S3EvidenceOptions.UsePathStyle"/>, because
/// Railway issues virtual-hosted-style on new buckets and path-style on older ones and MinIO
/// defaults to path-style. Request checksum calculation is pinned to "when required" so the SDK
/// does not attach trailing CRC32 checksums that some S3-compatible implementations reject.
/// </para>
/// </remarks>
public sealed class S3EvidenceStore : IEvidenceStore, IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly bool _ownsClient;
    private readonly S3EvidenceOptions _options;
    private readonly IModbotClock _clock;
    private readonly string _prefix;
    private readonly Protocol _protocol;

    public S3EvidenceStore(S3EvidenceOptions options, IModbotClock clock)
        : this(CreateClient(options), options, clock, ownsClient: true)
    {
    }

    /// <param name="client">
    /// An already-built client. The seam tests use to point at a MinIO container, and the seam a
    /// host would use to supply its own credential provider.
    /// </param>
    /// <param name="ownsClient">Whether disposing this store disposes the client.</param>
    public S3EvidenceStore(IAmazonS3 client, S3EvidenceOptions options, IModbotClock clock, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        if (string.IsNullOrWhiteSpace(options.Bucket))
            throw new ArgumentException("The S3 backend needs a bucket name.", nameof(options));

        _client = client;
        _options = options;
        _clock = clock;
        _ownsClient = ownsClient;
        _prefix = NormalisePrefix(options.Prefix);

        // Taken from the endpoint rather than left at the SDK's default, which is HTTPS whatever
        // the endpoint says. A self-hosted MinIO reached over HTTP would otherwise be handed
        // presigned URLs nothing could connect to, and the failure would appear in the browser
        // rather than anywhere an operator would look.
        _protocol = options.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? Protocol.HTTP
            : Protocol.HTTPS;
    }

    public EvidenceStoreCapabilities Capabilities
        => EvidenceStoreCapabilities.PresignedRead
           | EvidenceStoreCapabilities.PresignedWrite
           | EvidenceStoreCapabilities.RangeRead
           | EvidenceStoreCapabilities.ServerSideCopy;

    public string Description => $"bucket '{_options.Bucket}' at {_options.Endpoint}";

    public async Task<StagedObject> StageAsync(
        EvidenceUploadId uploadId,
        Stream body,
        long maxBytes,
        long? declaredLength = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        var length = declaredLength ?? (body.CanSeek ? body.Length - body.Position : null);

        if (length is null)
        {
            // The SDK needs a length or it has to buffer, and buffering a hundred megabytes is the
            // thing design section 9.3 exists to prevent. Browsers always send one; the presigned
            // path does not come through here at all.
            throw new ArgumentException(
                "The S3 backend needs the length of the body up front. Send Content-Length, or use "
                + "the presigned upload path.",
                nameof(declaredLength));
        }

        if (maxBytes > 0 && length > maxBytes)
            throw new EvidenceTooLargeException(maxBytes);

        await using var counting = new CountingHashStream(body, maxBytes);

        var request = new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = StagingKey(uploadId),
            InputStream = counting,
            AutoCloseStream = false,
            AutoResetStreamPosition = false,
        };

        request.Headers.ContentLength = length.Value;

        await _client.PutObjectAsync(request, ct).ConfigureAwait(false);

        return new StagedObject(counting.Hash, counting.BytesRead);
    }

    public async Task<CommitOutcome> CommitAsync(
        EvidenceUploadId uploadId, EvidenceHash hash, CancellationToken ct = default)
    {
        // Append-only, checked before the copy. The race — two commits of identical bytes — is
        // benign by construction: whoever wins, the object at that key is the same object.
        if (await StatAsync(hash, ct).ConfigureAwait(false) is not null)
            return CommitOutcome.AlreadyPresent;

        var staging = StagingKey(uploadId);

        try
        {
            // Server-side. The bytes never cross the application, which on Railway means the move
            // is a free API operation rather than charged service egress in both directions.
            await _client.CopyObjectAsync(
                new CopyObjectRequest
                {
                    SourceBucket = _options.Bucket,
                    SourceKey = staging,
                    DestinationBucket = _options.Bucket,
                    DestinationKey = ObjectKey(hash),
                },
                ct).ConfigureAwait(false);

            return CommitOutcome.Created;
        }
        catch (AmazonS3Exception e) when (IsMissing(e))
        {
            throw new EvidenceStagingNotFoundException($"Nothing staged under upload {uploadId}.", e);
        }
    }

    public Task<Stream?> OpenReadAsync(
        EvidenceHash hash, ByteRange? range = null, CancellationToken ct = default)
        => OpenAsync(ObjectKey(hash), range, ct);

    public Task<Stream?> OpenStagedAsync(EvidenceUploadId uploadId, CancellationToken ct = default)
        => OpenAsync(StagingKey(uploadId), null, ct);

    public async Task<ObjectStat?> StatAsync(EvidenceHash hash, CancellationToken ct = default)
    {
        try
        {
            var response = await _client
                .GetObjectMetadataAsync(_options.Bucket, ObjectKey(hash), ct)
                .ConfigureAwait(false);

            return new ObjectStat(response.ContentLength);
        }
        catch (AmazonS3Exception e) when (IsMissing(e))
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes the object. There is no undo: Railway Buckets has no versioning, and R2 and Wasabi
    /// have it only if somebody turned it on.
    /// </summary>
    public async Task DeleteAsync(EvidenceHash hash, CancellationToken ct = default)
        => await DeleteKeyAsync(ObjectKey(hash), ct).ConfigureAwait(false);

    public async Task DeleteStagedAsync(EvidenceUploadId uploadId, CancellationToken ct = default)
        => await DeleteKeyAsync(StagingKey(uploadId), ct).ConfigureAwait(false);

    public Task<Uri?> TryCreatePresignedReadAsync(
        EvidenceHash hash, TimeSpan ttl, PresignedReadOptions? options = null, CancellationToken ct = default)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = ObjectKey(hash),
            Verb = HttpVerb.GET,
            Protocol = _protocol,
            Expires = _clock.UtcNow.Add(ttl).UtcDateTime,
        };

        // A note on the expiry: the SDK turns this absolute instant into a relative
        // `X-Amz-Expires` against its own view of now, so the window is only as long as intended
        // while IModbotClock agrees with the machine clock. In production it does — the clock is
        // the machine's, corrected in one place — and taking the instant from IModbotClock rather
        // than from the machine clock here is what keeps the standing rule intact.
        //
        // Signing these pins the two headers that matter most. It cannot pin nosniff or a CSP —
        // those are not response-override parameters in the S3 API — which is why design
        // section 10.3 says plainly that direct delivery leans on the allowlist rather than on
        // headers, and why an operator can turn direct delivery off and pay the egress instead.
        if (options is not null)
        {
            request.ResponseHeaderOverrides.ContentType = options.ContentType;

            if (options.FileName is { } name)
                request.ResponseHeaderOverrides.ContentDisposition = $"attachment; filename=\"{Sanitise(name)}\"";
        }

        return Task.FromResult<Uri?>(new Uri(_client.GetPreSignedURL(request)));
    }

    public Task<Uri?> TryCreatePresignedWriteAsync(
        EvidenceUploadId uploadId, TimeSpan ttl, string? contentType = null, CancellationToken ct = default)
    {
        // A presigned PUT cannot carry a content-length-range condition — that is a POST policy
        // feature, and whether Tigris honours one is open question 2 in design section 20. So the
        // cap is enforced where it can be: at commit, on the size actually stored, before anything
        // is attached to a report. The cost of the gap is that an operator can be billed for bytes
        // Modbot then rejects and deletes.
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = StagingKey(uploadId),
            Verb = HttpVerb.PUT,
            Protocol = _protocol,
            Expires = _clock.UtcNow.Add(ttl).UtcDateTime,
            ContentType = contentType,
        };

        return Task.FromResult<Uri?>(new Uri(_client.GetPreSignedURL(request)));
    }

    public async Task WriteSentinelAsync(StoreSentinel sentinel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sentinel);

        var bytes = sentinel.Serialise();
        using var body = new MemoryStream(bytes, writable: false);

        await _client.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = _options.Bucket,
                Key = _prefix + EvidenceKeys.SentinelKey,
                InputStream = body,
                ContentType = "application/json",
            },
            ct).ConfigureAwait(false);
    }

    public async Task<StoreProbe> ProbeAsync(CancellationToken ct = default)
    {
        var key = _prefix + EvidenceKeys.SentinelKey;

        try
        {
            using var response = await _client
                .GetObjectAsync(_options.Bucket, key, ct)
                .ConfigureAwait(false);

            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct).ConfigureAwait(false);

            var sentinel = StoreSentinel.TryParse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            return sentinel is null ? StoreProbe.Malformed(Description) : StoreProbe.Present(sentinel, Description);
        }
        catch (AmazonS3Exception e) when (IsMissing(e))
        {
            // A missing key and a missing bucket are the same finding: this is not the store the
            // configuration describes. A mistyped prefix and a renamed bucket both land here, and
            // both are exactly what the sentinel exists to catch.
            return StoreProbe.Absent(Description);
        }
        catch (Exception e) when (e is AmazonS3Exception or AmazonServiceException or HttpRequestException or IOException)
        {
            return StoreProbe.Unreachable(Description, e);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }

    /// <summary>
    /// The client configuration Modbot uses against every S3-compatible endpoint. Public so a host
    /// that supplies its own client builds it the same way this one would.
    /// </summary>
    public static AmazonS3Config BuildConfig(S3EvidenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new AmazonS3Config
        {
            ServiceURL = options.Endpoint,
            ForcePathStyle = options.UsePathStyle,
            AuthenticationRegion = options.Region,

            // Some S3-compatible implementations reject the trailing CRC32 checksum the v4 SDK
            // would otherwise add to every PUT. Modbot has its own end-to-end integrity story —
            // the key is the hash — so the SDK's is redundant here as well as risky.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
    }

    private static IAmazonS3 CreateClient(S3EvidenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey),
            BuildConfig(options));
    }

    private async Task<Stream?> OpenAsync(string key, ByteRange? range, CancellationToken ct)
    {
        var request = new GetObjectRequest { BucketName = _options.Bucket, Key = key };

        if (range is { } r)
        {
            request.ByteRange = r.Last is { } last
                ? new S3ByteRange(r.First, last)
                : new S3ByteRange($"bytes={r.First}-");
        }

        try
        {
            var response = await _client.GetObjectAsync(request, ct).ConfigureAwait(false);
            return new S3ObjectStream(response);
        }
        catch (AmazonS3Exception e) when (IsMissing(e))
        {
            return null;
        }
    }

    private async Task DeleteKeyAsync(string key, CancellationToken ct)
    {
        try
        {
            await _client.DeleteObjectAsync(_options.Bucket, key, ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception e) when (IsMissing(e))
        {
            // Already gone.
        }
    }

    private static bool IsMissing(AmazonS3Exception e)
        => e.StatusCode is HttpStatusCode.NotFound
           || e.ErrorCode is "NoSuchKey" or "NoSuchBucket" or "NotFound";

    private string ObjectKey(EvidenceHash hash) => _prefix + EvidenceKeys.ForObject(hash);

    private string StagingKey(EvidenceUploadId uploadId) => _prefix + EvidenceKeys.ForStaging(uploadId);

    private static string NormalisePrefix(string? prefix)
        => string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix.Trim('/') + "/";

    /// <summary>
    /// Quotes and strips a filename for a signed disposition header. Nothing here reaches a key —
    /// keys are hex — so this only has to be safe to put between two quotation marks.
    /// </summary>
    private static string Sanitise(string fileName)
    {
        Span<char> buffer = stackalloc char[Math.Min(fileName.Length, 120)];
        var written = 0;

        foreach (var c in fileName)
        {
            if (written == buffer.Length)
                break;

            buffer[written++] = char.IsControl(c) || c is '"' or '\\' ? '_' : c;
        }

        return written == 0 ? "evidence" : new string(buffer[..written]);
    }
}
