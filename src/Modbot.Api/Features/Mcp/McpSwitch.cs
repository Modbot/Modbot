using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;

namespace Modbot.Api.Features.Mcp;

/// <summary>
/// The MCP server's on/off switch: while it is off, everything MCP answers 404 (MCP server design).
/// </summary>
/// <remarks>
/// <para>
/// A 404 rather than a 403 or a JSON refusal, because "off" should look like "not here": an AI
/// app probing a Modbot whose operator never turned this on learns nothing about it, and a
/// person who pasted the address into their AI app gets the same answer a wrong address would.
/// </para>
/// <para>
/// Middleware at the front of the pipeline rather than a check inside each endpoint, because the
/// MCP endpoint itself is mapped by the SDK and the metadata documents are what an app reads
/// first: one place decides, before any of them run. Registered through a startup filter so a
/// host that adds the feature gets the switch with it, without a line in its pipeline to forget.
/// </para>
/// </remarks>
internal sealed class McpSwitchStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            app.Use(McpSwitch.InvokeAsync);
            next(app);
        };
    }
}

internal static class McpSwitch
{
    private static readonly PathString ServerPath = new(McpAddress.ServerPath);
    private static readonly PathString ResourceMetadata = new("/.well-known/oauth-protected-resource");
    private static readonly PathString ServerMetadata = new("/.well-known/oauth-authorization-server");

    /// <summary>The paths the switch guards: the server and its sign-in, and the two metadata documents.</summary>
    public static bool Guards(PathString path)
        => path.StartsWithSegments(ServerPath)
           || path.StartsWithSegments(ResourceMetadata)
           || path.StartsWithSegments(ServerMetadata);

    public static async Task InvokeAsync(HttpContext http, RequestDelegate next)
    {
        if (!Guards(http.Request.Path))
        {
            await next(http);
            return;
        }

        var db = http.RequestServices.GetRequiredService<ModbotContext>();
        var on = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.McpServerEnabled)
            .FirstOrDefaultAsync(http.RequestAborted);

        if (!on)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(http);
    }
}
