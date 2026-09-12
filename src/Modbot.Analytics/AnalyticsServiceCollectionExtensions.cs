using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;

namespace Modbot.Analytics;

/// <summary>
/// Registers the analytics substrate: the fact log and the job that keeps it writable.
/// </summary>
public static class AnalyticsServiceCollectionExtensions
{
    /// <remarks>
    /// <see cref="EventPartitionMaintenanceService"/> is registered alongside
    /// <see cref="IFactWriter"/> rather than left to the host to remember, because a fact writer
    /// without partition maintenance fails every insert the moment the calendar moves on.
    /// </remarks>
    public static IServiceCollection AddModbotAnalytics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IFactWriter, FactWriter>();
        services.AddScoped<EventPartitionMaintainer>();
        services.AddHostedService<EventPartitionMaintenanceService>();

        return services;
    }
}
