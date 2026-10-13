using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Settings;

/// <summary>The gallery pictures' settings, as stored.</summary>
/// <param name="ChannelId">The channel members post pictures in. Null means none are collected.</param>
public sealed record DiscordGallerySettingsResponse(string? ChannelId);

/// <param name="ChannelId">Null or empty for none.</param>
public sealed record DiscordGallerySettingsUpdate(string? ChannelId);

/// <summary>
/// Settings → Discord → Gallery pictures: the channel where members post pictures for the group's
/// VRChat gallery. Each PNG or JPEG posted there waits for staff on the Gallery page.
/// </summary>
public static class DiscordGallerySettingsEndpoints
{
    public static IEndpointRouteBuilder MapDiscordGallerySettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/discord-gallery").WithTags("Settings");

        group.MapGet("", async ([FromServices] ModbotContext db, CancellationToken ct)
                => Results.Ok(View(await db.GetSettingsAsync(ct))))
            .WithName("GetDiscordGallerySettings")
            .WithSummary("Get gallery picture settings")
            .WithDescription("The Discord channel where members post pictures for the group's VRChat gallery.")
            .Produces<DiscordGallerySettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] DiscordGallerySettingsUpdate body,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);
                var channel = string.IsNullOrWhiteSpace(body.ChannelId) ? null : body.ChannelId.Trim();

                var change = new SettingsChange("discordGallery")
                    .Field("channelId", settings.DiscordGalleryChannelId, channel);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.DiscordGalleryChannelId = channel;

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings));
            })
            .WithName("SetDiscordGallerySettings")
            .WithSummary("Update gallery picture settings")
            .WithDescription("Save the channel members post gallery pictures in. Every PNG or JPEG posted there from now on waits for staff on the Gallery page; pictures posted earlier are not collected.")
            .Produces<DiscordGallerySettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    private static DiscordGallerySettingsResponse View(Core.Data.Entities.Settings settings)
        => new(settings.DiscordGalleryChannelId);
}
