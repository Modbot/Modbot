using Microsoft.Extensions.DependencyInjection;

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

        return services;
    }
}
