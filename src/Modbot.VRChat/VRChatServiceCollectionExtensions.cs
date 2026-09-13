using Microsoft.Extensions.DependencyInjection;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Scheduling;
using Modbot.VRChat.Session;

namespace Modbot.VRChat;

/// <summary>
/// Registers the gate and everything it refuses to work without.
/// </summary>
/// <remarks>
/// One method, and all of it singleton, because the guarantees are process-wide: one session, one
/// set of buckets, one queue. A scoped gate would mean a per-request rate limiter, which is not a
/// rate limiter.
/// </remarks>
public static class VRChatServiceCollectionExtensions
{
    public static IServiceCollection AddModbotVRChat(
        this IServiceCollection services,
        VRChatClientOptions? clientOptions = null,
        RateLimitOptions? rateLimits = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IVRChatClientFactory>(_ => new VRChatClientFactory(clientOptions));
        services.AddSingleton<IVRChatConnectionStore, SettingsConnectionStore>();

        services.AddSingleton<IMonotonicClock, StopwatchMonotonicClock>();
        services.AddSingleton<IDelayScheduler, RealDelayScheduler>();
        services.AddSingleton<IRateLimitStore, DatabaseRateLimitStore>();
        services.AddSingleton<IRateLimiter>(provider => new InProcessRateLimiter(
            provider.GetRequiredService<IRateLimitStore>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            provider.GetRequiredService<IDelayScheduler>(),
            rateLimits));

        services.AddSingleton<IVRChatGate>(provider => new VRChatGate(
            provider.GetRequiredService<IVRChatClientFactory>(),
            provider.GetRequiredService<IVRChatConnectionStore>(),
            provider.GetRequiredService<IRateLimiter>(),
            provider.GetRequiredService<Core.Time.IModbotClock>(),
            provider.GetRequiredService<IMonotonicClock>()));

        return services;
    }
}
