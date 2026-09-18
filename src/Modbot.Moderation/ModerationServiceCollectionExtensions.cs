using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.Core.Discord;
using Modbot.Core.Moderation;

namespace Modbot.Moderation;

/// <summary>
/// Registers AutoMod: the engine, term lists, Hub lists and the language detector (AutoMod design §2).
/// </summary>
/// <remarks>
/// Everything is <c>TryAdd</c>, so a host may call this before or after <c>AddModbotAi</c>: the AI
/// project adds the real <see cref="IAiRuleChecker"/> on top, and without it the fallback answers
/// "AI is off" and every check is term lists alone. The Discord bot and the VRChat gate replace the
/// two action fallbacks the same way, when they run in this process.
/// </remarks>
public static class ModerationServiceCollectionExtensions
{
    public static IServiceCollection AddModbotModeration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<TermListHubOptions>();

        // The language on every flag (AI moderation design §18). The detector builds its profiles
        // once, so it is a singleton.
        services.TryAddSingleton<TextLanguage>();

        services.AddHttpClient(HubTermLists.HttpClientName);
        services.TryAddSingleton<HubTermLists>();
        services.TryAddScoped<HubTermListUpdates>();
        services.TryAddSingleton<CompiledTermLists>();
        services.TryAddScoped<ModerationEngine>();
        services.TryAddScoped<IModerationChecker>(p => p.GetRequiredService<ModerationEngine>());
        services.TryAddScoped<ProfileModerationPass>();

        // Replaced by the real ones where they exist: Modbot.AI, the Discord bot, the VRChat gate.
        services.TryAddScoped<IAiRuleChecker, NoAiRuleChecker>();
        services.TryAddScoped<IAiCallTexts, NoAiCallTexts>();
        services.TryAddSingleton<IDiscordModerationActions, NoDiscordModerationActions>();
        services.TryAddScoped<IVRChatModerationActions, NoVRChatModerationActions>();

        return services;
    }

    /// <summary>
    /// The two AutoMod loops: checking Hub lists for updates, and checking profile text as the
    /// profile sync records it.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AddModbotModeration"/> so a test host can have the engine without
    /// loops running against its database.
    /// </remarks>
    public static IServiceCollection AddModbotModerationJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<HubTermListRefreshService>();
        services.AddHostedService<ProfileModerationService>();

        return services;
    }
}
