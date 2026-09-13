using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.Core.Discord;
using Modbot.Discord.Bot;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.ModerationLog;

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

        services.AddScoped<LookupQuery>();
        services.AddScoped<DiscordCommandHandler>();
        services.AddScoped<ModerationLogPoster>();

        return services;
    }
}
