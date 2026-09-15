using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Evidence.Options;

namespace Modbot.Api.Features.Evidence;

/// <summary>
/// Maps the <see cref="Settings"/> row onto <see cref="EvidenceOptions"/>, and back.
/// </summary>
/// <remarks>
/// <para>
/// <c>Modbot.Evidence</c> deliberately refuses to read <c>Settings</c> itself — it owns no
/// migrations and takes its configuration as plain records — so somebody has to be the translator.
/// This is that, and it is the only place in Modbot that knows both spellings of a backend.
/// </para>
/// <para>
/// <strong>The S3 secret is decrypted here and nowhere else.</strong> It is stored encrypted like
/// every other secret column (foundation §8.3), which protects a database dump from casual reading
/// and nothing more — the key is in the same database. Overstating that is a documented mistake;
/// see <c>docs/content/docs/security.mdx</c>.
/// </para>
/// <para>
/// Binding <em>mutates</em> an existing options object rather than returning a new one, and that
/// is load-bearing rather than stylistic. <c>AddModbotEvidence</c> registers
/// <see cref="EvidenceOptions"/> and each of its three nested option objects as separate
/// singletons, all referring to the same instances. Replacing a nested object would leave the DI
/// container handing out the old one, so a backend the operator had just reconfigured would appear
/// to take effect in one place and not another — which is the shape of bug that gets diagnosed as
/// "the store is haunted".
/// </para>
/// </remarks>
public static class EvidenceSettingsBinder
{
    /// <summary>
    /// Writes the stored configuration onto a live options object.
    /// </summary>
    /// <param name="target">The singleton the store and the monitor were built from.</param>
    /// <param name="settings">The row.</param>
    /// <param name="protector">Decrypts the S3 secret. Nothing else here is encrypted.</param>
    /// <param name="databaseConnectionString">
    /// Supplied by the caller from the context Modbot is already using, so the in-database backend
    /// does not need a second connection string in <c>Settings</c> that could drift from the first.
    /// </param>
    public static void ApplyTo(
        EvidenceOptions target,
        Core.Data.Entities.Settings settings,
        ISecretProtector protector,
        string? databaseConnectionString = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(protector);

        target.Backend = ToBackend(settings.EvidenceBackend);
        target.StoreId = settings.EvidenceStoreId;
        target.MaxFileBytes = settings.EvidenceMaxFileBytes;
        target.MaxReportBytes = settings.EvidenceMaxReportBytes;
        target.MaxDeploymentBytes = settings.EvidenceMaxDeploymentBytes;
        // Always on. A store that can hand the browser a link does; the stored column is no longer read.
        target.DirectDeliveryEnabled = true;

        target.Filesystem.Root = settings.EvidenceRoot ?? string.Empty;
        target.Filesystem.Durability.UseAnyway = settings.EvidenceDiskAcknowledged;
        target.Filesystem.Durability.AcknowledgedBy = settings.EvidenceDiskAcknowledgedBy;
        target.Filesystem.Durability.AcknowledgedAt = settings.EvidenceDiskAcknowledgedAt;
        target.Filesystem.Durability.WarningShown = settings.EvidenceDiskWarningShown;

        target.S3.Bucket = settings.EvidenceS3Bucket ?? string.Empty;
        target.S3.Endpoint = settings.EvidenceS3Endpoint ?? string.Empty;
        target.S3.AccessKeyId = settings.EvidenceS3AccessKeyId ?? string.Empty;
        target.S3.Region = string.IsNullOrWhiteSpace(settings.EvidenceS3Region)
            ? "us-east-1"
            : settings.EvidenceS3Region;
        target.S3.Prefix = settings.EvidenceS3Prefix;
        target.S3.UsePathStyle = settings.EvidenceS3UsePathStyle;
        target.S3.SecretAccessKey =
            protector.Unprotect(settings.EvidenceS3SecretAccessKeyEncrypted) ?? string.Empty;

        if (databaseConnectionString is { Length: > 0 })
            target.Database.ConnectionString = databaseConnectionString;
    }

    /// <summary>
    /// Builds a detached options object from the same row, for a candidate store.
    /// </summary>
    /// <remarks>
    /// Used by the setup check (design §8.5), which has to build a store from values
    /// the operator has typed and Modbot has not saved. Detached on purpose: a candidate that
    /// shared the live options would repoint the running store the moment somebody pressed
    /// <em>Test</em>, before the test had said whether the new store works.
    /// </remarks>
    public static EvidenceOptions Read(
        Core.Data.Entities.Settings settings,
        ISecretProtector protector,
        string? databaseConnectionString = null)
    {
        var options = new EvidenceOptions();
        ApplyTo(options, settings, protector, databaseConnectionString);
        return options;
    }

    /// <summary>
    /// Maps the raw column onto the enum, treating an unknown value as "nothing configured".
    /// </summary>
    /// <remarks>
    /// A row written by a newer Modbot naming a backend this build does not have must not crash
    /// the deployment on startup. Refusing uploads and saying so is the same answer an
    /// unconfigured store gets, and it leaves the operator inside the settings page that is the
    /// only place to fix it (design §8.4).
    /// </remarks>
    public static EvidenceBackend ToBackend(short stored)
        => stored switch
        {
            (short)EvidenceBackend.S3 => EvidenceBackend.S3,
            (short)EvidenceBackend.Filesystem => EvidenceBackend.Filesystem,
            (short)EvidenceBackend.Database => EvidenceBackend.Database,
            _ => EvidenceBackend.None,
        };

    /// <summary>Parses the wire spelling of a backend. Case-insensitive; unknown means null.</summary>
    public static EvidenceBackend? ParseBackend(string? name)
        => name?.Trim().ToLowerInvariant() switch
        {
            "none" or "" => EvidenceBackend.None,
            "s3" => EvidenceBackend.S3,
            "filesystem" => EvidenceBackend.Filesystem,
            "database" => EvidenceBackend.Database,
            _ => null,
        };
}
