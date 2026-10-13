using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Settings;

/// <summary>The permission watch's settings, as stored.</summary>
/// <param name="ChannelId">The channel staff are told in. Null means the fact log alone.</param>
/// <param name="Removes">Also take the permission (or the role) away.</param>
public sealed record DiscordPermissionWatchSettingsResponse(string? ChannelId, bool Removes);

/// <param name="ChannelId">Null or empty for none.</param>
/// <param name="Removes">Off unless somebody turns it on.</param>
public sealed record DiscordPermissionWatchSettingsUpdate(string? ChannelId, bool Removes);

/// <summary>
/// Settings → Discord → Permission watch: where staff are told when a role or a member gains a
/// dangerous permission, and whether the bot also takes it away.
/// </summary>
public static class DiscordPermissionWatchSettingsEndpoints
{
    public static IEndpointRouteBuilder MapDiscordPermissionWatchSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/discord-permission-watch").WithTags("Settings");

        group.MapGet("", async ([FromServices] ModbotContext db, CancellationToken ct)
                => Results.Ok(View(await db.GetSettingsAsync(ct))))
            .WithName("GetDiscordPermissionWatchSettings")
            .WithSummary("Get permission watch settings")
            .WithDescription("The channel staff are told in when a Discord role or member gains a dangerous permission, and whether the bot also removes it.")
            .Produces<DiscordPermissionWatchSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] DiscordPermissionWatchSettingsUpdate body,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);
                var channel = string.IsNullOrWhiteSpace(body.ChannelId) ? null : body.ChannelId.Trim();

                var change = new SettingsChange("discordPermissionWatch")
                    .Field("channelId", settings.DiscordPermissionWatchChannelId, channel)
                    .Field("removes", settings.DiscordPermissionWatchRemoves, body.Removes);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.DiscordPermissionWatchChannelId = channel;
                settings.DiscordPermissionWatchRemoves = body.Removes;

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings));
            })
            .WithName("SetDiscordPermissionWatchSettings")
            .WithSummary("Update permission watch settings")
            .WithDescription("Save the permission watch's settings. `removes` takes the permission from the role, or the role from the member, and never touches the server's owner.")
            .Produces<DiscordPermissionWatchSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    private static DiscordPermissionWatchSettingsResponse View(Core.Data.Entities.Settings settings)
        => new(settings.DiscordPermissionWatchChannelId, settings.DiscordPermissionWatchRemoves);
}
