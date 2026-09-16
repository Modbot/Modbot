using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Security;
using Modbot.Core.Time;

namespace Modbot.Core.Cloud;

/// <summary>Wires up the public rooms report.</summary>
public static class PublicRoomsServices
{
    /// <summary>The name of the <see cref="HttpClient"/> the report is sent on.</summary>
    public const string HttpClientName = "modbot-public-rooms";

    /// <summary>
    /// Registers the report builder, the sender, the nudge and the loop that sends on a schedule.
    /// </summary>
    /// <remarks>
    /// Safe to call whatever <c>MODBOT_CLOUD_DISABLED</c> says: the builder returns nothing and the
    /// loop stops immediately when this server may not talk to Cloud. Registering it either way is
    /// what lets the settings endpoint read the switch without knowing whether the loop is running.
    /// </remarks>
    public static IServiceCollection AddPublicRooms(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<PublicRoomsNudge>();

        services.AddHttpClient(HttpClientName, http =>
        {
            // Short: nothing waits on this, and a Cloud that is slow should cost one skipped report
            // rather than a background loop parked for a minute.
            http.Timeout = TimeSpan.FromSeconds(20);
        });

        services.AddScoped<PublicRoomsReportBuilder>(provider => new PublicRoomsReportBuilder(
            provider.GetRequiredService<ModbotContext>(),
            provider.GetRequiredService<ModbotCloudAddress>()));

        services.AddScoped<PublicRoomsSender>(provider => new PublicRoomsSender(
            provider.GetRequiredService<ModbotContext>(),
            provider.GetRequiredService<PublicRoomsReportBuilder>(),
            provider.GetRequiredService<ModbotCloudAddress>(),
            provider.GetRequiredService<ISecretProtector>(),
            provider.GetRequiredService<IModbotClock>(),
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)));

        services.AddHostedService(provider => new PublicRoomsService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ModbotCloudAddress>(),
            provider.GetRequiredService<PublicRoomsNudge>()));

        return services;
    }
}
