using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Retention;
using Modbot.Analytics.Rollups;
using Modbot.Analytics.Storage;

namespace Modbot.Analytics;

/// <summary>
/// Registers the analytics substrate: the fact log, the rollups derived from it, retention, and
/// the jobs that keep all three healthy.
/// </summary>
public static class AnalyticsServiceCollectionExtensions
{
    /// <remarks>
    /// The background services are registered alongside the things they maintain rather than left
    /// to the host to remember. A fact writer without partition maintenance fails every insert the
    /// moment the calendar moves on, and rollups nobody runs are charts that stop at install day.
    /// </remarks>
    public static IServiceCollection AddModbotAnalytics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IFactWriter, FactWriter>();
        services.AddScoped<EventPartitionMaintainer>();
        services.AddHostedService<EventPartitionMaintenanceService>();

        services.AddScoped<RollupJob>();
        services.AddScoped<IRollupCounter, RollupCounter>();
        services.AddHostedService<RollupService>();

        services.AddScoped<RetentionPruner>();
        services.AddScoped<IUserPurger, UserPurger>();
        services.AddHostedService<RetentionService>();

        // Measured on demand, not on a timer. It runs a handful of catalogue queries and a
        // count, which is cheap when somebody opens the settings page and pure waste every
        // fifteen minutes when nobody is looking at it.
        services.AddScoped<StorageProjector>();

        return services;
    }
}
