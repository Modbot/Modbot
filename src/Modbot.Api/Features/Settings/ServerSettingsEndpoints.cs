using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Settings;

/// <param name="ShowOwnerEmail">Whether <c>GET /api/server</c> gives out the owner's address.</param>
public sealed record ServerSettingsView(bool ShowOwnerEmail);

public sealed record SetServerSettingsRequest(bool ShowOwnerEmail);

/// <summary>
/// The one switch over what <c>GET /api/server</c> tells the world (server info and account email
/// design §2.3).
/// </summary>
/// <remarks>
/// Its own endpoint rather than a field on the public-address settings, because it is recorded as
/// its own settings change: "the owner's address stopped being published on the 3rd" is a sentence
/// an audit log should be able to produce.
/// </remarks>
public static class ServerSettingsEndpoints
{
    public static IEndpointRouteBuilder MapServerSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/server")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("/", async ([FromServices] ModbotContext db, CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);
                return Results.Ok(new ServerSettingsView(settings.ServerShowOwnerEmail));
            })
            .WithName("GetServerSettings")
            .WithSummary("Get server settings")
            .WithDescription("What this server tells anyone who asks about it.")
            .Produces<ServerSettingsView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/", async (
                [FromBody] SetServerSettingsRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);
                var before = settings.ServerShowOwnerEmail;

                if (before == body.ShowOwnerEmail)
                    return Results.Ok(new ServerSettingsView(before));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.ServerShowOwnerEmail = body.ShowOwnerEmail;
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.SettingsChanged,
                    "settings",
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["setting"] = "server.showOwnerEmail",
                        ["before"] = before,
                        ["after"] = body.ShowOwnerEmail,
                    },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(new ServerSettingsView(body.ShowOwnerEmail));
            })
            .WithName("SetServerSettings")
            .WithSummary("Update server settings")
            .WithDescription("Turn the owner's email address on or off on the server page.")
            .Produces<ServerSettingsView>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }
}
