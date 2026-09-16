using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Data;

namespace Modbot.Cloud.Features.Registry;

/// <summary>
/// The registered server behind <c>Authorization: Bearer &lt;serverId&gt;.&lt;secret&gt;</c>.
/// </summary>
public static class ServerAccess
{
    /// <summary>Refuses anything that does not carry a registered server's own secret.</summary>
    public static TBuilder RequireServer<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;

            if (await ReadAsync(http) is null)
            {
                http.Response.Headers.WWWAuthenticate = "Bearer";
                return Results.Json(
                    new { error = "Register first." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return await next(context);
        });

    /// <summary>The calling server, or null. Read once per request and remembered.</summary>
    public static async Task<RegisteredServer?> ReadAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        const string itemKey = "modbot.server";

        if (http.Items.TryGetValue(itemKey, out var known))
            return known as RegisteredServer;

        RegisteredServer? server = null;

        if (ServerSecrets.TryRead(http.Request.Headers.Authorization.ToString(), out var id, out var secret))
        {
            var db = http.RequestServices.GetRequiredService<CloudContext>();
            var row = await db.RegisteredServers.FirstOrDefaultAsync(s => s.Id == id, http.RequestAborted);

            if (row is not null && ServerSecrets.Matches(row.SecretHash, secret))
                server = row;
        }

        http.Items[itemKey] = server;
        return server;
    }

    public static async Task<RegisteredServer> RequiredAsync(HttpContext http) =>
        await ReadAsync(http) ?? throw new InvalidOperationException("This endpoint needs RequireServer.");
}
