using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Security;
using Modbot.Core.Time;

namespace Modbot.Core.Cloud;

/// <summary>Wires up the public instances report.</summary>
public static class PublicInstancesServices
{
    /// <summary>The name of the <see cref="HttpClient"/> the report is sent on.</summary>
    public const string HttpClientName = "modbot-public-instances";

    /// <summary>
    /// Registers the report builder, the sender, the nudge and the loop that sends on a schedule.
    /// </summary>
    /// <remarks>
    /// Safe to call whatever <c>MODBOT_CLOUD_DISABLED</c> says: the builder returns nothing and the
    /// loop stops immediately when this server may not talk to Cloud. Registering it either way is
    /// what lets the settings endpoint read the switch without knowing whether the loop is running.
    /// </remarks>
    public static IServiceCollection AddPublicInstances(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<PublicInstancesNudge>();

        services.AddHttpClient(HttpClientName, http =>
        {
            // Short: nothing waits on this, and a Cloud that is slow should cost one skipped report
            // rather than a background loop parked for a minute.
            http.Timeout = TimeSpan.FromSeconds(20);
        });

        services.AddScoped<PublicInstancesReportBuilder>(provider => new PublicInstancesReportBuilder(
            provider.GetRequiredService<ModbotContext>(),
            provider.GetRequiredService<ModbotCloudAddress>()));

        services.AddScoped<PublicInstancesSender>(provider => new PublicInstancesSender(
            provider.GetRequiredService<ModbotContext>(),
            provider.GetRequiredService<PublicInstancesReportBuilder>(),
            provider.GetRequiredService<ModbotCloudAddress>(),
            provider.GetRequiredService<ISecretProtector>(),
            provider.GetRequiredService<IModbotClock>(),
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)));

        services.AddHostedService(provider => new PublicInstancesService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ModbotCloudAddress>(),
            provider.GetRequiredService<PublicInstancesNudge>()));

        return services;
    }
}
