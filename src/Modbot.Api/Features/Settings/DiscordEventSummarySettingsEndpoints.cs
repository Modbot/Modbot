using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Settings;

/// <summary>Settings → Discord → Monthly event summary, as stored.</summary>
/// <param name="On">Whether Modbot posts, once a month, how each event did the month before.</param>
/// <param name="ChannelId">The channel the summary goes in. Null for none.</param>
/// <param name="Problem">What stopped the last summary. Null when all is well.</param>
public sealed record DiscordEventSummarySettingsResponse(bool On, string? ChannelId, string? Problem);

/// <summary>Changes to Settings → Discord → Monthly event summary. Every part left out keeps what is stored.</summary>
/// <param name="On">Turn the summary on or off.</param>
/// <param name="ChannelId">The channel. Empty for none.</param>
public sealed record DiscordEventSummarySettingsUpdate(bool? On = null, string? ChannelId = null);

/// <summary>
/// Settings → Discord → Monthly event summary: whether Modbot posts, once a month, how each event
/// did the month before, and where. Needs Manage settings. Every change is recorded as a settings
/// change.
/// </summary>
public static class DiscordEventSummarySettingsEndpoints
{
    public static IEndpointRouteBuilder MapDiscordEventSummarySettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/discord-event-summary")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("", async ([FromServices] ModbotContext db, CancellationToken ct)
                => Results.Ok(View(await db.GetSettingsAsync(ct))))
            .WithName("GetDiscordEventSummarySettings")
            .WithSummary("Get monthly event summary settings")
            .WithDescription(
                "Whether Modbot posts a summary in Discord once a month of how each event did the month before, "
                + "and the channel. `problem` says what stopped the last summary.")
            .Produces<DiscordEventSummarySettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] DiscordEventSummarySettingsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);

                var onAfter = body.On ?? settings.EventSummaryOn;
                var channelAfter = body.ChannelId is null ? settings.EventSummaryChannelId : Clean(body.ChannelId);

                if (onAfter && channelAfter is null)
                    return Results.BadRequest(new { error = "Pick a channel to post in.", problems = new[] { "Pick a channel to post in." } });

                var change = new SettingsChange("discordEventSummary")
                    .Field("eventSummaryOn", settings.EventSummaryOn, onAfter)
                    .Field("channelId", settings.EventSummaryChannelId, channelAfter);

                if (change.IsEmpty)
                    return Results.Ok(View(settings));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                // Only the months that end from now on are summarised: turning it on never posts for
                // a month already over.
                if (onAfter && !settings.EventSummaryOn)
                    settings.EventSummaryLastMonth = EventMonthSummary.MonthOf(clock.UtcNow).AddMonths(-1);

                settings.EventSummaryOn = onAfter;
                settings.EventSummaryChannelId = channelAfter;
                settings.EventSummaryProblem = null;

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings));
            })
            .WithName("SetDiscordEventSummarySettings")
            .WithSummary("Update monthly event summary settings")
            .WithDescription(
                "Turn the monthly event summary on or off, or change its channel (empty for none). Turning it on "
                + "never posts for a month already over. A refusal (400) says what is wrong.")
            .Accepts<DiscordEventSummarySettingsUpdate>("application/json")
            .Produces<DiscordEventSummarySettingsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DiscordEventSummarySettingsResponse View(Core.Data.Entities.Settings settings)
        => new(settings.EventSummaryOn, settings.EventSummaryChannelId, settings.EventSummaryProblem);
}
