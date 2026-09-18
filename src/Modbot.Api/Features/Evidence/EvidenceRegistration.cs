using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.Evidence.Health;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Storage.Database;

namespace Modbot.Api.Features.Evidence;

/// <summary>
/// Connects <c>AddModbotEvidence</c>'s registration-time options to configuration that lives in
/// the database and changes while Modbot is running.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The problem this solves.</strong> <c>AddModbotEvidence</c> takes an
/// <c>Action&lt;EvidenceOptions&gt;</c> and resolves it at registration — before the container
/// exists, before migrations have run, and therefore before there is a <c>Settings</c> row to read.
/// Left alone it takes its defaults, which is <c>EvidenceBackend.None</c>: the upload pipeline
/// refuses everything and no screen can change that.
/// </para>
/// <para>
/// <strong>How it is resolved.</strong> Options are bound <em>after</em> the migrations, from the
/// database, onto the very options object the store and the monitor were built from; and the store
/// itself is held behind <see cref="ReloadableEvidenceStore"/>, so a backend chosen in the UI takes
/// effect on the next request rather than the next deployment. Registration order matters: this
/// must be called after <c>AddModbotEvidence</c>, because it deliberately replaces two of that
/// method's registrations.
/// </para>
/// <para>
/// The monitor is replaced for a reason that is easy to miss. <c>AddModbotEvidence</c> hands it a
/// null store when the backend is <c>None</c> at registration, which is correct when the options
/// are final and wrong when they are about to be read from a database — a deployment that had not
/// yet chosen a backend would be stuck reporting <c>NotConfigured</c> for the life of the process,
/// including after the operator configured one. Here it always holds the reloadable store, which
/// <em>is</em> the unconfigured store until it is not.
/// </para>
/// </remarks>
public static class EvidenceRegistration
{
    /// <summary>Makes the evidence store reconfigurable from <c>Settings</c> at runtime.</summary>
    public static IServiceCollection AddModbotEvidenceSettings(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(provider => new ReloadableEvidenceStore(
            provider.GetRequiredService<EvidenceOptions>(),
            provider.GetRequiredService<IModbotClock>(),
            provider.GetService<IEvidenceConnectionFactory>()));

        services.AddSingleton<IEvidenceStore>(
            provider => provider.GetRequiredService<ReloadableEvidenceStore>());

        services.AddSingleton(provider => new EvidenceStoreMonitor(
            provider.GetRequiredService<IEvidenceStore>(),
            provider.GetRequiredService<EvidenceOptions>(),
            provider.GetRequiredService<IModbotClock>()));

        // One boot id for the process, so the persistence marker can tell "an earlier run wrote
        // this" from "I wrote it thirty milliseconds ago". Not a clock read; see PersistenceProbe.
        services.AddSingleton(new EvidenceBootId(Guid.NewGuid().ToString("n")));

        // Taken from what startup already worked out where the host recorded it, so the evidence
        // page and the data page cannot disagree about what this deployment is running on. Detected
        // only as a fallback, for a host that composes the API without that record.
        services.TryAddSingleton(provider =>
            provider.GetService<DeploymentInfo>()?.Platform ?? HostPlatform.Detect());

        services.AddScoped<EvidenceSettingsService>();

        return services;
    }

    /// <summary>
    /// Reads the stored configuration, rebuilds the store from it, and takes the startup store marker
    /// probe of design §8.3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called after migrations, because <c>Settings</c> has to exist. Nothing here is allowed to
    /// stop the host: a deployment whose evidence store is misconfigured still ingests the audit
    /// log, still records presence and still syncs bans, and design §8.4 is explicit that trading
    /// those for a dramatic gesture about data already lost is a bad trade.
    /// </para>
    /// <para>
    /// The probe's verdict is returned rather than acted on, so the caller decides what to log.
    /// </para>
    /// </remarks>
    public static async Task<EvidenceStoreHealth> LoadEvidenceSettingsAsync(
        this IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;

        var db = provider.GetRequiredService<ModbotContext>();
        var settings = await db.GetSettingsAsync(ct);

        EvidenceSettingsBinder.ApplyTo(
            provider.GetRequiredService<EvidenceOptions>(),
            settings,
            provider.GetRequiredService<ISecretProtector>(),
            db.Database.GetConnectionString());

        provider.GetRequiredService<ReloadableEvidenceStore>().Reload();

        var health = await provider.GetRequiredService<EvidenceStoreMonitor>().CheckAsync(ct);

        // Design §8.4's critical notification. Raised here rather than by the caller because this
        // is the only place the startup probe's verdict exists before anything else has read it.
        if (provider.GetService<Modbot.Core.Notifications.INotifier>() is { } notifier)
            await EvidenceStoreAlarm.RaiseIfLockedAsync(health, notifier, ct);

        return health;
    }
}

/// <summary>
/// A value unique to this process run, used to interpret the persistence marker.
/// </summary>
/// <remarks>
/// A type rather than a bare string so it can be resolved from the container without colliding
/// with every other string somebody might register. <c>PersistenceProbe</c> compares it against
/// what the marker file holds: a different value means an earlier run wrote it, which is proof the
/// directory survived a restart; the same value proves nothing at all.
/// </remarks>
/// <param name="Value">Opaque, stable for the life of the process.</param>
public sealed record EvidenceBootId(string Value);
