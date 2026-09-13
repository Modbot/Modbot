using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.Settings;

/// <param name="PacingFloorSeconds">
/// Spec 4.2's cap for this producer's endpoint class. Configuration may raise an interval past it
/// but never lower one below it — spec 4.2.1's rule that configuration may only make Modbot
/// gentler.
/// </param>
public sealed record AuditLogCadenceSettings(
    double MinIntervalSeconds,
    double MaxIntervalSeconds,
    double PacingFloorSeconds,
    double QuietBackoff,
    double JitterFraction,
    int PageSize,
    int MaxPagesPerRun,
    double OverlapSeconds,
    bool Backfill,
    int MaxBackfillPages);

public sealed record GroupInfoCadenceSettings(
    double IntervalSeconds,
    double RetryIntervalSeconds,
    double RateLimitedIntervalSeconds,
    double PacingFloorSeconds,
    double JitterFraction);

/// <param name="Editable">
/// False. See <see cref="EditableExplanation"/> — the values are process configuration, not
/// settings, and nothing persists a changed one.
/// </param>
/// <param name="Running">
/// Whether the producers are registered in this host at all. When false the intervals below are
/// what <em>would</em> be used, and nothing is polling.
/// </param>
public sealed record SyncSettingsResponse(
    AuditLogCadenceSettings AuditLog,
    GroupInfoCadenceSettings GroupInfo,
    bool Editable,
    string EditableExplanation,
    bool Running);

/// <summary>
/// The sync cadence, as it is actually configured in this process.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only, and that is a gap rather than a design.</strong> Spec 4.2.1 says every rate
/// is operator-configurable through a slider in settings, with the cap enforced server-side on
/// write. It is not, because the values live in <c>AuditLogSyncOptions</c> and
/// <c>GroupInfoSyncOptions</c>, which are registered as singletons from code at startup and read
/// nothing from the database — there is no column to write a lowered rate into and no mechanism to
/// re-read one without a restart.
/// </para>
/// <para>
/// Adding the columns is a migration, and migrations are not this slice's to make. So the screen
/// shows what is running and says plainly that it cannot be changed here, which is the honest
/// version of a control that does not work yet. An empty slider that silently discards its value
/// would be worse than no slider: an operator who dialled a rate down and believed it had taken
/// effect would be wrong about the one thing this page exists to tell them.
/// </para>
/// <para>
/// The live cadence <em>decision</em> — the interval in force right now and the producer's reason
/// for it — is on the health screen rather than here, because it changes every poll and is a
/// diagnostic, not a setting.
/// </para>
/// </remarks>
public static class SyncSettingsEndpoints
{
    /// <summary>The sentence the screen shows in place of a control that would not work.</summary>
    public const string NotEditable =
        "Sync intervals are process configuration in this build, not stored settings: they are "
        + "fixed at startup and there is nowhere to persist a change. Spec 4.2.1 calls for "
        + "operator-adjustable rates — that needs settings columns and a migration, and it is not "
        + "built. Until it is, these are shown rather than edited.";

    public static IEndpointRouteBuilder MapSyncSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/settings/sync", (
                HttpContext http,
                // Optional so a host without the producers still answers. Explicit [FromServices]
                // because an unattributed concrete type is bound as the request body, and on a GET
                // that throws while the route is mapped and takes the rest of the host with it.
                [FromServices] AuditLogSyncOptions? auditLog,
                [FromServices] GroupInfoSyncOptions? groupInfo) =>
            {
                var held = ModbotAuth.PermissionsOf(http.User);

                if (!held.HasFlag(ModbotPermissions.Administrator)
                    && !held.HasFlag(ModbotPermissions.ManageSettings))
                {
                    return Results.Forbid();
                }

                // Clamped, so what is shown is what the producer would actually use rather than
                // what was asked for -- spec 4.2.1's floor is applied on read as well as on write.
                var audit = (auditLog ?? new AuditLogSyncOptions()).Clamped();
                var info = (groupInfo ?? new GroupInfoSyncOptions()).Clamped();

                return Results.Ok(new SyncSettingsResponse(
                    new AuditLogCadenceSettings(
                        audit.MinInterval.TotalSeconds,
                        audit.MaxInterval.TotalSeconds,
                        AuditLogSyncOptions.PacingFloor.TotalSeconds,
                        audit.QuietBackoff,
                        audit.JitterFraction,
                        audit.PageSize,
                        audit.MaxPagesPerRun,
                        audit.Overlap.TotalSeconds,
                        audit.Backfill,
                        audit.MaxBackfillPages),
                    new GroupInfoCadenceSettings(
                        info.Interval.TotalSeconds,
                        info.RetryInterval.TotalSeconds,
                        info.RateLimitedInterval.TotalSeconds,
                        GroupInfoSyncOptions.PacingFloor.TotalSeconds,
                        info.JitterFraction),
                    Editable: false,
                    NotEditable,
                    Running: auditLog is not null || groupInfo is not null));
            })
            .RequireAuthorization()
            .WithTags("Settings")
            .WithName("GetSyncSettings")
            .WithSummary("How often the producers poll, and why that cannot be changed here")
            .WithDescription(
                "Read-only. Spec 4.2.1 calls for operator-adjustable rates; the values are "
                + "process configuration in this build and there is no column to persist a change "
                + "into. The response says so in `editableExplanation` rather than offering a "
                + "control that would discard what the operator typed.")
            .Produces<SyncSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }
}
