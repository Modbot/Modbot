using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.VRChat.Pacing;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Scheduling;
using Modbot.VRChat.Session;
using Modbot.VRChat.Sync;

namespace Modbot.VRChat;

/// <summary>
/// Registers the gate and everything it refuses to work without.
/// </summary>
/// <remarks>
/// One method, and all of it singleton, because the guarantees are process-wide: one session, one
/// set of buckets, one queue. A scoped gate would mean a per-request rate limiter, which is not a
/// rate limiter.
/// </remarks>
public static class VRChatServiceCollectionExtensions
{
    /// <summary>
    /// Registers the live view of spec 4.2.1's operator-configured rates, once.
    /// </summary>
    /// <remarks>
    /// Both entry points call it, because both need it and either may be used alone; the
    /// <c>TryAdd</c> is what makes calling both harmless. A second provider would be a second
    /// cache, which means two answers to "what is the rate right now" and a limiter and a
    /// producer that can disagree about it.
    /// </remarks>
    private static void AddSyncPacing(IServiceCollection services) =>
        services.TryAddSingleton<ISyncPacingSource>(provider => new SyncPacingProvider(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            provider.GetService<SyncPacingBaseline>()));

    public static IServiceCollection AddModbotVRChat(
        this IServiceCollection services,
        VRChatClientOptions? clientOptions = null,
        RateLimitOptions? rateLimits = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IVRChatClientFactory>(_ => new VRChatClientFactory(clientOptions));
        services.AddSingleton<IVRChatConnectionStore, SettingsConnectionStore>();

        services.AddSingleton<IMonotonicClock, StopwatchMonotonicClock>();
        services.AddSingleton<IDelayScheduler, RealDelayScheduler>();
        services.AddSingleton<IRateLimitStore, DatabaseRateLimitStore>();

        // The operator's rates (spec 4.2.1). Registered with the gate rather than with the
        // producers, because the limiter needs them too and a host may want VRChat access
        // without background sync.
        AddSyncPacing(services);

        services.AddSingleton<IRateLimiter>(provider => new InProcessRateLimiter(
            provider.GetRequiredService<IRateLimitStore>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            provider.GetRequiredService<IDelayScheduler>(),
            rateLimits,
            provider.GetRequiredService<ISyncPacingSource>()));

        services.AddSingleton<IVRChatGate>(provider => new VRChatGate(
            provider.GetRequiredService<IVRChatClientFactory>(),
            provider.GetRequiredService<IVRChatConnectionStore>(),
            provider.GetRequiredService<IRateLimiter>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            provider.GetRequiredService<IMonotonicClock>()));

        return services;
    }

    /// <summary>
    /// Registers the fact producers: the group audit log, and the group's own metadata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="AddModbotVRChat"/> because these two write to the fact log, and
    /// the gate does not. A host that wants VRChat access without background sync -- a test host,
    /// a one-shot administrative command -- should not have to start hosted services to get it.
    /// </para>
    /// <para>
    /// <strong>Requires <c>AddModbotAnalytics</c>.</strong> The producers resolve
    /// <c>IFactWriter</c> and the partition maintainer from it; a producer with nowhere to write
    /// is the state Modbot has been in until now, and it should fail loudly at startup rather
    /// than quietly at the first fact.
    /// </para>
    /// <para>
    /// Two hosted services, not one, because spec 4.3.1's cold stop is scoped to a bucket: a 429
    /// on <c>groups.read</c> must not stop audit-log ingestion, which spec 4.2.3 singles out as
    /// the one to protect. Sharing a loop would silently couple them.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddModbotVRChatSync(
        this IServiceCollection services,
        AuditLogSyncOptions? auditLog = null,
        GroupInfoSyncOptions? groupInfo = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var auditLogOptions = (auditLog ?? new AuditLogSyncOptions()).Clamped();
        var groupInfoOptions = (groupInfo ?? new GroupInfoSyncOptions()).Clamped();

        // The arguments to this method become the baseline the operator's own lowerings sit on
        // top of, so a host that deliberately passed a gentler cadence keeps it. Registered as a
        // service rather than captured, because AddModbotVRChat may already have registered the
        // provider and the factory has not run yet -- so the later registration is still visible
        // to it.
        services.AddSingleton(new SyncPacingBaseline(auditLogOptions, groupInfoOptions));

        // Also registered here, so a host that wires the producers without the gate still has a
        // pacing source rather than silently falling back to the compiled-in cadence.
        AddSyncPacing(services);

        // The startup values, which are also the fallback whenever the settings row has nothing
        // configured. Every consumer below prefers the live snapshot; these are what the snapshot
        // resolves to when the operator has changed nothing, and what an argument passed to this
        // method means.
        services.AddSingleton(auditLogOptions);
        services.AddSingleton(groupInfoOptions);

        // Singleton: the unmapped-event counters and the cadence decision are what an operator
        // reads to tell a quiet producer from a stuck one, and a per-scope copy would reset them
        // every poll.
        services.AddSingleton<SyncDiagnostics>();

        // Checks the mapping table against VRChat's own declared event types. Scoped so it shares
        // the sync's scope and runs after it, on its own endpoint class -- a 429 while verifying
        // must never cold-stop the history it was verifying (spec 4.3.4.1).
        services.AddScoped<AuditLogVocabulary>(provider => new AuditLogVocabulary(
            provider.GetRequiredService<IVRChatGate>(),
            provider.GetRequiredService<Core.Time.IModbotClock>()));

        // Scoped, because they hold a ModbotContext for the run and hand it back afterwards.
        //
        // The cadence comes from the live snapshot rather than the startup value, and from the
        // snapshot rather than a fresh read: the producer refreshed it immediately before
        // creating this scope, so the interval the tick waited and the page size this pass uses
        // came from one read of the settings row.
        services.AddScoped<GroupAuditLogSync>(provider => new GroupAuditLogSync(
            provider.GetRequiredService<IVRChatGate>(),
            provider.GetRequiredService<Analytics.Facts.IFactWriter>(),
            provider.GetRequiredService<Analytics.Facts.EventPartitionMaintainer>(),
            provider.GetRequiredService<Core.Data.ModbotContext>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            provider.GetRequiredService<SyncDiagnostics>(),
            provider.GetRequiredService<ISyncPacingSource>().Snapshot.AuditLog));

        services.AddScoped<GroupInfoSync>(provider => new GroupInfoSync(
            provider.GetRequiredService<IVRChatGate>(),
            provider.GetRequiredService<Analytics.Facts.IFactWriter>(),
            provider.GetRequiredService<Analytics.Facts.EventPartitionMaintainer>(),
            provider.GetRequiredService<Core.Data.ModbotContext>(),
            provider.GetRequiredService<Core.Time.IModbotClock>()));

        services.AddHostedService(provider => new GroupAuditLogSyncService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            provider.GetRequiredService<SyncDiagnostics>(),
            auditLogOptions,
            provider.GetRequiredService<ISyncPacingSource>()));

        services.AddHostedService(provider => new GroupInfoSyncService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            provider.GetRequiredService<SyncDiagnostics>(),
            provider.GetRequiredService<IMonotonicClock>(),
            groupInfoOptions,
            provider.GetRequiredService<ISyncPacingSource>()));

        return services;
    }
}
