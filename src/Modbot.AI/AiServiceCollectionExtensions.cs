using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // Token counts by feature, and each feature's spend limit. Every AI feature records here.
        services.TryAddScoped<IAiUsage, AiUsageLedger>();

        services.AddScoped<InsightFigureReader>();
        services.AddScoped<InsightWriter>();
        services.AddScoped<InsightScheduler>();

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
}
