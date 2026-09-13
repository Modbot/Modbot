using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Time;
using Modbot.Evidence.Health;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Storage.Database;
using Modbot.Evidence.Storage.FileSystem;
using Modbot.Evidence.Storage.S3;
using Modbot.Evidence.Upload;

namespace Modbot.Evidence;

/// <summary>Registers the evidence store, its health latch and the upload pipeline.</summary>
public static class EvidenceServiceCollectionExtensions
{
    /// <summary>
    /// Wires evidence storage from already-resolved options.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The options are taken as plain records rather than read from <c>Settings</c>, so that
    /// nothing in this project depends on a migration and the host keeps the single job of mapping
    /// the settings row onto them. Foundation section 2.6 still holds: the database decides, and
    /// the environment only ever pre-fills the wizard.
    /// </para>
    /// <para>
    /// The store is a singleton because it holds a connection pool and, for S3, an HTTP client.
    /// The monitor is a singleton because the latch is process state — a scoped latch would clear
    /// itself on every request, which is the one thing design section 8.4 says it must never do.
    /// </para>
    /// <para>
    /// <see cref="IEvidenceMetadata"/> is <strong>not</strong> registered here. It is the Postgres
    /// side of evidence — the blob record and the attachment facts — and it belongs to whoever
    /// owns the schema.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddModbotEvidence(
        this IServiceCollection services, Action<EvidenceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new EvidenceOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton(options.Filesystem);
        services.AddSingleton(options.S3);
        services.AddSingleton(options.Database);

        services.AddSingleton<IEvidenceStore>(provider => CreateStore(provider, options));

        services.AddSingleton(provider => new EvidenceStoreMonitor(
            options.Backend is EvidenceBackend.None ? null : provider.GetRequiredService<IEvidenceStore>(),
            options,
            provider.GetRequiredService<IModbotClock>()));

        services.AddSingleton<EvidenceStoreCommissioner>();
        services.AddSingleton<IEvidenceUploadRegistry, InMemoryEvidenceUploadRegistry>();

        services.AddScoped<EvidenceUploadService>();
        services.AddScoped<EvidenceDestroyer>();
        services.AddScoped<StagingSweeper>();

        return services;
    }

    /// <summary>
    /// Builds the configured store. Public so that the settings page can construct a candidate
    /// store from unsaved values and run the commissioning round trip against it before anything
    /// is persisted (design section 8.5).
    /// </summary>
    public static IEvidenceStore CreateStore(EvidenceOptions options, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        return options.Backend switch
        {
            EvidenceBackend.Filesystem => new FilesystemEvidenceStore(options.Filesystem),
            EvidenceBackend.S3 => new S3EvidenceStore(options.S3, clock),
            EvidenceBackend.Database => new DatabaseEvidenceStore(
                new ConnectionStringEvidenceConnectionFactory(options.Database), options.Database),
            _ => new UnconfiguredEvidenceStore(),
        };
    }

    private static IEvidenceStore CreateStore(IServiceProvider provider, EvidenceOptions options)
    {
        // A host that already owns a connection pool can register its own factory and keep the
        // in-database backend on the same pool as everything else.
        if (options.Backend is EvidenceBackend.Database
            && provider.GetService<IEvidenceConnectionFactory>() is { } factory)
        {
            return new DatabaseEvidenceStore(factory, options.Database);
        }

        return CreateStore(options, provider.GetRequiredService<IModbotClock>());
    }
}
