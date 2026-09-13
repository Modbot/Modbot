using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.VRChat;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.Health;

/// <summary>
/// What the producers and the gate would tell an operator about themselves.
/// </summary>
/// <remarks>
/// <para>
/// Spec 4.2.3 and 4.3.3 both require this and nothing surfaced it: <c>SyncDiagnostics</c> has been
/// populated and readable with no reader, which is the same shape of problem as an analytics
/// substrate with no producer — the machinery works and nobody can see it.
/// </para>
/// <para>
/// <strong>Two endpoints, because the two audiences are different.</strong> The gate summary is
/// available to any signed-in account: a moderator whose action did nothing needs to be able to
/// tell "Modbot is cold-stopped" from "Modbot is broken", and withholding that produces a support
/// question instead of an informed wait. The detail — per-bucket budgets, poll rate reasoning, the
/// vocabulary check — is Modbot's operational record and takes <c>ViewOperationalLog</c>, which is
/// the same line spec 5.9.4 draws for the logs themselves.
/// </para>
/// </remarks>
public static class SyncHealthEndpoints
{
    public static IEndpointRouteBuilder MapSyncHealth(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/health").WithTags("Health").RequireAuthorization();

        group.MapGet("/gate", async (
                [FromServices] IVRChatGate gate,
                CancellationToken ct) =>
            {
                var (health, _) = await GateHealthReader.ReadAsync(gate, ct);
                return Results.Ok(health);
            })
            .WithName("GetGateHealth")
            .WithSummary("Whether Modbot is reaching VRChat, and whether that is a problem")
            .WithDescription(
                "`status` is the load-bearing field. RateLimited and WafBlocked both stop "
                + "traffic and look identical from outside, and they mean opposite things: a cold "
                + "stop is spec 4.3.1 working as designed and recovers on its own, while a WAF "
                + "block needs an egress proxy and will not clear by itself.\n\n"
                + "Available to any signed-in account. A moderator whose action did nothing needs "
                + "to be able to tell waiting from broken.")
            .Produces<GateHealth>();

        group.MapGet("/sync", async (
                [FromServices] IVRChatGate gate,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                // Optional: a host that registered the API without the producers still answers,
                // and says so, rather than failing to resolve a service at request time.
                [FromServices] SyncDiagnostics? diagnostics,
                CancellationToken ct) =>
            {
                var (health, buckets) = await GateHealthReader.ReadAsync(gate, ct);

                var settings = await db.Settings.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == 1, ct);

                return Results.Ok(new SyncHealth(
                    health,
                    buckets,
                    diagnostics is not null,
                    PollRate(diagnostics?.AuditLogPollRate),
                    Run(diagnostics?.LastAuditLogRun),
                    Run(diagnostics?.LastGroupInfoRun),
                    settings?.AuditLogPolledAt,
                    settings?.GroupInfoPolledAt,
                    settings?.AuditLogCatchUpComplete ?? false,
                    settings?.AuditLogSyncedThrough,
                    !string.IsNullOrWhiteSpace(settings?.ManagedGroupId),
                    diagnostics?.UnmappedAuditEvents
                        .Select(e => new UnmappedEvent(
                            e.EventType, e.Count, e.FirstSeen, e.LastSeen,
                            e.SampleEntryId, e.SampleDescription))
                        .ToList() ?? [],
                    Vocabulary(diagnostics?.Vocabulary),
                    clock.UtcNow));
            })
            .RequiresFlag(ModbotPermissions.ViewOperationalLog)
            .WithName("GetSyncHealth")
            .WithSummary("Producer poll rate, last runs, bucket budgets, and the vocabulary check")
            .WithDescription(
                "`vocabulary.missingPrimary` is the loudest thing here. It lists audit-log event "
                + "types Modbot treats as real that VRChat does not declare, which means facts of "
                + "those types are being lost right now and nothing else shows it — the fact log "
                + "looks healthy because Modbot is waiting for a string VRChat never sends.\n\n"
                + "The poll rate carries the producer's own reason for the interval it chose. "
                + "Without it a deliberately slow poll and a stuck one are indistinguishable "
                + "(spec 4.2.3).\n\n"
                + "`now` is the server's clock. Ages should be computed against it rather than "
                + "against the browser's, which is not the authority for anything here.")
            .Produces<SyncHealth>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static PollRateReport? PollRate(PollRateDecision? decision)
        => decision is null
            ? null
            : new PollRateReport(
                decision.Interval.TotalSeconds,
                decision.Reason,
                decision.ConsecutiveQuietPolls,
                decision.DecidedAt);

    private static SyncRunSummary? Run(SyncRunReport? report)
        => report is null
            ? null
            : new SyncRunSummary(
                report.Outcome.ToString(),
                report.At,
                report.Duration.TotalSeconds,
                report.Summary);

    private static VocabularyReport? Vocabulary(AuditLogVocabularyReport? report)
        => report is null
            ? null
            : new VocabularyReport(
                report.CheckedAt,
                report.Declared,
                report.Unmapped,
                report.MissingPrimary,
                report.UnusedAliases,
                report.HasProblem);
}
