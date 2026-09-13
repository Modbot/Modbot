using Microsoft.EntityFrameworkCore;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.Evidence.Content;
using Modbot.Evidence.Health;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;

namespace Modbot.Api.Features.Evidence;

/// <summary>
/// Everything the Settings → Evidence screen does, in one place: read the configuration, test a
/// candidate store, and save one that passed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing is persisted that has not been proved.</strong> Saving a backend runs design
/// §8.5's round trip first — write a canary, read it back, compare the bytes, promote it, read it
/// again, delete it, then write the sentinel — and only then does the row change. A backend that
/// cannot do all of that cannot be selected, and the error names the step rather than saying
/// "storage error".
/// </para>
/// <para>
/// <strong>A suspicion never blocks; proof always does.</strong> The filesystem backend on a
/// directory Modbot cannot prove survives a restart produces a warning and a question, never a
/// refusal — Railway, Fly.io and Render all support mountable volumes, and the operator is the only
/// party who knows whether they mounted one. A directory that cannot be written to at all is
/// refused, because that is a fact with nothing to judge. The sentinel latch is untouched by any
/// of this and stays fatal to uploads: an absent, foreign or malformed sentinel is proof of a lost
/// or wrong store.
/// </para>
/// </remarks>
public sealed class EvidenceSettingsService
{
    /// <summary>
    /// Said at the moment of choosing, because that is when the operator is making the decision.
    /// </summary>
    /// <remarks>
    /// Design §8.5 and §4.1.1: an operator who believes their hosting provider keeps copies of
    /// their evidence is wrong, and finding that out during a dispute is the worst possible moment.
    /// Railway Buckets has no backups and no object versioning; a filesystem unlink is an unlink.
    /// </remarks>
    public const string DurabilityStatement =
        "Modbot does not back this store up and cannot undo a deletion from it. Object storage "
        + "providers differ: Railway Buckets keeps no backups and supports no object versioning, "
        + "Cloudflare R2 and Wasabi version objects only if you enable it, and a deleted file on "
        + "disk is simply gone. This data is yours to look after.";

    private static readonly IReadOnlyList<EvidenceBackendOption> BackendOptions =
    [
        new("S3", "S3-compatible object storage", "Wasabi, Railway Buckets, Cloudflare R2, MinIO or "
            + "AWS. Survives losing the container, hands the browser the bytes directly, and needs "
            + "no volume.", Recommended: true, Caution: null),
        new("Filesystem", "A directory on disk", "For a machine in somebody's house, where the disk "
            + "is right there. Needs a Docker volume you mounted yourself; Modbot declares none.",
            Recommended: false,
            Caution: "Evidence here is lost when the container is recreated unless a volume is "
                + "mounted at this path, and nothing errors when that happens."),
        new("Database", "Inside PostgreSQL", "Supported, and not recommended. One backup covers "
            + "everything, which is a real virtue for a small group.",
            Recommended: false,
            Caution: "Every byte lands in pg_dump, in the WAL, and on any replica. A 240 MB "
                + "database becomes a 20 GB dump the first time somebody attaches a dozen clips, "
                + "and a restore that takes four hours is not a restore you can perform during an "
                + "incident."),
        new("None", "Not configured", "Evidence cannot be attached to a report. Nothing else is "
            + "affected.", Recommended: false, Caution: null),
    ];

    private readonly ModbotContext _db;
    private readonly ISecretProtector _protector;
    private readonly EvidenceOptions _live;
    private readonly ReloadableEvidenceStore _store;
    private readonly EvidenceStoreMonitor _monitor;
    private readonly EvidenceStoreCommissioner _commissioner;
    private readonly IModbotClock _clock;
    private readonly HostPlatform _platform;
    private readonly EvidenceBootId _bootId;

    public EvidenceSettingsService(
        ModbotContext db,
        ISecretProtector protector,
        EvidenceOptions live,
        ReloadableEvidenceStore store,
        EvidenceStoreMonitor monitor,
        EvidenceStoreCommissioner commissioner,
        IModbotClock clock,
        HostPlatform platform,
        EvidenceBootId bootId)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(commissioner);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(bootId);

        _db = db;
        _protector = protector;
        _live = live;
        _store = store;
        _monitor = monitor;
        _commissioner = commissioner;
        _clock = clock;
        _platform = platform;
        _bootId = bootId;
    }

    /// <summary>The whole screen, in one payload.</summary>
    public async Task<EvidenceSettingsResponse> DescribeAsync(CancellationToken ct = default)
    {
        var settings = await _db.GetSettingsAsync(ct);
        var backend = EvidenceSettingsBinder.ToBackend(settings.EvidenceBackend);

        var present = await _db.EvidenceBlobs.AsNoTracking()
            .Where(b => b.DestroyedAt == null)
            .GroupBy(_ => 1)
            .Select(g => new { Count = (long)g.Count(), Bytes = g.Sum(b => b.ByteSize) })
            .FirstOrDefaultAsync(ct);

        var destroyed = await _db.EvidenceBlobs.AsNoTracking()
            .LongCountAsync(b => b.DestroyedAt != null, ct);

        var capabilities = _store.Capabilities;
        var health = _monitor.Current;

        return new EvidenceSettingsResponse(
            new EvidenceBackendView(
                backend.ToString(),
                settings.EvidenceStoreId,
                settings.EvidenceRoot,
                new EvidenceS3View(
                    settings.EvidenceS3Bucket,
                    settings.EvidenceS3Endpoint,
                    settings.EvidenceS3AccessKeyId,
                    settings.EvidenceS3Region,
                    settings.EvidenceS3Prefix,
                    settings.EvidenceS3UsePathStyle),
                settings.EvidenceS3SecretAccessKeyEncrypted is { Length: > 0 }),
            new EvidenceLimitsView(
                settings.EvidenceMaxFileBytes,
                settings.EvidenceMaxReportBytes,
                settings.EvidenceMaxDeploymentBytes,
                settings.EvidenceDirectDeliveryEnabled),
            Describe(capabilities, settings.EvidenceDirectDeliveryEnabled),
            Describe(health, _store.Description),
            backend is EvidenceBackend.Filesystem ? DescribeDurability(_live.Filesystem) : null,
            new EvidenceStoredView(present?.Count ?? 0, present?.Bytes ?? 0, destroyed),
            EvidenceContentType.Allowed,
            BackendOptions,
            backend is EvidenceBackend.None ? ReadEnvironmentHint() : null,
            DurabilityStatement,
            (present?.Count ?? 0) > 0
                ? "Changing the backend does not move objects that are already stored. Until a "
                  + "migration job exists — one that copies, verifies every hash and only then "
                  + "repoints — switching would strand the evidence this deployment already holds. "
                  + "Destroy it deliberately, or keep this backend."
                : null);
    }

    /// <summary>Runs the round trip against a candidate store and persists nothing.</summary>
    public async Task<EvidenceCommissioningResponse> TestAsync(
        EvidenceBackendRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = await _db.GetSettingsAsync(ct);
        var prepared = await PrepareAsync(request, settings, ct);

        return prepared.Failure ?? await CommissionAsync(prepared, ct);
    }

    /// <summary>
    /// Runs the round trip and, only if it passed, saves the backend and repoints the live store.
    /// </summary>
    /// <param name="actor">Who is saving. Recorded against a durability acknowledgement.</param>
    public async Task<EvidenceCommissioningResponse> SaveBackendAsync(
        EvidenceBackendRequest request, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = await _db.GetSettingsAsync(ct);
        var prepared = await PrepareAsync(request, settings, ct);

        if (prepared.Failure is { } refused)
            return refused;

        // Selecting "None" is the one save with nothing to commission: there is no store to write
        // a canary into. It is also the only way back out of a misconfiguration that cannot be
        // fixed in place, so it must not be gated on a round trip that cannot succeed.
        if (prepared.Options.Backend is EvidenceBackend.None)
        {
            settings.EvidenceBackend = (short)EvidenceBackend.None;
            settings.EvidenceStoreId = null;
            await _db.SaveChangesAsync(ct);
            await ApplyAsync(settings, ct);

            return new EvidenceCommissioningResponse(
                true,
                null,
                "Evidence storage is switched off. Uploads are refused and nothing else is "
                + "affected; anything already stored is still where it was.",
                null,
                false,
                null);
        }

        var result = await CommissionAsync(prepared, ct);
        if (!result.Succeeded)
            return result;

        Persist(settings, request, prepared, actor);
        await _db.SaveChangesAsync(ct);
        await ApplyAsync(settings, ct);

        return result;
    }

    /// <summary>Saves the caps and the delivery toggle. No round trip: no store is repointed.</summary>
    public async Task<(EvidenceLimitsView? Saved, string? Error)> SaveLimitsAsync(
        EvidenceLimitsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.MaxFileBytes <= 0)
            return (null, "The per-file cap must be a positive number of bytes.");

        if (request.MaxReportBytes < 0 || request.MaxDeploymentBytes < 0)
            return (null, "A total cap cannot be negative. Use 0 for no limit.");

        if (request.MaxReportBytes > 0 && request.MaxReportBytes < request.MaxFileBytes)
        {
            return (null, "The per-report total is below the per-file cap, so no file that passed "
                + "the first check could ever be attached.");
        }

        var settings = await _db.GetSettingsAsync(ct);

        settings.EvidenceMaxFileBytes = request.MaxFileBytes;
        settings.EvidenceMaxReportBytes = request.MaxReportBytes;
        settings.EvidenceMaxDeploymentBytes = request.MaxDeploymentBytes;
        settings.EvidenceDirectDeliveryEnabled = request.DirectDeliveryEnabled;

        await _db.SaveChangesAsync(ct);
        await ApplyAsync(settings, ct);

        return (new EvidenceLimitsView(
            settings.EvidenceMaxFileBytes,
            settings.EvidenceMaxReportBytes,
            settings.EvidenceMaxDeploymentBytes,
            settings.EvidenceDirectDeliveryEnabled), null);
    }

    /// <summary>Re-reads the sentinel (design §8.3) and returns the verdict.</summary>
    public async Task<EvidenceHealthView> ProbeAsync(CancellationToken ct = default)
        => Describe(await _monitor.CheckAsync(ct), _store.Description);

    private async Task ApplyAsync(Core.Data.Entities.Settings settings, CancellationToken ct)
    {
        EvidenceSettingsBinder.ApplyTo(
            _live, settings, _protector, _db.Database.GetConnectionString());

        _store.Reload();
        await _monitor.CheckAsync(ct);
    }

    /// <summary>
    /// Turns a request into a candidate store, or into the reason it cannot become one.
    /// </summary>
    private async Task<Candidate> PrepareAsync(
        EvidenceBackendRequest request, Core.Data.Entities.Settings settings, CancellationToken ct)
    {
        if (EvidenceSettingsBinder.ParseBackend(request.Backend) is not { } backend)
            return Candidate.Refused("backend", $"'{request.Backend}' is not a storage backend Modbot has.");

        var options = new EvidenceOptions
        {
            Backend = backend,
            MaxFileBytes = settings.EvidenceMaxFileBytes,
            MaxReportBytes = settings.EvidenceMaxReportBytes,
            MaxDeploymentBytes = settings.EvidenceMaxDeploymentBytes,
            DirectDeliveryEnabled = settings.EvidenceDirectDeliveryEnabled,
        };

        options.Database.ConnectionString = _db.Database.GetConnectionString();

        var stored = EvidenceSettingsBinder.ToBackend(settings.EvidenceBackend);
        var sameTarget = SameTarget(settings, stored, backend, request);

        if (!sameTarget
            && await _db.EvidenceBlobs.AnyAsync(b => b.DestroyedAt == null, ct))
        {
            return Candidate.Refused(
                "switch",
                "This deployment already holds evidence in the store it is configured with, and "
                + "changing the backend does not move it. A migration between backends copies every "
                + "object, verifies it by hash and only then repoints — it is an explicit job, never "
                + "a side effect of saving a setting, and it does not exist yet.");
        }

        switch (backend)
        {
            case EvidenceBackend.Filesystem:
                if (string.IsNullOrWhiteSpace(request.Root))
                    return Candidate.Refused("root", "The filesystem backend needs a directory to write into.");

                options.Filesystem.Root = request.Root.Trim();
                break;

            case EvidenceBackend.S3:
                if (string.IsNullOrWhiteSpace(request.Bucket))
                    return Candidate.Refused("bucket", "A bucket name is required.");

                if (string.IsNullOrWhiteSpace(request.Endpoint))
                    return Candidate.Refused("endpoint", "An endpoint is required, with its scheme — https://…");

                if (!Uri.TryCreate(request.Endpoint.Trim(), UriKind.Absolute, out var endpoint)
                    || endpoint.Scheme is not ("http" or "https"))
                {
                    return Candidate.Refused(
                        "endpoint",
                        $"'{request.Endpoint}' is not an endpoint URL. It needs a scheme, as in "
                        + "https://s3.example.com.");
                }

                if (string.IsNullOrWhiteSpace(request.AccessKeyId))
                    return Candidate.Refused("credentials", "An access key id is required.");

                var secret = ResolveSecret(request, settings, stored);
                if (string.IsNullOrWhiteSpace(secret))
                {
                    return Candidate.Refused(
                        "credentials",
                        "A secret access key is required. Modbot has none on file for this bucket.");
                }

                options.S3.Bucket = request.Bucket.Trim();
                options.S3.Endpoint = endpoint.ToString();
                options.S3.AccessKeyId = request.AccessKeyId.Trim();
                options.S3.SecretAccessKey = secret;
                options.S3.Region = string.IsNullOrWhiteSpace(request.Region) ? "us-east-1" : request.Region.Trim();
                options.S3.Prefix = string.IsNullOrWhiteSpace(request.Prefix) ? null : request.Prefix.Trim();
                options.S3.UsePathStyle = request.UsePathStyle;
                break;

            case EvidenceBackend.Database:
                if (string.IsNullOrWhiteSpace(options.Database.ConnectionString))
                {
                    return Candidate.Refused(
                        "connection",
                        "Modbot could not work out its own database connection, so it cannot store "
                        + "evidence in it.");
                }

                break;

            case EvidenceBackend.None:
            default:
                break;
        }

        var durability = backend is EvidenceBackend.Filesystem
            ? PrepareDurability(options.Filesystem, request, settings, stored)
            : null;

        if (durability is { } assessment)
        {
            if (assessment.Finding is DurabilityFinding.Unwritable)
                return Candidate.Refused("root", assessment.Message);

            if (!assessment.CanProceed)
            {
                return new Candidate(
                    options,
                    null,
                    Guid.Empty,
                    new EvidenceCommissioningResponse(
                        false,
                        "durability",
                        assessment.Message,
                        null,
                        RequiresAcknowledgement: true,
                        Durability: ToView(assessment, null, null, null)));
            }
        }

        // Reusing the id when the target has not moved is what keeps a re-test from rewriting the
        // sentinel of the store Modbot is already using with an id Settings does not know about —
        // which would latch the store the instant the operator pressed Test.
        var storeId = sameTarget && settings.EvidenceStoreId is { } existing ? existing : Guid.NewGuid();

        return new Candidate(options, durability, storeId, null);
    }

    private async Task<EvidenceCommissioningResponse> CommissionAsync(
        Candidate candidate, CancellationToken ct)
    {
        var store = ReloadableEvidenceStore.Create(
            candidate.Options, _clock, _store.Connections);

        try
        {
            var result = await _commissioner.CommissionAsync(store, candidate.StoreId, null, ct);

            return new EvidenceCommissioningResponse(
                result.Succeeded,
                result.FailedStep,
                result.Succeeded ? result.Message + " " + DurabilityStatement : result.Message,
                result.StoreId,
                RequiresAcknowledgement: false,
                Durability: candidate.Durability is { } d ? ToView(d, null, null, null) : null);
        }
        finally
        {
            // A candidate is built to be thrown away. Its S3 client owns an HTTP handler, and a
            // settings page somebody is fiddling with would otherwise leak one per press.
            (store as IDisposable)?.Dispose();
        }
    }

    private void Persist(
        Core.Data.Entities.Settings settings,
        EvidenceBackendRequest request,
        Candidate candidate,
        string actor)
    {
        var options = candidate.Options;

        settings.EvidenceBackend = (short)options.Backend;
        settings.EvidenceStoreId = candidate.StoreId;

        if (options.Backend is EvidenceBackend.Filesystem)
        {
            settings.EvidenceRoot = options.Filesystem.Root;

            if (candidate.Durability is { RequiresAcknowledgement: true }
                && string.Equals(
                    request.AcknowledgeWarning,
                    EvidenceDurabilityAdvisor.UnprovenWarning,
                    StringComparison.Ordinal))
            {
                // Verbatim, and with it who and when. A reworded warning must not retroactively
                // change what somebody agreed to, so what is stored is the sentence that was on
                // their screen rather than a pointer to whatever the text says today.
                settings.EvidenceDiskAcknowledged = true;
                settings.EvidenceDiskAcknowledgedBy = actor;
                settings.EvidenceDiskAcknowledgedAt = _clock.UtcNow;
                settings.EvidenceDiskWarningShown = request.AcknowledgeWarning;
            }
        }

        if (options.Backend is EvidenceBackend.S3)
        {
            settings.EvidenceS3Bucket = options.S3.Bucket;
            settings.EvidenceS3Endpoint = options.S3.Endpoint;
            settings.EvidenceS3AccessKeyId = options.S3.AccessKeyId;
            settings.EvidenceS3Region = options.S3.Region;
            settings.EvidenceS3Prefix = options.S3.Prefix;
            settings.EvidenceS3UsePathStyle = options.S3.UsePathStyle;
            settings.EvidenceS3SecretAccessKeyEncrypted = _protector.Protect(options.S3.SecretAccessKey);
        }
    }

    /// <summary>
    /// Works out whether an acknowledgement is still needed, and whether one already covers this
    /// exact directory and this exact wording.
    /// </summary>
    /// <remarks>
    /// Tied to both, deliberately. An acknowledgement is about a particular disk — carrying one
    /// over to a different path would let a second directory inherit consent nobody gave for it —
    /// and about a particular sentence, because that sentence is the thing that was agreed to.
    /// </remarks>
    private DurabilityAssessment PrepareDurability(
        FilesystemEvidenceOptions candidate,
        EvidenceBackendRequest request,
        Core.Data.Entities.Settings settings,
        EvidenceBackend stored)
    {
        var carriedOver = settings.EvidenceDiskAcknowledged
            && stored is EvidenceBackend.Filesystem
            && string.Equals(settings.EvidenceRoot, candidate.Root, StringComparison.Ordinal)
            && string.Equals(
                settings.EvidenceDiskWarningShown,
                EvidenceDurabilityAdvisor.UnprovenWarning,
                StringComparison.Ordinal);

        var offered = string.Equals(
            request.AcknowledgeWarning,
            EvidenceDurabilityAdvisor.UnprovenWarning,
            StringComparison.Ordinal);

        candidate.Durability.UseAnyway = carriedOver || offered;

        return EvidenceDurabilityAdvisor.Assess(candidate, _platform, _bootId.Value);
    }

    private EvidenceDurabilityView DescribeDurability(FilesystemEvidenceOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Root))
        {
            return new EvidenceDurabilityView(
                nameof(DurabilityFinding.Unwritable),
                "No directory is configured for the filesystem backend.",
                RequiresAcknowledgement: false,
                Acknowledged: false,
                CanProceed: false,
                IsSuspicion: false,
                null,
                null,
                null);
        }

        var assessment = EvidenceDurabilityAdvisor.Assess(options, _platform, _bootId.Value);

        return ToView(
            assessment,
            options.Durability.AcknowledgedBy,
            options.Durability.AcknowledgedAt,
            options.Durability.WarningShown);
    }

    private static EvidenceDurabilityView ToView(
        DurabilityAssessment assessment, string? by, DateTimeOffset? at, string? shown)
        => new(
            assessment.Finding.ToString(),
            assessment.Message,
            assessment.RequiresAcknowledgement,
            assessment.Acknowledged,
            assessment.CanProceed,
            assessment.IsSuspicion,
            by,
            at,
            shown);

    private static EvidenceHealthView Describe(EvidenceStoreHealth health, string description)
        => new(
            health.State.ToString(),
            health.Explanation,
            health.ExpectedStoreId,
            health.FoundStoreId,
            health.Since,
            health.ConsecutiveFailures,
            health.UploadsAllowed,
            health.State is EvidenceStoreState.Unavailable,
            description);

    private static EvidenceCapabilitiesView Describe(
        EvidenceStoreCapabilities capabilities, bool directDeliveryEnabled)
    {
        var canPresign = capabilities.HasFlag(EvidenceStoreCapabilities.PresignedRead);
        var direct = canPresign && directDeliveryEnabled;

        return new EvidenceCapabilitiesView(
            canPresign,
            capabilities.HasFlag(EvidenceStoreCapabilities.PresignedWrite),
            capabilities.HasFlag(EvidenceStoreCapabilities.RangeRead),
            capabilities.HasFlag(EvidenceStoreCapabilities.ServerSideCopy),
            direct,
            direct
                ? "Evidence is delivered by the store straight to the browser, on a link that lasts "
                  + "five minutes. Modbot never sees those bytes, so it cannot re-hash them on the "
                  + "way past and cannot add its own response headers — the format allowlist is "
                  + "doing that work instead."
                : canPresign
                    ? "Direct delivery is switched off, so every byte is streamed through Modbot and "
                      + "re-hashed on the way past. That costs egress and latency and buys a verified "
                      + "read and stricter response headers."
                    : "This backend cannot hand the browser a link, so every byte is streamed through "
                      + "Modbot and re-hashed on the way past.");
    }

    /// <summary>
    /// Whether the request points at the same physical store the settings row already names.
    /// </summary>
    private static bool SameTarget(
        Core.Data.Entities.Settings settings,
        EvidenceBackend stored,
        EvidenceBackend requested,
        EvidenceBackendRequest request)
    {
        if (stored != requested)
            return false;

        return requested switch
        {
            EvidenceBackend.Filesystem => string.Equals(
                settings.EvidenceRoot?.Trim(), request.Root?.Trim(), StringComparison.Ordinal),

            // The credentials may be rotated without the store moving; the bucket, the endpoint and
            // the prefix are what decide which objects the store can see.
            EvidenceBackend.S3 =>
                string.Equals(settings.EvidenceS3Bucket?.Trim(), request.Bucket?.Trim(), StringComparison.Ordinal)
                && string.Equals(settings.EvidenceS3Endpoint?.Trim(), request.Endpoint?.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(Normalise(settings.EvidenceS3Prefix), Normalise(request.Prefix), StringComparison.Ordinal),

            EvidenceBackend.Database => true,
            _ => true,
        };

        static string Normalise(string? prefix) => prefix?.Trim().Trim('/') ?? string.Empty;
    }

    /// <summary>
    /// The stored secret, the submitted one, or — at first configuration only — the environment's.
    /// </summary>
    /// <remarks>
    /// Design §16.1. The environment may pre-fill the wizard once and never decides anything after
    /// that: re-reading it on every boot would let an edit to a Railway variable silently repoint
    /// the store at a different bucket, which is exactly the trap §8 exists to catch, introduced
    /// deliberately by the convenience meant to smooth setup.
    /// </remarks>
    private string? ResolveSecret(
        EvidenceBackendRequest request, Core.Data.Entities.Settings settings, EvidenceBackend stored)
    {
        if (!string.IsNullOrWhiteSpace(request.SecretAccessKey))
            return request.SecretAccessKey.Trim();

        if (stored is EvidenceBackend.S3 && settings.EvidenceS3SecretAccessKeyEncrypted is { Length: > 0 })
            return _protector.Unprotect(settings.EvidenceS3SecretAccessKeyEncrypted);

        return stored is EvidenceBackend.None
            ? Environment.GetEnvironmentVariable("SECRET_ACCESS_KEY")
            : null;
    }

    /// <remarks>
    /// These names are generic enough — <c>BUCKET</c>, <c>REGION</c> — to belong to something else
    /// entirely, which is a second reason they are a hint requiring confirmation and never
    /// configuration.
    /// </remarks>
    private static EvidenceEnvironmentHint? ReadEnvironmentHint()
    {
        var bucket = Environment.GetEnvironmentVariable("BUCKET");
        var endpoint = Environment.GetEnvironmentVariable("ENDPOINT");
        var accessKeyId = Environment.GetEnvironmentVariable("ACCESS_KEY_ID");
        var secret = Environment.GetEnvironmentVariable("SECRET_ACCESS_KEY");

        if (string.IsNullOrWhiteSpace(bucket)
            || string.IsNullOrWhiteSpace(endpoint)
            || string.IsNullOrWhiteSpace(accessKeyId)
            || string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

        return new EvidenceEnvironmentHint(
            bucket,
            endpoint,
            Environment.GetEnvironmentVariable("REGION"),
            accessKeyId,
            SecretAvailable: true);
    }

    /// <param name="Failure">Non-null when the request cannot become a store at all.</param>
    private sealed record Candidate(
        EvidenceOptions Options,
        DurabilityAssessment? Durability,
        Guid StoreId,
        EvidenceCommissioningResponse? Failure)
    {
        public static Candidate Refused(string step, string message)
            => new(new EvidenceOptions(), null, Guid.Empty,
                new EvidenceCommissioningResponse(false, step, message, null, false, null));
    }
}
