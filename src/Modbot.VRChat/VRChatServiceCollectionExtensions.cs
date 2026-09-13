using Microsoft.Extensions.DependencyInjection;
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
        services.AddSingleton<IRateLimiter>(provider => new InProcessRateLimiter(
            provider.GetRequiredService<IRateLimitStore>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            provider.GetRequiredService<IDelayScheduler>(),
            rateLimits));

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
        services.AddScoped<GroupAuditLogSync>(provider => new GroupAuditLogSync(
            provider.GetRequiredService<IVRChatGate>(),
            provider.GetRequiredService<Analytics.Facts.IFactWriter>(),
            provider.GetRequiredService<Analytics.Facts.EventPartitionMaintainer>(),
            provider.GetRequiredService<Core.Data.ModbotContext>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            provider.GetRequiredService<SyncDiagnostics>(),
            auditLogOptions));

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
            auditLogOptions));

        services.AddHostedService(provider => new GroupInfoSyncService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            provider.GetRequiredService<SyncDiagnostics>(),
            provider.GetRequiredService<IMonotonicClock>(),
            groupInfoOptions));

        return services;
    }
}
