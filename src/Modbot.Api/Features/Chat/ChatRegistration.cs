using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.AI.Chat;
using Modbot.Api.Features.Chat.Tools;

namespace Modbot.Api.Features.Chat;

/// <summary>The Chat loop, its tool registry and the first set of tools (AI chat design §3.2).</summary>
public static class ChatRegistration
{
    public static IServiceCollection AddModbotChat(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Singletons: a tool keeps no state and takes everything scoped from the request's own
        // scope, handed to it in ChatToolContext.
        services.AddSingleton<IChatTool, FindPersonTool>();
        services.AddSingleton<IChatTool, GetPersonTool>();
        services.AddSingleton<IChatTool, PersonHistoryTool>();
        services.AddSingleton<IChatTool, PersonCasesTool>();
        services.AddSingleton<IChatTool, PersonBansTool>();
        services.AddSingleton<IChatTool, SearchMembersTool>();
        services.AddSingleton<IChatTool, SearchAuditLogTool>();
        services.AddSingleton<IChatTool, ListLiveRoomsTool>();
        services.AddSingleton<IChatTool, FindWorldTool>();
        services.AddSingleton<IChatTool, GetWorldTool>();
        services.AddSingleton<IChatTool, GetInstanceTool>();
        services.AddSingleton<IChatTool, GroupAnalyticsTool>();

        services.TryAddSingleton<ChatToolRegistry>();
        services.TryAddSingleton<ChatLoop>();

        return services;
    }
}
