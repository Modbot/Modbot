using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Cloud;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Settings;

/// <param name="Shared">Whether the group's public rooms are sent to Modbot Cloud.</param>
/// <param name="CloudDisabled">
/// True when <c>MODBOT_CLOUD_DISABLED</c> is set, which stops the sending whatever the setting says.
/// </param>
/// <param name="LastSentAt">When the last report reached Cloud, or null.</param>
public sealed record PublicRoomsView(
    [property: JsonPropertyName("shared")] bool Shared,
    [property: JsonPropertyName("cloudDisabled")] bool CloudDisabled,
    [property: JsonPropertyName("lastSentAt")] DateTimeOffset? LastSentAt);

public sealed record SetPublicRoomsRequest(
    [property: JsonPropertyName("shared")] bool Shared);

/// <summary>
/// Whether this Modbot tells Modbot Cloud which of the group's rooms are open to everyone, so
/// they are listed on modbot.co.
/// </summary>
/// <remarks>
/// On by default. Turning it off stops the next report and asks Cloud to drop what it already has,
/// so the group leaves the page rather than waiting to be aged out.
/// </remarks>
public static class PublicRoomsSettingsEndpoints
{
    public static IEndpointRouteBuilder MapPublicRoomsSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/public-rooms")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                [FromServices] ModbotCloudAddress cloud,
                CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);

                return Results.Ok(new PublicRoomsView(
                    settings.SharePublicRooms,
                    cloud.Disabled,
                    settings.PublicRoomsReportedAt));
            })
            .WithName("GetPublicRooms")
            .WithSummary("Whether the group's public rooms are listed on modbot.co")
            .Produces<PublicRoomsView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/", async (
                [FromBody] SetPublicRoomsRequest body,
                [FromServices] ModbotContext db,
                [FromServices] ModbotCloudAddress cloud,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);
                var before = settings.SharePublicRooms;

                if (before == body.Shared)
                    return Results.Ok(new PublicRoomsView(before, cloud.Disabled, settings.PublicRoomsReportedAt));

                settings.SharePublicRooms = body.Shared;
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.SettingsChanged,
                    "settings",
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["setting"] = "sharePublicRooms",
                        ["before"] = before,
                        ["after"] = body.Shared,
                    },
                    ct);

                // Turned off: ask Cloud to drop the rooms now. Turned on: send them now. Either
                // way the page matches the switch before the operator has closed the tab.
                var sender = http.RequestServices.GetService<PublicRoomsSender>();

                if (sender is not null && !body.Shared)
                    await sender.StopAsync(ct);
                else if (body.Shared)
                    http.RequestServices.GetService<PublicRoomsNudge>()?.Poke();

                var after = await db.GetSettingsAsync(ct);
                return Results.Ok(new PublicRoomsView(after.SharePublicRooms, cloud.Disabled, after.PublicRoomsReportedAt));
            })
            .WithName("SetPublicRooms")
            .WithSummary("Turn the modbot.co listing on or off")
            .Produces<PublicRoomsView>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }
}
