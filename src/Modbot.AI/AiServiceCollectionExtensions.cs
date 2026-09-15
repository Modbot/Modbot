using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.AI.Alerts;
using Modbot.AI.Calls;
using Modbot.AI.Insights;
using Modbot.AI.Moderation;
using Modbot.AI.Usage;
using Modbot.Core.Discord;
using Modbot.Core.Moderation;

namespace Modbot.AI;

/// <summary>
/// Registers <see cref="IAiClients"/>, the source of every AI client in Modbot (M8 section 4).
/// </summary>
/// <remarks>
/// Registered unconditionally, like the Discord bot and the VRChat gate: it reads its settings
/// from the database on each call and hands out nothing while AI is switched off, so a deployment
/// that never uses AI pays for nothing.
/// </remarks>
public static class AiServiceCollectionExtensions
{
    public static IServiceCollection AddModbotAi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(AiClients.HttpClientName);
        services.AddScoped<IAiClients, AiClients>();

        // Token counts by feature, and the spend limits every AI feature asks before a call: for
        // everyone, per feature, and for a person or a role in Chat (AI chat design §10).
        services.TryAddScoped<IAiUsage, AiUsageLedger>();

        // Every AI call goes through the runner: the feature's timeout around it, the fallback
        // model behind it, and a row in the call log whatever happens.
        services.TryAddScoped<IAiCallLog, AiCallLog>();
        services.AddScoped<AiCallRunner>();
        services.AddScoped<AiSpendLimits>();
        services.AddScoped<AiLimitNotices>();
        services.AddScoped<AiSpendReport>();
        services.AddScoped<AiTokenLimits>();

        // Email alerts plug in here, registered before this call so the real one wins.
        services.TryAddScoped<IAiSpendAlerts, NoAiSpendAlerts>();

        // Model prices from OpenRouter's public list. Its own client: never the VRChat one.
        services.AddHttpClient(OpenRouterPrices.HttpClientName);
        services.AddScoped<OpenRouterPrices>();

        services.AddScoped<InsightFigureReader>();
        services.AddScoped<InsightWriter>();
        services.AddScoped<InsightScheduler>();

        // Unusual-activity alerts (AI insights design §8). Every watcher is off until an operator
        // turns one on, so a pass is one small read.
        services.AddScoped<AlertFigureReader>();
        services.AddScoped<AlertChecker>();

        // AI moderation (AI moderation design). The engine reads its rules from the database on
        // every check, so like the client it does nothing until an operator switches it on.
        services.TryAddSingleton<TermListHubOptions>();
        services.AddHttpClient(HubTermLists.HttpClientName);
        services.AddSingleton<HubTermLists>();
        services.AddScoped<HubTermListUpdates>();
        services.AddSingleton<CompiledTermLists>();
        services.AddScoped<ModerationEngine>();
        services.AddScoped<IModerationChecker>(p => p.GetRequiredService<ModerationEngine>());
        services.AddScoped<ProfileModerationPass>();

        // The bot registers the real one first when it runs in this process (M8 §2).
        services.TryAddSingleton<IDiscordModerationActions, NoDiscordModerationActions>();

        return services;
    }

    /// <summary>
    /// Writes scheduled insights (AI insights design §3). Separate from <see cref="AddModbotAi"/> so
    /// a test host can use the insight classes without a loop running underneath it.
    /// </summary>
    public static IServiceCollection AddModbotAiInsightSchedule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<InsightScheduleService>();

        return services;
    }

    /// <summary>
    /// Fetches model prices daily and changes token limits into money limits once they have a price
    /// (AI chat design §10). Separate from <see cref="AddModbotAi"/> so a test host makes no call to
    /// OpenRouter.
    /// </summary>
    public static IServiceCollection AddModbotAiPriceFetch(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<AiPriceFetchService>();

        return services;
    }

    /// <summary>
    /// The two AI moderation loops: checking Hub lists for updates, and checking profile text as the
    /// profile sync records it.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AddModbotAi"/> so a test host can have the engine without loops
    /// running against its database.
    /// </remarks>
    public static IServiceCollection AddModbotAiModerationJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<HubTermListRefreshService>();
        services.AddHostedService<ProfileModerationService>();

        return services;
    }

    /// <summary>
    /// Deletes call log rows past the operator's keep-for setting, once a day. Separate from
    /// <see cref="AddModbotAi"/> so a test host has no loop deleting rows underneath it.
    /// </summary>
    public static IServiceCollection AddModbotAiCallLogPrune(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<AiCallLogPruneService>();

        return services;
    }
}
