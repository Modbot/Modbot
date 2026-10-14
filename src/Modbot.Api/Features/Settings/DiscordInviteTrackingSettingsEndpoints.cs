using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Settings;

/// <summary>Settings → Discord → Invite tracking, as stored.</summary>
/// <param name="On">Whether Modbot records which invite each new member used.</param>
public sealed record DiscordInviteTrackingSettingsResponse(bool On);

/// <summary>A change to Settings → Discord → Invite tracking.</summary>
/// <param name="On">Turn the tracking on or off.</param>
public sealed record DiscordInviteTrackingSettingsUpdate(bool On);

/// <summary>
/// Settings → Discord → Invite tracking: whether Modbot records which invite each new member used.
/// Needs Manage settings. Every change is recorded as a settings change. While it is off the bot's
/// invite link does not ask for Manage Server or Create Invite.
/// </summary>
public static class DiscordInviteTrackingSettingsEndpoints
{
    public static IEndpointRouteBuilder MapDiscordInviteTrackingSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/discord-invite-tracking")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("", async ([FromServices] ModbotContext db, CancellationToken ct)
                => Results.Ok(View(await db.GetSettingsAsync(ct))))
            .WithName("GetDiscordInviteTrackingSettings")
            .WithSummary("Get invite tracking settings")
            .WithDescription("Whether Modbot records which invite each new Discord member used.")
            .Produces<DiscordInviteTrackingSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] DiscordInviteTrackingSettingsUpdate body,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);

                var change = new SettingsChange("discordInviteTracking")
                    .Field("on", settings.DiscordInviteTrackingOn, body.On);

                if (change.IsEmpty)
                    return Results.Ok(View(settings));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.DiscordInviteTrackingOn = body.On;

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings));
            })
            .WithName("SetDiscordInviteTrackingSettings")
            .WithSummary("Update invite tracking settings")
            .WithDescription(
                "Turn invite tracking on or off. While it is on the bot's invite link asks for Manage Server and "
                + "Create Invite, so a bot added earlier has to be added again from the new link.")
            .Accepts<DiscordInviteTrackingSettingsUpdate>("application/json")
            .Produces<DiscordInviteTrackingSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static DiscordInviteTrackingSettingsResponse View(Core.Data.Entities.Settings settings)
        => new(settings.DiscordInviteTrackingOn);
}
