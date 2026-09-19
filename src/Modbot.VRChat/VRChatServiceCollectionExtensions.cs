using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.VRChat.Pacing;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Scheduling;
using Modbot.VRChat.Session;
using Modbot.VRChat.Sync;
using Modbot.VRChat.Users;

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

        // The operator's address comes from the account code, which registers its own
        // IOperatorContact; until it does, the developer contact stands in (see the factory).
        services.TryAddSingleton<IOperatorContact, NoOperatorContact>();
        services.AddSingleton<IVRChatClientFactory>(provider => new VRChatClientFactory(
            clientOptions, provider.GetRequiredService<IOperatorContact>()));
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

        // The sign-in limit's record (spec 4.1.2). In the database, so a restart or a crash loop
        // cannot hand the hour's sign-ins back or cut a wait short.
        services.AddSingleton<IVRChatSignInStore, DatabaseSignInStore>();

        services.AddSingleton<IVRChatGate>(provider => new VRChatGate(
            provider.GetRequiredService<IVRChatClientFactory>(),
            provider.GetRequiredService<IVRChatConnectionStore>(),
            provider.GetRequiredService<IRateLimiter>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            provider.GetRequiredService<IMonotonicClock>(),
            signIns: provider.GetRequiredService<IVRChatSignInStore>(),
            rateLimits: rateLimits,
            clientOptions: clientOptions));

        // The bytes behind a picture address, for anything that cannot follow a VRChat address
        // itself -- a Discord card sends them, because Discord fetches pictures signed in as
        // nobody (Discord embeds design §3).
        services.AddSingleton<Core.Files.IPictures, Files.VRChatPictures>();

        return services;
    }

    /// <summary>
    /// Registers the fact producers: the group audit log, the group's own metadata, and the
    /// profiles of everyone either of those mentions.
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
    /// Five hosted services, not one, because spec 4.3.1's cold stop is scoped to a bucket: a
    /// 429 on <c>groups.read</c> must not stop audit-log ingestion, which spec 4.2.3 singles out
    /// as the one to protect; a 429 on the users lane must stop profile fetches and nothing
    /// else; and a 429 on <c>groups.members</c> must stop the member sweep and leave the ban
    /// sweep running. Sharing a loop would silently couple them.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddModbotVRChatSync(
        this IServiceCollection services,
        AuditLogSyncOptions? auditLog = null,
        GroupInfoSyncOptions? groupInfo = null,
        UserProfileSyncOptions? userProfile = null,
        GroupMemberSyncOptions? memberSweep = null,
        GroupBanSyncOptions? banSweep = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var auditLogOptions = (auditLog ?? new AuditLogSyncOptions()).Clamped();
        var groupInfoOptions = (groupInfo ?? new GroupInfoSyncOptions()).Clamped();
        var userProfileOptions = (userProfile ?? new UserProfileSyncOptions()).Clamped();
        var memberSweepOptions = (memberSweep ?? new GroupMemberSyncOptions()).Clamped();
        var banSweepOptions = (banSweep ?? new GroupBanSyncOptions()).Clamped();

        // The arguments to this method become the baseline the operator's own lowerings sit on
        // top of, so a host that deliberately passed a gentler poll rate keeps it. Registered as a
        // service rather than captured, because AddModbotVRChat may already have registered the
        // provider and the factory has not run yet -- so the later registration is still visible
        // to it.
        services.AddSingleton(new SyncPacingBaseline(
            auditLogOptions, groupInfoOptions, userProfileOptions, memberSweepOptions, banSweepOptions));

        // Also registered here, so a host that wires the producers without the gate still has a
        // pacing source rather than silently falling back to the compiled-in pollRate.
        AddSyncPacing(services);

        // The startup values, which are also the fallback whenever the settings row has nothing
        // configured. Every consumer below prefers the live snapshot; these are what the snapshot
        // resolves to when the operator has changed nothing, and what an argument passed to this
        // method means.
        services.AddSingleton(auditLogOptions);
        services.AddSingleton(groupInfoOptions);
        services.AddSingleton(userProfileOptions);
        services.AddSingleton(memberSweepOptions);
        services.AddSingleton(banSweepOptions);

        // Singleton: the unmapped-event counters and the poll rate decision are what an operator
        // reads to tell a quiet producer from a stuck one, and a per-scope copy would reset them
        // every poll.
        services.AddSingleton<SyncDiagnostics>();

        // One queue for the whole process (user profile sync design §3). The producer takes from
        // it, its own discovery and the API feed it, and a scoped copy would be a queue nobody
        // else could see.
        services.AddSingleton<UserRefreshQueue>();

        // Kick, ban and unban. Holds nothing but the gate, so a singleton is enough; it is here
        // rather than in the API so that the endpoint class, the interactive priority and the
        // ...WithHttpInfoAsync rule are decided once beside the syncs (M4 §4).
        services.AddSingleton<Moderation.GroupModeration>();
        services.AddSingleton<Moderation.GroupRoles>();

        // The join queue and the two answers to one of it, for the same reason and in the same
        // place: the endpoint classes and the interactive priority are decided beside the syncs.
        services.AddSingleton<Moderation.GroupJoinRequests>();

        // What an AutoMod rule set to act may do in the group (AutoMod design §5), through the
        // same wrapper. Scoped: it reads the settings row for the group and the signed-in account.
        services.AddScoped<Core.Moderation.IVRChatModerationActions, Moderation.AutoModVRChatActions>();

        // The one writer of vrchat_user rows. Scoped, because it holds a ModbotContext; used by
        // the profile sync, by the API's manual 18+ flag and refresh endpoints, and by anything
        // else that fetches a user object and should record having seen it.
        services.AddScoped<VRChatUserProfiles>(provider => new VRChatUserProfiles(
            provider.GetRequiredService<Core.Data.ModbotContext>(),
            provider.GetRequiredService<Analytics.Facts.IFactWriter>(),
            provider.GetRequiredService<Analytics.Facts.EventPartitionMaintainer>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            provider.GetRequiredService<UserRefreshQueue>(),
            provider.GetRequiredService<ISyncPacingSource>().Snapshot.UserProfile));

        // Scoped, because they hold a ModbotContext for the run and hand it back afterwards.
        //
        // The poll rate comes from the live snapshot rather than the startup value, and from the
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

        // The third producer: profiles, one user at a time, on the users lane (spec 4.2.5).
        services.AddScoped<UserProfileSync>(provider => new UserProfileSync(
            provider.GetRequiredService<IVRChatGate>(),
            provider.GetRequiredService<Core.Data.ModbotContext>(),
            provider.GetRequiredService<VRChatUserProfiles>(),
            provider.GetRequiredService<UserRefreshQueue>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            provider.GetRequiredService<SyncDiagnostics>(),
            provider.GetRequiredService<ISyncPacingSource>().Snapshot.UserProfile));

        services.AddHostedService(provider => new UserProfileSyncService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            provider.GetRequiredService<SyncDiagnostics>(),
            provider.GetRequiredService<UserRefreshQueue>(),
            provider.GetRequiredService<IMonotonicClock>(),
            userProfileOptions,
            provider.GetRequiredService<ISyncPacingSource>()));

        // The member and ban sweeps (member and ban sync design). Each reads one page per pass
        // and records sightings through the same writer the profile sync uses, so everyone on
        // either list gets a profile fetched in due course without a second fetch path.
        services.AddScoped<GroupMemberSync>(provider => new GroupMemberSync(
            provider.GetRequiredService<IVRChatGate>(),
            provider.GetRequiredService<Analytics.Facts.IFactWriter>(),
            provider.GetRequiredService<Analytics.Facts.EventPartitionMaintainer>(),
            provider.GetRequiredService<Core.Data.ModbotContext>(),
            provider.GetRequiredService<VRChatUserProfiles>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            provider.GetRequiredService<ISyncPacingSource>().Snapshot.MemberSweep));

        services.AddScoped<GroupBanSync>(provider => new GroupBanSync(
            provider.GetRequiredService<IVRChatGate>(),
            provider.GetRequiredService<Analytics.Facts.IFactWriter>(),
            provider.GetRequiredService<Analytics.Facts.EventPartitionMaintainer>(),
            provider.GetRequiredService<Core.Data.ModbotContext>(),
            provider.GetRequiredService<VRChatUserProfiles>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            provider.GetRequiredService<ISyncPacingSource>().Snapshot.BanSweep));

        services.AddHostedService(provider => new GroupMemberSyncService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            provider.GetRequiredService<SyncDiagnostics>(),
            provider.GetRequiredService<IMonotonicClock>(),
            memberSweepOptions,
            provider.GetRequiredService<ISyncPacingSource>()));

        // Places: which instances the group has open, and what the worlds they are in are called.
        //
        // The instance poll is the only way Modbot sees an instance nobody from the moderation team is
        // standing in -- a group event nobody has joined yet is otherwise invisible, and so is the
        // hour before the first moderator arrives. Its own bucket and its own service, so a cold
        // stop on worlds.read cannot take it down.
        services.AddScoped<Core.Data.PlaceStore>();

        services.AddScoped<GroupInstanceSync>(provider => new GroupInstanceSync(
            provider.GetRequiredService<IVRChatGate>(),
            provider.GetRequiredService<Core.Data.PlaceStore>(),
            provider.GetRequiredService<Core.Data.ModbotContext>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            // Optional: a host that does not report public instances registers no nudge, and the poll
            // is unchanged.
            provider.GetService<Core.Cloud.PublicInstancesNudge>(),
            // Optional for the same reason: without it the poll records the close and says nothing.
            provider.GetService<Core.Notifications.INotifier>()));

        services.AddScoped<WorldSync>(provider => new WorldSync(
            provider.GetRequiredService<IVRChatGate>(),
            provider.GetRequiredService<Core.Data.ModbotContext>(),
            provider.GetRequiredService<Core.Time.IModbotClock>()));

        services.AddHostedService(provider => new GroupInstanceSyncService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IMonotonicClock>()));

        services.AddHostedService(provider => new WorldSyncService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IMonotonicClock>()));

        // Each open group instance's own page, for its head count. Its own bucket (instances.read) and
        // its own service, so a cold stop here leaves the group list -- and the list's count as the
        // fallback -- untouched.
        services.AddScoped<InstanceHeadCountSync>(provider => new InstanceHeadCountSync(
            provider.GetRequiredService<IVRChatGate>(),
            provider.GetRequiredService<Core.Data.ModbotContext>(),
            provider.GetRequiredService<Core.Time.IModbotClock>()));

        services.AddHostedService(provider => new InstanceHeadCountSyncService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IMonotonicClock>()));

        // The one sign-in attempt after a wait (spec 4.1.2), made on time even when nothing else is
        // asking VRChat for anything.
        services.AddHostedService(provider => new SignInResumeService(
            provider.GetRequiredService<IVRChatGate>(),
            provider.GetRequiredService<IDelayScheduler>()));

        // The calendar's VRChat side (calendar design §9): moving events along, opening their
        // instances, and VRChat calendar writes, each on its own budget.
        services.AddScoped<Calendar.CalendarFacts>();
        services.AddScoped<Calendar.CalendarScheduler>();
        services.AddScoped<Calendar.CalendarOpener>(provider => new Calendar.CalendarOpener(
            provider.GetRequiredService<IVRChatGate>(),
            provider.GetRequiredService<Core.Data.ModbotContext>(),
            provider.GetRequiredService<Core.Data.PlaceStore>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            provider.GetRequiredService<Calendar.CalendarFacts>()));
        services.AddScoped<Calendar.CalendarVRChatPublisher>(provider => new Calendar.CalendarVRChatPublisher(
            provider.GetRequiredService<IVRChatGate>(),
            provider.GetRequiredService<Core.Data.ModbotContext>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            provider.GetRequiredService<Calendar.CalendarFacts>()));
        services.AddHostedService(provider => new Calendar.CalendarService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IDelayScheduler>()));

        services.AddHostedService(provider => new GroupBanSyncService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            provider.GetRequiredService<SyncDiagnostics>(),
            provider.GetRequiredService<IMonotonicClock>(),
            banSweepOptions,
            provider.GetRequiredService<ISyncPacingSource>()));

        return services;
    }
}
