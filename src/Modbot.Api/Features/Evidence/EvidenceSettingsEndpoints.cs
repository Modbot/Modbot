using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Evidence;

/// <summary>
/// Settings → Evidence: choose a backend, prove it works, and see what the store is doing
/// (design §16).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every one of these answers 200 with a verdict, not a status code carrying a mood.</strong>
/// A backend that fails the round trip has been successfully diagnosed, and the whole value of
/// §8.5 is the sentence naming <em>which</em> step failed — credentials, endpoint, URL style,
/// permissions, or a read that returned different bytes than were written. An HTTP status cannot
/// carry that, and a client that turned a 400 into "the server answered 400" would throw away the
/// only useful part. This is the same position the VRChat connection check already takes: a failed
/// check is still a successful diagnosis, and every sentence the operator reads comes from the
/// server so the two cannot drift.
/// </para>
/// <para>
/// <strong>Nothing here is saved before it is proved.</strong> The round trip runs against a
/// candidate store built from the values the operator typed, and the row changes only afterwards.
/// </para>
/// </remarks>
public static class EvidenceSettingsEndpoints
{
    internal static IEndpointRouteBuilder MapEvidenceSettings(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings/evidence")
            .WithTags("Settings")
            .RequireAuthorization();

        group.MapGet("/", async (
                HttpContext http,
                [FromServices] EvidenceSettingsService service,
                CancellationToken ct) =>
            {
                if (Forbidden(http)) return Results.Forbid();

                return Results.Ok(await service.DescribeAsync(ct));
            })
            .WithName("GetEvidenceSettings")
            .WithSummary("The configured evidence backend, its health, and what it is holding")
            .WithDescription(
                "Counts and bytes come from the blob record rather than from listing the "
                + "store, because LIST is slow everywhere and billed on some providers. The S3 "
                + "secret is never returned; secretStored says only whether one is on file.")
            .Produces<EvidenceSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/test", async (
                HttpContext http,
                EvidenceBackendRequest body,
                [FromServices] EvidenceSettingsService service,
                CancellationToken ct) =>
            {
                if (Forbidden(http)) return Results.Forbid();

                return Results.Ok(await service.TestAsync(body, ct));
            })
            .WithName("TestEvidenceStore")
            .WithSummary("Run the setup check without saving anything")
            .WithDescription(
                "Writes a test file, reads it back, compares the bytes, promotes it to its "
                + "content-addressed key, reads it again, deletes it, and writes the store "
                + "store marker. Nothing is persisted. Leave secretAccessKey empty to test with the "
                + "credential already on file.")
            .Produces<EvidenceSetupResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/backend", async (
                HttpContext http,
                EvidenceBackendRequest body,
                [FromServices] EvidenceSettingsService service,
                CancellationToken ct) =>
            {
                if (Forbidden(http)) return Results.Forbid();

                var actor = http.User.Identity?.Name ?? "unknown";

                return Results.Ok(await service.SaveBackendAsync(body, actor, ct));
            })
            .WithName("SetEvidenceBackend")
            .WithSummary("Save a backend, once it has passed the round trip")
            .WithDescription(
                "The round trip runs first and the row changes only if it passed, so a backend "
                + "that cannot store evidence cannot be selected. Choosing the filesystem backend "
                + "on a directory Modbot cannot prove survives a restart returns "
                + "requiresAcknowledgement with the warning to show; echo that text back in "
                + "acknowledgeWarning to proceed, and it is recorded verbatim against your name. "
                + "Switching backends while objects are stored is refused: changing the setting "
                + "does not move them.")
            .Produces<EvidenceSetupResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/limits", async (
                HttpContext http,
                EvidenceLimitsRequest body,
                [FromServices] EvidenceSettingsService service,
                CancellationToken ct) =>
            {
                if (Forbidden(http)) return Results.Forbid();

                var (saved, error) = await service.SaveLimitsAsync(body, ct);

                return saved is null
                    ? Results.BadRequest(new { error })
                    : Results.Ok(saved);
            })
            .WithName("SetEvidenceLimits")
            .WithSummary("Per-file, per-report and per-deployment caps, and direct delivery")
            .WithDescription(
                "No round trip: none of these repoints a store. Zero means no limit on the two "
                + "totals; the per-file cap is enforced three times and cannot be zero.")
            .Produces<EvidenceLimitsView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/probe", async (
                HttpContext http,
                [FromServices] EvidenceSettingsService service,
                CancellationToken ct) =>
            {
                if (Forbidden(http)) return Results.Forbid();

                return Results.Ok(await service.ProbeAsync(ct));
            })
            .WithName("ProbeEvidenceStore")
            .WithSummary("Re-read the store marker now")
            .WithDescription(
                "The same three-valued probe startup takes. A store that answers with somebody "
                + "else's store marker, or with none, locks; a store that does not answer at all "
                + "does not, because silence is not evidence of loss.")
            .Produces<EvidenceHealthView>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    /// <remarks>
    /// The same permission the rest of settings uses. Choosing a storage backend decides where a
    /// group's evidence physically lives and whether it survives the next redeploy, which is a
    /// deployment-shaped decision rather than a moderation one — holding
    /// <see cref="ModbotPermissions.UploadEvidence"/> does not imply it.
    /// </remarks>
    private static bool Forbidden(HttpContext http)
    {
        var held = ModbotAuth.PermissionsOf(http.User);

        return !held.HasFlag(ModbotPermissions.Administrator)
            && !held.HasFlag(ModbotPermissions.ManageSettings);
    }
}
