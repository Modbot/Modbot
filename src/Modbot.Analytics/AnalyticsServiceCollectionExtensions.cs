using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        services.TryAddSingleton<FactSignal>();
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

        // Giveaways. Here rather than in the API because the Discord bot draws too -- a giveaway
        // whose draw time comes round while nobody is looking at a page still has to be drawn, and
        // a second answer to "who is in this" living in the API would be a second answer that can
        // disagree with the first (giveaways design §2.6).
        services.AddScoped<Giveaways.GiveawayRuleChecker>();
        services.AddScoped<Giveaways.GiveawayDrawer>();
        services.AddScoped<Giveaways.GiveawayScheduler>();
        services.AddHostedService<Giveaways.GiveawayService>();

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
