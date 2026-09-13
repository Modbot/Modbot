using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Storage;
using Modbot.Api.Auth;
using Modbot.Core;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Settings;

/// <param name="ModerationFactRetentionDays">0 means keep forever.</param>
/// <param name="PresenceFactRetentionDays">0 means keep forever.</param>
public sealed record RetentionSettings(int ModerationFactRetentionDays, int PresenceFactRetentionDays);

/// <param name="Version">Calendar release, so a bug report can name it.</param>
/// <param name="Platform">Detected host, e.g. "Railway" or "self-hosted".</param>
/// <param name="PlatformEvidence">The variable that produced the match, or null.</param>
/// <param name="LogFilesWritten">Whether the six file streams are being written at all.</param>
/// <param name="PersistenceExplanation">Why, in one sentence.</param>
public sealed record DeploymentSummary(
    string Version,
    string Platform,
    string? PlatformEvidence,
    bool LogFilesWritten,
    string PersistenceExplanation);

/// <param name="Bytes">Measured, including indexes.</param>
/// <param name="Facts">Row count — the planner's estimate once one exists.</param>
/// <param name="BytesPerFact">Measured, not summed from column widths.</param>
/// <param name="FactsPerDay">Observed arrival rate.</param>
/// <param name="ObservedDays">How much history that rate came from.</param>
/// <param name="Confidence">How far to trust the estimate.</param>
/// <param name="Horizons">Projected totals, empty when there is too little history.</param>
/// <param name="CapacityExhausted">When the entered disk fills, if one was entered.</param>
public sealed record StorageSummary(
    long Bytes,
    long Facts,
    double BytesPerFact,
    double FactsPerDay,
    double ObservedDays,
    string Confidence,
    IReadOnlyList<StorageHorizon> Horizons,
    DateTimeOffset? CapacityExhausted);

public sealed record DataSettingsResponse(
    RetentionSettings Retention,
    StorageSummary Storage,
    DeploymentSummary Deployment);

/// <summary>
/// The Data settings screen: what Modbot is keeping, what it costs, and for how long.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.5. Modbot has no default retention window — an unconfigured deployment keeps every fact
/// forever — and that is only a defensible default if the operator can see what it costs. These
/// endpoints are what makes "keep everything" a decision rather than an accident.
/// </para>
/// <para>
/// <strong>The cost and capacity inputs are query parameters, not stored settings.</strong> They
/// are a what-if calculator: an operator tries "what if I pay $0.25/GB" and "what if this disk is
/// 500 GB" and reads off the answers. Nothing in Modbot behaves differently for having been told,
/// so persisting them would add a column and a migration to change a number on a screen. The
/// browser remembers the last values so they survive a reload, which is where that state belongs.
/// </para>
/// </remarks>
public static class DataSettingsEndpoints
{
    public static IEndpointRouteBuilder MapDataSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings").WithTags("Settings").RequireAuthorization();

        group.MapGet("/data", async (
                HttpContext http,
                ModbotContext db,
                // Explicit, because minimal APIs infer a concrete type as the request body --
                // and on a GET that is not merely wrong, it throws while the route is being
                // mapped and takes every other endpoint in the host down with it.
                [FromServices] StorageEstimator estimator,
                [FromServices] DeploymentInfo deployment,
                [FromQuery] decimal? costPerGbMonth,
                [FromQuery] long? capacityBytes,
                CancellationToken ct) =>
            {
                if (Forbidden(http)) return Results.Forbid();

                var settings = await db.Settings.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == 1, ct) ?? new Core.Data.Entities.Settings();

                var forecast = await estimator.ForecastAsync(
                    new StorageBudget(costPerGbMonth, capacityBytes), ct);

                var m = forecast.Measurement;

                return Results.Ok(new DataSettingsResponse(
                    new RetentionSettings(
                        settings.ModerationFactRetentionDays,
                        settings.PresenceFactRetentionDays),
                    new StorageSummary(
                        m.TotalBytes,
                        m.FactCount,
                        m.BytesPerFact,
                        m.FactsPerDay,
                        m.ObservedDays,
                        forecast.Confidence.ToString(),
                        forecast.Horizons,
                        forecast.CapacityExhausted),
                    new DeploymentSummary(
                        ModbotVersion.Release,
                        deployment.Platform.Name,
                        deployment.Platform.Evidence,
                        deployment.LogFilesWritten,
                        deployment.PersistenceExplanation)));
            })
            .WithName("GetDataSettings")
            .WithSummary("Retention, measured storage, and what this deployment is running on")
            .WithDescription(
                "costPerGbMonth and capacityBytes are optional what-if inputs. They are not "
                + "stored: nothing in Modbot behaves differently for having been told, so they "
                + "are answered against rather than persisted.")
            .Produces<DataSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/retention", async (
                HttpContext http,
                ModbotContext db,
                RetentionSettings body,
                CancellationToken ct) =>
            {
                if (Forbidden(http)) return Results.Forbid();

                if (body.ModerationFactRetentionDays < 0 || body.PresenceFactRetentionDays < 0)
                    return Results.BadRequest(new { error = "Retention cannot be negative. Use 0 to keep forever." });

                var settings = await db.GetSettingsAsync(ct);

                settings.ModerationFactRetentionDays = body.ModerationFactRetentionDays;
                settings.PresenceFactRetentionDays = body.PresenceFactRetentionDays;

                await db.SaveChangesAsync(ct);

                return Results.Ok(body);
            })
            .WithName("SetRetention")
            .WithSummary("Set retention windows, or turn them off")
            .WithDescription(
                "Zero means keep forever, which is the default for both classes. Turning a window "
                + "on schedules destruction of data that cannot be recovered or filled in later.")
            .Produces<RetentionSettings>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    /// <remarks>
    /// Same permission the onboarding wizard uses once setup is done: every screen here changes
    /// how the deployment behaves, and retention specifically schedules irreversible deletion.
    /// </remarks>
    private static bool Forbidden(HttpContext http)
    {
        var held = ModbotAuth.PermissionsOf(http.User);

        return !held.HasFlag(ModbotPermissions.Administrator)
            && !held.HasFlag(ModbotPermissions.ManageSettings);
    }
}
