using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Modbot.Api.Auth;
using Modbot.Core;
using Modbot.Core.Data.Entities;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

namespace Modbot.Api.Features.Mcp;

/// <summary>
/// The MCP server (MCP server design): Chat's tools over Streamable HTTP at <c>/mcp</c>, for a
/// person's own AI app.
/// </summary>
/// <remarks>
/// <para>
/// For whoever does not want to give Modbot a model of its own: Claude.ai, ChatGPT, Claude Code
/// or Cursor connects to this Modbot and runs the same tools the Chat page has, as the person who
/// signed in, with their permissions. Modbot sends nothing to any provider; the app asks and
/// Modbot answers.
/// </para>
/// <para>
/// Stateless: every request stands alone, which is what the hosted chats' connectors expect and
/// what lets the request's own scope -- its database context, its authenticated user -- be the
/// tool's scope, exactly as on the Chat page.
/// </para>
/// </remarks>
public static class McpRegistration
{
    public static IServiceCollection AddModbotMcp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();

        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "modbot",
                    Title = "Modbot",
                    Version = ModbotVersion.Release,
                };
                options.ServerInstructions =
                    "Tools that look things up in this VRChat group's Modbot: people, bans, the audit log, "
                    + "rooms, worlds, Discord messages and members, flags, the calendar and figures. Every "
                    + "answer is what the signed-in person may already see. Answer only from what the tools "
                    + "return, and never write an id a tool did not return.";
            })
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler(McpTools.ListAsync)
            .WithCallToolHandler(McpTools.CallAsync);

        // Its own scheme, named by the endpoint alone. AddAuthentication() here adds a scheme to
        // whatever AddModbotAuth set up; it does not change the default.
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, McpAuthenticationHandler>(McpAuthentication.Scheme, null);

        // Off answers 404, everywhere MCP.
        services.AddTransient<IStartupFilter, McpSwitchStartupFilter>();

        return services;
    }

    public static IEndpointRouteBuilder MapModbotMcp(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The MCP endpoint, behind the Mcp scheme and the same permission as the Chat page. The
        // default policy's VRChat-link requirement comes along, as everywhere.
        app.MapMcp(McpAddress.ServerPath)
            .WithMetadata(new AuthorizeAttribute { AuthenticationSchemes = McpAuthentication.Scheme })
            .RequiresFlag(ModbotPermissions.UseAiChat)
            .WithMetadata(new ExcludeFromDescriptionAttribute());

        app.MapMcpOAuth();
        app.MapMcpSettings();

        return app;
    }
}
