using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Settings;

/// <param name="PublicAddress">What is saved, or null.</param>
/// <param name="Suggestion">What the platform says it is, or null. Shown to confirm, never adopted silently.</param>
public sealed record PublicAddressView(string? PublicAddress, string? Suggestion);

public sealed record SetPublicAddressRequest(string? PublicAddress);

/// <summary>
/// The public address: the one thing an emailed or messaged link is built from (accounts and
/// access design §4.2).
/// </summary>
/// <remarks>
/// It is a setting a person saves and not a value read from a request, because a request's host
/// -- forwarded headers included -- belongs to whoever sent the request. A forgot-password request
/// can be sent by anyone for anyone; if the link inside the victim's genuine reset email were built
/// from that request, the attacker would choose where it pointed.
/// </remarks>
public static class PublicAddressSettingsEndpoints
{
    public static IEndpointRouteBuilder MapPublicAddressSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/public-address")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                HttpContext http,
                CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);
                var deployment = http.RequestServices.GetService(typeof(DeploymentInfo)) as DeploymentInfo;

                return Results.Ok(new PublicAddressView(settings.PublicAddress, deployment?.PublicAddressSuggestion));
            })
            .WithName("GetPublicAddress")
            .WithSummary("The address people use to reach this Modbot, and the platform's suggestion")
            .Produces<PublicAddressView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/", async (
                [FromBody] SetPublicAddressRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var (address, error) = PublicAddress.Normalize(body.PublicAddress);
                if (error is not null)
                    return Results.BadRequest(new { error });

                var settings = await db.GetSettingsAsync(ct);
                var before = settings.PublicAddress;

                if (before == address)
                    return Results.Ok(new PublicAddressView(address, null));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.PublicAddress = address;
                await db.SaveChangesAsync(ct);

                // Not a secret, so before and after are recorded (spec 5.9.3): a changed public
                // address right before a run of reset links is exactly what an audit log is for.
                await facts.RecordAsync(
                    FactType.SettingsChanged,
                    "settings",
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["setting"] = "publicAddress",
                        ["before"] = before,
                        ["after"] = address,
                    },
                    ct);

                await transaction.CommitAsync(ct);

                var deployment = http.RequestServices.GetService(typeof(DeploymentInfo)) as DeploymentInfo;
                return Results.Ok(new PublicAddressView(address, deployment?.PublicAddressSuggestion));
            })
            .WithName("SetPublicAddress")
            .WithSummary("Save the public address, or clear it")
            .WithDescription(
                "Just the start of the address -- https://modbot.example.com -- with no path. "
                + "Clearing it stops reset links being sent until it is set again.")
            .Produces<PublicAddressView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }
}
