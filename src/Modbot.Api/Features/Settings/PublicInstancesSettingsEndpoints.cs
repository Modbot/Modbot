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

/// <param name="Shared">Whether the group's public instances are sent to Modbot Cloud.</param>
/// <param name="CloudDisabled">
/// True when <c>MODBOT_CLOUD_DISABLED</c> is set, which stops the sending whatever the setting says.
/// </param>
/// <param name="LastSentAt">When the last report reached Cloud, or null.</param>
public sealed record PublicInstancesView(
    [property: JsonPropertyName("shared")] bool Shared,
    [property: JsonPropertyName("cloudDisabled")] bool CloudDisabled,
    [property: JsonPropertyName("lastSentAt")] DateTimeOffset? LastSentAt);

public sealed record SetPublicInstancesRequest(
    [property: JsonPropertyName("shared")] bool Shared);

/// <summary>
/// Whether this Modbot tells Modbot Cloud which of the group's instances are open to everyone, so
/// they are listed on modbot.co.
/// </summary>
/// <remarks>
/// On by default. Turning it off stops the next report and asks Cloud to drop what it already has,
/// so the group leaves the page rather than waiting to be aged out.
/// </remarks>
public static class PublicInstancesSettingsEndpoints
{
    public static IEndpointRouteBuilder MapPublicInstancesSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/public-instances")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                [FromServices] ModbotCloudAddress cloud,
                CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);

                return Results.Ok(new PublicInstancesView(
                    settings.SharePublicInstances,
                    cloud.Disabled,
                    settings.PublicInstancesReportedAt));
            })
            .WithName("GetPublicInstances")
            .WithSummary("Get instance listing")
            .WithDescription("Whether the group's public instances are listed on modbot.co.")
            .Produces<PublicInstancesView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/", async (
                [FromBody] SetPublicInstancesRequest body,
                [FromServices] ModbotContext db,
                [FromServices] ModbotCloudAddress cloud,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);
                var before = settings.SharePublicInstances;

                if (before == body.Shared)
                    return Results.Ok(new PublicInstancesView(before, cloud.Disabled, settings.PublicInstancesReportedAt));

                settings.SharePublicInstances = body.Shared;
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.SettingsChanged,
                    "settings",
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["setting"] = "sharePublicInstances",
                        ["before"] = before,
                        ["after"] = body.Shared,
                    },
                    ct);

                // Turned off: ask Cloud to drop the instances now. Turned on: send them now. Either
                // way the page matches the switch before the operator has closed the tab.
                var sender = http.RequestServices.GetService<PublicInstancesSender>();

                if (sender is not null && !body.Shared)
                    await sender.StopAsync(ct);
                else if (body.Shared)
                    http.RequestServices.GetService<PublicInstancesNudge>()?.Poke();

                var after = await db.GetSettingsAsync(ct);
                return Results.Ok(new PublicInstancesView(after.SharePublicInstances, cloud.Disabled, after.PublicInstancesReportedAt));
            })
            .WithName("SetPublicInstances")
            .WithSummary("Set instance listing")
            .WithDescription("Turn the modbot.co listing on or off.")
            .Produces<PublicInstancesView>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }
}
