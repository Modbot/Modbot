using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Discord;

namespace Modbot.Discord;

/// <summary>Registers what Modbot can do with Discord today: send one person a direct message.</summary>
public static class DiscordServiceCollectionExtensions
{
    public static IServiceCollection AddModbotDiscord(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(nameof(DiscordRestMessenger));
        services.AddScoped<IDiscordMessenger, DiscordRestMessenger>();

        return services;
    }
}
