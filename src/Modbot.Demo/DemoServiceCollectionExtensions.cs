using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.Core.Configuration;

namespace Modbot.Demo;

/// <summary>
/// Registers the demo's seeder and its background service.
/// </summary>
/// <remarks>
/// Registered unconditionally, like the VRChat gate: whether this is a demo is not known until the
/// database has been reached, which is after the container is built. The background service asks
/// <see cref="DemoMode.IsOn"/> when it starts and does nothing at all when the answer is no.
/// </remarks>
public static class DemoServiceCollectionExtensions
{
    public static IServiceCollection AddModbotDemo(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<DemoState>();
        services.TryAddSingleton<DemoPlanHolder>();

        services.AddScoped<DemoSeeder>();
        services.AddScoped<DemoHistory>();

        services.AddSingleton<DemoDataService>();
        services.AddHostedService(sp => sp.GetRequiredService<DemoDataService>());

        return services;
    }
}
