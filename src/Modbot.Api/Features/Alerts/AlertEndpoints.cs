using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.AI;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Alerts;

/// <summary>
/// Unusual-activity alerts: reading them, hiding one, and Settings → AI → Alerts
/// (AI insights design §8).
/// </summary>
/// <remarks>
/// <para>
/// Reading needs <c>ViewAnalytics</c>, because an alert is the analytics pages' own figures said
/// out loud. Hiding one needs the same: it hides a card and keeps the alert. Changing what is
/// watched needs <c>ManageSettings</c>, like the rest of the AI tab.
/// </para>
/// <para>
/// Every parameter is explicitly attributed: an unattributed concrete type on a GET is bound as a
/// body, which throws while routes are mapped and takes every endpoint with it.
/// </para>
/// </remarks>
public static class AlertEndpoints
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;
    public const int MaxChannelIdLength = 32;

    /// <summary>How long an alert stays on the card before it is history.</summary>
    public static readonly TimeSpan RecentFor = TimeSpan.FromHours(24);

    public static IEndpointRouteBuilder MapAlerts(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var alerts = app.MapGroup("/api/alerts").WithTags("Alerts");

        alerts.MapGet("", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromQuery] bool? all,
                [FromQuery] int? limit,
                CancellationToken ct) =>
            {
                var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
                var since = clock.UtcNow - RecentFor;
                var query = db.Alerts.AsNoTracking().AsQueryable();

                if (all != true)
                    query = query.Where(a => a.DismissedAt == null && a.At >= since);

                var rows = await query.OrderByDescending(a => a.At).Take(take).ToListAsync(ct);

                return Results.Ok(new AlertPage([.. rows.Select(AlertView.From)]));
            })
            .RequiresFlag(ModbotPermissions.ViewAnalytics)
            .WithName("ListAlerts")
            .WithSummary("List alerts")
            .WithDescription(
                "Times something ran far outside this deployment's own normal, newest first. "
                + "Recent alerts nobody has hidden by default; `all=true` lists every alert kept. "
                + "Each carries the figure, what normal looks like, the window, and where in Modbot "
                + "to look. Counts and places only -- never a person.")
            .Produces<AlertPage>()
            .Produces(StatusCodes.Status403Forbidden);

        alerts.MapPost("/{id:guid}/dismiss", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == id, ct);
                if (alert is null)
                    return Results.NotFound(new { error = "No such alert." });

                if (alert.DismissedAt is null)
                {
                    alert.DismissedAt = clock.UtcNow;

                    if (ModbotAuth.UserIdOf(http.User) is { } userId)
                    {
                        alert.DismissedByUserId = userId;
                        alert.DismissedByUsername = await db.Users.AsNoTracking()
                            .Where(u => u.Id == userId).Select(u => u.Username).FirstOrDefaultAsync(ct);
                    }

                    await db.SaveChangesAsync(ct);
                }

                return Results.Ok(AlertView.From(alert));
            })
            .RequiresFlag(ModbotPermissions.ViewAnalytics)
            .WithName("DismissAlert")
            .WithSummary("Dismiss alert")
            .WithDescription(
                "Hide one alert's card. "
                + "The alert itself is kept, and so is the fact it wrote.")
            .Produces<AlertView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        var settings = app.MapGroup("/api/settings/ai/alerts").WithTags("AI settings");

        settings.MapGet("", async (
                [FromServices] ModbotContext db,
                [FromServices] IAiClients ai,
                CancellationToken ct) => Results.Ok(await ViewAsync(db, ai, ct)))
            .RequiresFlag(ModbotPermissions.ManageSettings)
            .WithName("GetAiAlertSettings")
            .WithSummary("Get AI alert settings")
            .WithDescription("What is watched for unusual activity, and where alerts go.")
            .Produces<AlertSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        settings.MapPut("", async (
                [FromBody] AlertSettingsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] IAiClients ai,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var channel = body.DiscordChannelId?.Trim();
                if (!string.IsNullOrEmpty(channel)
                    && (channel.Length > MaxChannelIdLength || !channel.All(char.IsAsciiDigit)))
                    return Results.BadRequest(new { error = "Choose a Discord channel." });

                if (body.QuietHours is < 0 or > AlertWatchers.MaxQuietHours)
                    return Results.BadRequest(new { error = "Choose a quiet time." });

                foreach (var w in body.Watchers ?? [])
                {
                    if (!AlertWatchers.IsKnown(w.Watcher))
                        return Results.BadRequest(new { error = "Not something Modbot watches." });
                    if (!AlertSensitivities.IsKnown(w.Sensitivity))
                        return Results.BadRequest(new { error = "Choose a sensitivity." });
                }

                var stored = await SettingsRowAsync(db, ct);
                stored.DiscordChannelId = string.IsNullOrEmpty(channel) ? null : channel;
                stored.QuietHours = body.QuietHours;
                stored.WriteSentence = body.WriteSentence;

                var watches = await WatchesAsync(db, ct);

                foreach (var w in body.Watchers ?? [])
                    watches.Single(x => x.Watcher == w.Watcher).Sensitivity = w.Sensitivity;

                await db.SaveChangesAsync(ct);

                return Results.Ok(await ViewAsync(db, ai, ct));
            })
            .RequiresFlag(ModbotPermissions.ManageSettings)
            .WithName("SetAiAlertSettings")
            .WithSummary("Update AI alert settings")
            .WithDescription(
                "Save what is watched, how sensitive each watcher is, and where alerts go.")
            .Produces<AlertSettingsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<AlertSettings> SettingsRowAsync(ModbotContext db, CancellationToken ct)
    {
        var row = await db.AlertSettings.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (row is null)
        {
            row = new AlertSettings { Id = 1 };
            db.AlertSettings.Add(row);
        }

        return row;
    }

    /// <summary>One row per watcher, created on first read with every watcher off.</summary>
    private static async Task<List<AlertWatch>> WatchesAsync(ModbotContext db, CancellationToken ct)
    {
        var rows = await db.AlertWatches.ToListAsync(ct);

        foreach (var watcher in AlertWatchers.All.Where(w => rows.All(r => r.Watcher != w)))
        {
            var row = new AlertWatch { Watcher = watcher };
            db.AlertWatches.Add(row);
            rows.Add(row);
        }

        return rows;
    }

    private static async Task<AlertSettingsResponse> ViewAsync(ModbotContext db, IAiClients ai, CancellationToken ct)
    {
        var settings = await db.AlertSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var watches = await db.AlertWatches.AsNoTracking().ToListAsync(ct);
        var aiOn = await ai.GetChatAsync(ct) is not null;

        var watchers = new List<AlertWatchSettings>();

        foreach (var watcher in AlertWatchers.All)
        {
            var watch = watches.FirstOrDefault(w => w.Watcher == watcher);
            var last = await db.Alerts.AsNoTracking()
                .Where(a => a.Watcher == watcher)
                .OrderByDescending(a => a.At)
                .FirstOrDefaultAsync(ct);

            watchers.Add(new AlertWatchSettings(
                watcher,
                AlertWatchers.Label(watcher),
                watch?.Sensitivity ?? AlertSensitivities.Off,
                last is null ? null : AlertView.From(last)));
        }

        return new AlertSettingsResponse(
            settings?.DiscordChannelId,
            settings?.QuietHours ?? AlertWatchers.DefaultQuietHours,
            settings?.WriteSentence ?? true,
            aiOn,
            watchers);
    }
}
