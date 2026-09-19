using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Api.Features.Mcp;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Proxy;

/// <param name="BaseUrl">What a caller puts in front of a VRChat path: the public address and <c>/api/proxy/vrchat/</c>.</param>
/// <param name="PublicAddressSet">Whether the public address is saved. Without it the URL is this request's own address.</param>
/// <param name="ImagesProxied">Whether the web app loads VRChat pictures through Modbot.</param>
public sealed record VRChatProxySettingsResponse(
    bool Enabled,
    string BaseUrl,
    bool PublicAddressSet,
    bool ImagesProxied);

public sealed record VRChatProxySettingsUpdate(bool Enabled, bool ImagesProxied);

/// <summary>Settings → VRChat Proxy: the switch and the address (VRChat proxy design).</summary>
public static class VRChatProxySettingsEndpoints
{
    public static IEndpointRouteBuilder MapVRChatProxySettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/vrchat-proxy")
            .WithTags("VRChat proxy")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                CancellationToken ct) => Results.Ok(await ViewAsync(http, db, ct)))
            .WithName("GetVRChatProxySettings")
            .WithSummary("Get proxy settings")
            .WithDescription("The VRChat proxy's switch and address.")
            .Produces<VRChatProxySettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] VRChatProxySettingsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var row = await db.GetSettingsAsync(ct);
                var proxyMoved = row.VRChatProxyEnabled != body.Enabled;
                var imagesMoved = row.VRChatImagesProxied != body.ImagesProxied;

                if (proxyMoved || imagesMoved)
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);

                    row.VRChatProxyEnabled = body.Enabled;
                    row.VRChatImagesProxied = body.ImagesProxied;
                    await db.SaveChangesAsync(ct);

                    // One fact per switch, because they are read back one at a time: a log line
                    // saying "the VRChat proxy settings changed" leaves a reader to guess which.
                    if (proxyMoved)
                    {
                        await facts.RecordAsync(
                            FactType.SettingsChanged,
                            "settings",
                            Actor.Of(http),
                            new JsonObject
                            {
                                ["setting"] = "vrchatProxyEnabled",
                                ["before"] = !body.Enabled,
                                ["after"] = body.Enabled,
                            },
                            ct);
                    }

                    if (imagesMoved)
                    {
                        await facts.RecordAsync(
                            FactType.SettingsChanged,
                            "settings",
                            Actor.Of(http),
                            new JsonObject
                            {
                                ["setting"] = "vrchatImagesProxied",
                                ["before"] = !body.ImagesProxied,
                                ["after"] = body.ImagesProxied,
                            },
                            ct);
                    }

                    await transaction.CommitAsync(ct);
                }

                return Results.Ok(await ViewAsync(http, db, ct));
            })
            .WithName("SetVRChatProxySettings")
            .WithSummary("Update proxy settings")
            .WithDescription("Turn the VRChat proxy on or off.")
            .Produces<VRChatProxySettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<VRChatProxySettingsResponse> ViewAsync(HttpContext http, ModbotContext db, CancellationToken ct)
    {
        var row = await db.GetSettingsAsync(ct);

        // The same address rule the MCP server uses: the saved public address, or the address
        // this request came to, because the answer goes back to whoever asked about it.
        var address = await McpAddress.ForAsync(http, db, ct);

        return new VRChatProxySettingsResponse(
            row.VRChatProxyEnabled,
            address.Origin + VRChatProxyEndpoints.RoutePrefix,
            row.PublicAddress is not null,
            row.VRChatImagesProxied);
    }
}
