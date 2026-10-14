using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Settings;

/// <summary>Settings → Discord → Server snapshots, as stored.</summary>
/// <param name="Schedule">One of <c>off</c>, <c>weekly</c>, <c>every-two-weeks</c> or <c>monthly</c>.</param>
/// <param name="RestoreOn">Staff can put a role's or a channel's permissions back from a snapshot.</param>
public sealed record DiscordSnapshotSettingsResponse(string Schedule, bool RestoreOn);

/// <summary>A change to Settings → Discord → Server snapshots.</summary>
public sealed record DiscordSnapshotSettingsUpdate(string Schedule, bool RestoreOn);

/// <summary>
/// Settings → Discord → Server snapshots: how often Modbot saves a snapshot by itself, and whether a
/// role's or channel's permissions may be put back from one. Needs Manage settings. Every change is
/// recorded as a settings change. This setting adds Manage Roles to the bot's invite link only while
/// restoring is on.
/// </summary>
public static class DiscordSnapshotSettingsEndpoints
{
    public static IEndpointRouteBuilder MapDiscordSnapshotSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/discord-snapshots")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("", async ([FromServices] ModbotContext db, CancellationToken ct)
                => Results.Ok(View(await db.GetSettingsAsync(ct))))
            .WithName("GetDiscordSnapshotSettings")
            .WithSummary("Get server snapshot settings")
            .WithDescription("How often Modbot saves a snapshot of the Discord server, and whether restoring from one is allowed.")
            .Produces<DiscordSnapshotSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] DiscordSnapshotSettingsUpdate body,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (!DiscordSnapshotSchedules.IsKnown(body.Schedule))
                    return Results.BadRequest(new { error = "Pick off, weekly, every two weeks or monthly." });

                var settings = await db.GetSettingsAsync(ct);

                var change = new SettingsChange("discordSnapshots")
                    .Field("schedule", settings.DiscordSnapshotSchedule, body.Schedule)
                    .Field("restoreOn", settings.DiscordSnapshotRestoreOn, body.RestoreOn);

                if (change.IsEmpty)
                    return Results.Ok(View(settings));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.DiscordSnapshotSchedule = body.Schedule;
                settings.DiscordSnapshotRestoreOn = body.RestoreOn;

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings));
            })
            .WithName("SetDiscordSnapshotSettings")
            .WithSummary("Update server snapshot settings")
            .WithDescription(
                "Set the schedule (`off`, `weekly`, `every-two-weeks` or `monthly`) and whether restoring is allowed. "
                + "While restoring is on the bot's invite link asks for Manage Roles, so a bot added earlier has to be "
                + "added again from the new link. Turning the schedule on takes the first snapshot within ten minutes.")
            .Accepts<DiscordSnapshotSettingsUpdate>("application/json")
            .Produces<DiscordSnapshotSettingsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static DiscordSnapshotSettingsResponse View(Core.Data.Entities.Settings settings)
        => new(settings.DiscordSnapshotSchedule, settings.DiscordSnapshotRestoreOn);
}
