using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.AI.Chat;
using Modbot.Api.Auth;
using Modbot.Api.Features.Settings;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Mcp;

/// <param name="ServerUrl">Where an AI app connects: the public address and <c>/mcp</c>.</param>
/// <param name="PublicAddressSet">Whether the public address is saved. Without it the URL is this request's own address.</param>
/// <param name="Tools">Every tool, with its switch, as Settings → AI → Chat shows them.</param>
/// <param name="KeyPermissions">
/// The permissions an API key made for MCP should carry: Use AI chat and every tool's need,
/// narrowed to what the caller holds.
/// </param>
public sealed record McpSettingsResponse(
    bool Enabled,
    string ServerUrl,
    bool PublicAddressSet,
    IReadOnlyList<AiChatToolView> Tools,
    IReadOnlyList<string> KeyPermissions);

public sealed record McpSettingsUpdate(bool Enabled);

/// <summary>One AI app connected to the caller's account.</summary>
public sealed record McpConnectionView(
    Guid Id,
    string ClientName,
    string? ClientUri,
    DateTimeOffset ConnectedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset ExpiresAt);

/// <summary>Settings → AI → MCP: the switch, and each person's connected apps (MCP server design).</summary>
public static class McpSettingsEndpoints
{
    public static IEndpointRouteBuilder MapMcpSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var settings = app.MapGroup("/api/mcp/settings")
            .WithTags("MCP")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        settings.MapGet("", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] ChatToolRegistry registry,
                CancellationToken ct) =>
                Results.Ok(await ViewAsync(http, db, registry, ct)))
            .WithName("GetMcpSettings")
            .WithSummary("Get MCP settings")
            .WithDescription("The MCP server's switch, address and tools.")
            .Produces<McpSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        settings.MapPut("", async (
                HttpContext http,
                [FromBody] McpSettingsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] ChatToolRegistry registry,
                [FromServices] AccountFacts facts,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var row = await db.GetSettingsAsync(ct);
                if (row.McpServerEnabled != body.Enabled)
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);

                    row.McpServerEnabled = body.Enabled;
                    await db.SaveChangesAsync(ct);

                    await facts.RecordAsync(
                        FactType.SettingsChanged,
                        "settings",
                        Actor.Of(http),
                        new JsonObject
                        {
                            ["setting"] = "mcpServerEnabled",
                            ["before"] = !body.Enabled,
                            ["after"] = body.Enabled,
                        },
                        ct);

                    await transaction.CommitAsync(ct);
                }

                return Results.Ok(await ViewAsync(http, db, registry, ct));
            })
            .WithName("SetMcpSettings")
            .WithSummary("Update MCP settings")
            .WithDescription("Turn the MCP server on or off.")
            .Produces<McpSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        // A person's own connections. Any signed-in, linked account: the list is theirs.
        var connections = app.MapGroup("/api/mcp/connections")
            .WithTags("MCP")
            .RequireAuthorization();

        connections.MapGet("", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var userId = ModbotAuth.UserIdOf(http.User)!.Value;
                var now = clock.UtcNow;

                var grants = await db.McpGrants.AsNoTracking()
                    .Include(g => g.Client)
                    .Where(g => g.UserId == userId && g.RevokedAt == null && g.RefreshExpiresAt > now)
                    .OrderByDescending(g => g.CreatedAt)
                    .ToListAsync(ct);

                return Results.Ok(new McpConnectionsResponse([.. grants.Select(View)]));
            })
            .WithName("ListMcpConnections")
            .WithSummary("List MCP connections")
            .WithDescription("The AI apps connected to your account through the MCP server.")
            .Produces<McpConnectionsResponse>();

        connections.MapDelete("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                if (Actor.Of(http) is not { } actor)
                    return Results.Unauthorized();

                var grant = await db.McpGrants.Include(g => g.Client)
                    .FirstOrDefaultAsync(g => g.Id == id && g.UserId == actor.Id, ct);

                if (grant is null)
                    return Results.NotFound();

                if (grant.RevokedAt is not null)
                    return Results.NoContent();

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                grant.RevokedAt = clock.UtcNow;
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.McpDisconnected,
                    grant.Id.ToString(),
                    actor,
                    new JsonObject { ["client"] = grant.Client?.Name, ["by"] = "person" },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .WithName("RevokeMcpConnection")
            .WithSummary("Disconnect an AI app")
            .WithDescription("Disconnect an AI app. Its tokens stop working on their next request.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<McpSettingsResponse> ViewAsync(HttpContext http, ModbotContext db, ChatToolRegistry registry, CancellationToken ct)
    {
        var row = await db.GetSettingsAsync(ct);
        var address = await McpAddress.ForAsync(http, db, ct);
        var switches = ChatToolRegistry.ParseSwitches(row.AiChatToolSwitches);
        var held = ModbotAuth.PermissionsOf(http.User);

        // What a key for MCP needs: the door, and what each tool the person is offered needs.
        var wanted = ModbotPermissions.UseAiChat;
        foreach (var tool in registry.All)
            wanted |= tool.Needs;

        var grantable = held.HasFlag(ModbotPermissions.Administrator) ? wanted : wanted & held;

        return new McpSettingsResponse(
            row.McpServerEnabled,
            address.ServerUrl,
            row.PublicAddress is not null,
            [.. registry.All.Select(t => new AiChatToolView(t.Name, t.Label, NeedsOf(t.Needs), t.OnlyReads, ChatToolRegistry.IsOn(t, switches)))],
            PermissionCatalog.NamesOf(grantable));
    }

    private static McpConnectionView View(McpGrant g) => new(
        g.Id,
        g.Client?.Name ?? "AI app",
        g.Client?.ClientUri,
        g.CreatedAt,
        g.LastUsedAt,
        g.RefreshExpiresAt);

    private static IReadOnlyList<string> NeedsOf(ModbotPermissions needs) =>
        [.. PermissionCatalog.All.Where(p => p.Value != 0 && needs.HasFlag((ModbotPermissions)p.Value)).Select(p => p.Label)];
}

public sealed record McpConnectionsResponse(IReadOnlyList<McpConnectionView> Connections);
