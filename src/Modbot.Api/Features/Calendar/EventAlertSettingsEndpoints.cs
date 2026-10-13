using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Calendar;

/// <summary>
/// The event alert settings (event alerts design §3): the master switch, the two ways of being
/// told, the channel for the ping and how long before the start. One request saves all of them.
/// </summary>
/// <remarks>
/// Under Manage calendar. Nothing here asks Discord anything: the alerts pass reads the settings
/// each time it runs. The channel is an opaque Discord id and is never checked for its shape.
/// </remarks>
public static class EventAlertSettingsEndpoints
{
    public static IEndpointRouteBuilder MapEventAlertSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/calendar/alerts").WithTags("Calendar");

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);
                return Results.Ok(View(settings));
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("GetEventAlertSettings")
            .WithSummary("Get event alert settings")
            .WithDescription("Whether members are told before an event, the ways they can be told, the ping channel and how many minutes before the start.")
            .Produces<EventAlertSettingsView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/", async (
                [FromBody] SetEventAlertSettingsRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (body.MinutesBefore < EventAlertSettings.LeastMinutesBefore
                    || body.MinutesBefore > EventAlertSettings.MostMinutesBefore)
                {
                    return Results.BadRequest(new
                    {
                        error = $"Minutes before the start must be {EventAlertSettings.LeastMinutesBefore} to {EventAlertSettings.MostMinutesBefore}.",
                    });
                }

                var channelId = string.IsNullOrWhiteSpace(body.ChannelId) ? null : body.ChannelId.Trim();
                if (body.Ping && channelId is null)
                    return Results.BadRequest(new { error = "Pick a channel to turn pings on." });

                var settings = await db.GetSettingsAsync(ct);

                var changes = new List<(string Setting, JsonNode? Before, JsonNode? After)>();
                void Note(string setting, JsonNode? before, JsonNode? after)
                {
                    if (!JsonNode.DeepEquals(before, after))
                        changes.Add((setting, before, after));
                }

                Note("eventAlertsOn", settings.EventAlertsOn, body.On);
                Note("eventAlertsDirectMessage", settings.EventAlertsDirectMessage, body.DirectMessage);
                Note("eventAlertsPing", settings.EventAlertsPing, body.Ping);
                Note("eventAlertsChannelId", settings.EventAlertsChannelId, channelId);
                Note("eventAlertsMinutesBefore", settings.EventAlertsMinutesBefore, body.MinutesBefore);

                if (changes.Count == 0)
                    return Results.Ok(View(settings));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.EventAlertsOn = body.On;
                settings.EventAlertsDirectMessage = body.DirectMessage;
                settings.EventAlertsPing = body.Ping;
                settings.EventAlertsChannelId = channelId;
                settings.EventAlertsMinutesBefore = body.MinutesBefore;
                await db.SaveChangesAsync(ct);

                foreach (var (setting, before, after) in changes)
                {
                    await facts.RecordAsync(
                        FactType.SettingsChanged,
                        "settings",
                        Actor.Of(http),
                        new JsonObject
                        {
                            ["setting"] = setting,
                            ["before"] = before,
                            ["after"] = after,
                        },
                        ct);
                }

                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings));
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("SetEventAlertSettings")
            .WithSummary("Set event alert settings")
            .WithDescription("Save the event alert settings in one request. Minutes before the start outside 5 to 1,440 is a 400, and so is pings on with no channel.")
            .Produces<EventAlertSettingsView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static EventAlertSettingsView View(Modbot.Core.Data.Entities.Settings settings)
        => new(
            settings.EventAlertsOn,
            settings.EventAlertsDirectMessage,
            settings.EventAlertsPing,
            settings.EventAlertsChannelId,
            settings.EventAlertsMinutesBefore);
}
