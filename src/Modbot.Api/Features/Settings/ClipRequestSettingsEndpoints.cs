using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Settings;

/// <summary>Which actions ask the moderator's companion for a clip, as stored.</summary>
public sealed record ClipRequestSettingsResponse(bool Bans, bool Kicks, bool Warnings);

public sealed record ClipRequestSettingsUpdate(bool Bans, bool Kicks, bool Warnings);

/// <summary>
/// Settings → Automatic roles → Ask for a clip on: bans, kicks and warnings, each on or off.
/// </summary>
public static class ClipRequestSettingsEndpoints
{
    public static IEndpointRouteBuilder MapClipRequestSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/clip-requests").WithTags("Settings");

        group.MapGet("", async ([FromServices] ModbotContext db, CancellationToken ct)
                => Results.Ok(View(await db.GetSettingsAsync(ct))))
            .WithName("GetClipRequestSettings")
            .WithSummary("Get which actions ask for a clip")
            .WithDescription("Whether a ban, a kick or a warning asks the moderator's companion for a clip.")
            .Produces<ClipRequestSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] ClipRequestSettingsUpdate body,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);

                var change = new SettingsChange("clipRequests")
                    .Field("bans", settings.ClipOnBans, body.Bans)
                    .Field("kicks", settings.ClipOnKicks, body.Kicks)
                    .Field("warnings", settings.ClipOnWarnings, body.Warnings);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.ClipOnBans = body.Bans;
                settings.ClipOnKicks = body.Kicks;
                settings.ClipOnWarnings = body.Warnings;

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings));
            })
            .WithName("SetClipRequestSettings")
            .WithSummary("Update which actions ask for a clip")
            .WithDescription("Save which of bans, kicks and warnings ask the moderator's companion for a clip. Actions taken before the change are not asked about.")
            .Produces<ClipRequestSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    private static ClipRequestSettingsResponse View(Core.Data.Entities.Settings settings)
        => new(settings.ClipOnBans, settings.ClipOnKicks, settings.ClipOnWarnings);
}
