using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.Core.Discord;
using Modbot.Discord.Bot;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.Insights;
using Modbot.Discord.Instances;
using Modbot.Discord.ModerationLog;
using Modbot.Discord.ServerIndex;

namespace Modbot.Discord;

/// <summary>
/// Registers what Modbot does with Discord: a direct message to one person, and the bot of
/// foundation §9 -- a gateway session, three slash commands, and a moderation log channel.
/// </summary>
/// <remarks>
/// Registered unconditionally, like the VRChat gate: the bot reads its token from the database
/// and does nothing until one is stored, so a deployment without Discord pays for one settings
/// read every ten seconds and nothing else.
/// </remarks>
public static class DiscordServiceCollectionExtensions
{
    public static IServiceCollection AddModbotDiscord(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(nameof(DiscordRestMessenger));
        services.AddScoped<IDiscordMessenger, DiscordRestMessenger>();

        services.TryAddSingleton<DiscordBotOptions>();
        services.TryAddSingleton<ModerationLogOptions>();
        services.TryAddSingleton<IDiscordGatewayFactory, DiscordNetGatewayFactory>();

        services.AddSingleton<DiscordBotStatus>();
        services.AddSingleton<IDiscordBotStatus>(p => p.GetRequiredService<DiscordBotStatus>());

        services.AddSingleton<DiscordBotService>();
        services.AddHostedService(p => p.GetRequiredService<DiscordBotService>());
        services.AddHostedService<ModerationLogService>();

        // Its own loop: a deleted announcements channel must not hold up the
        // moderation log, which is the record rather than a notice board.
        services.AddHostedService<InstanceAnnounceService>();

        // Scheduled AI insights that name a channel (AI insights design §4). Its own loop too.
        services.AddHostedService<InsightPostService>();

        services.AddScoped<LookupQuery>();
        services.AddScoped<DiscordCommandHandler>();
        services.AddScoped<ModerationLogPoster>();
        services.AddScoped<InstanceAnnouncer>();
        services.AddScoped<DiscordServerIndex>();
        services.AddScoped<InsightPoster>();

        return services;
    }
}
