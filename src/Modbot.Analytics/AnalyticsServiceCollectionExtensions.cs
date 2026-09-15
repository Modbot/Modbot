using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Retention;
using Modbot.Analytics.DailyTotals;
using Modbot.Analytics.Reviews;
using Modbot.Analytics.Storage;

namespace Modbot.Analytics;

/// <summary>
/// Registers the analytics foundation: the fact log, the daily totals derived from it, retention, and
/// the jobs that keep all three healthy.
/// </summary>
public static class AnalyticsServiceCollectionExtensions
{
    /// <remarks>
    /// The background services are registered alongside the things they maintain rather than left
    /// to the host to remember. A fact writer without partition maintenance fails every insert the
    /// moment the calendar moves on, and daily totals nobody runs are charts that stop at install day.
    /// </remarks>
    public static IServiceCollection AddModbotAnalytics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IFactWriter, FactWriter>();
        services.AddScoped<EventPartitionMaintainer>();
        services.AddScoped<Messages.MessagePartitionMaintainer>();
        services.AddHostedService<EventPartitionMaintenanceService>();

        services.AddScoped<DailyTotalsJob>();
        services.AddScoped<IDailyTotalCounter, DailyTotalCounter>();
        services.AddHostedService<DailyTotalsService>();

        // Repeat offenders and moderator pattern reviews (spec 5.8). No hosted service of their
        // own: DailyTotalsService runs the review job after each daily totals run, because the
        // baselines are summed from the daily totals and must not be a run behind them.
        services.AddScoped<ReviewFacts>();
        services.AddScoped<ReviewJob>();

        services.AddScoped<RetentionPruner>();
        services.AddScoped<IUserPurger, UserPurger>();
        services.AddHostedService<RetentionService>();

        // Measured on demand when somebody opens the settings page, plus once a day for the
        // storage chart's history. More often would be a handful of catalogue queries and a
        // count, every time, for a line drawn one point per day.
        services.AddScoped<StorageEstimator>();
        services.AddScoped<StorageHistory>();
        services.AddHostedService<StorageHistoryService>();

        return services;
    }
}
