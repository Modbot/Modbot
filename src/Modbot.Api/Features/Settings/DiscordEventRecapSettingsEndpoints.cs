using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Settings;

/// <summary>Settings → Discord → Event recaps, as stored.</summary>
/// <param name="On">Whether Modbot posts a recap once an event's instance has closed.</param>
/// <param name="ChannelId">The channel the recap goes in. Null for none.</param>
/// <param name="Problem">What stopped the last recap or its photo thread. Null when all is well.</param>
public sealed record DiscordEventRecapSettingsResponse(bool On, string? ChannelId, string? Problem);

/// <summary>Changes to Settings → Discord → Event recaps. Every part left out keeps what is stored.</summary>
/// <param name="On">Turn the recaps on or off.</param>
/// <param name="ChannelId">The channel. Empty for none.</param>
public sealed record DiscordEventRecapSettingsUpdate(bool? On = null, string? ChannelId = null);

/// <summary>
/// Settings → Discord → Event recaps: whether Modbot posts a recap in Discord once the instance of an
/// event's date has closed, and where. Needs Manage settings. Every change is recorded as a settings
/// change.
/// </summary>
public static class DiscordEventRecapSettingsEndpoints
{
    public static IEndpointRouteBuilder MapDiscordEventRecapSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/discord-event-recaps")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("", async ([FromServices] ModbotContext db, CancellationToken ct)
                => Results.Ok(View(await db.GetSettingsAsync(ct))))
            .WithName("GetDiscordEventRecapSettings")
            .WithSummary("Get event recap settings")
            .WithDescription(
                "Whether Modbot posts a recap in Discord once the instance of an event's date has closed, and "
                + "the channel. `problem` says what stopped the last recap, or its photo thread.")
            .Produces<DiscordEventRecapSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] DiscordEventRecapSettingsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);

                var onAfter = body.On ?? settings.EventRecapsOn;
                var channelAfter = body.ChannelId is null ? settings.EventRecapChannelId : Clean(body.ChannelId);

                if (onAfter && channelAfter is null)
                    return Results.BadRequest(new { error = "Pick a channel to post in.", problems = new[] { "Pick a channel to post in." } });

                var change = new SettingsChange("discordEventRecaps")
                    .Field("eventRecapsOn", settings.EventRecapsOn, onAfter)
                    .Field("channelId", settings.EventRecapChannelId, channelAfter);

                if (change.IsEmpty)
                    return Results.Ok(View(settings));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                // Only events whose instance closes from now on are recapped: turning it on never
                // posts for events that ended before.
                if (onAfter && !settings.EventRecapsOn)
                    settings.EventRecapsFrom = clock.UtcNow;

                settings.EventRecapsOn = onAfter;
                settings.EventRecapChannelId = channelAfter;
                settings.EventRecapProblem = null;

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings));
            })
            .WithName("SetDiscordEventRecapSettings")
            .WithSummary("Update event recap settings")
            .WithDescription(
                "Turn the event recaps on or off, or change their channel (empty for none). Turning them on never "
                + "posts for an event whose instance closed before. A refusal (400) says what is wrong.")
            .Accepts<DiscordEventRecapSettingsUpdate>("application/json")
            .Produces<DiscordEventRecapSettingsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DiscordEventRecapSettingsResponse View(Core.Data.Entities.Settings settings)
        => new(settings.EventRecapsOn, settings.EventRecapChannelId, settings.EventRecapProblem);
}
