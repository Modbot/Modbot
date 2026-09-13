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
/// foundation with no producer — the machinery works and nobody can see it.
/// </para>
/// <para>
/// <strong>Two endpoints, because the two audiences are different.</strong> The gate summary is
/// available to any signed-in account: a moderator whose action did nothing needs to be able to
/// tell "Modbot is cold-stopped" from "Modbot is broken", and withholding that produces a support
/// question instead of an informed wait. The detail — per-bucket budgets, poll rate reasoning, the
/// audit-log event types Modbot could not map — is Modbot's operational record and takes
/// <c>ViewOperationalLog</c>, which is the same line spec 5.9.4 draws for the logs themselves.
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
                [FromServices] UserRefreshQueue? queue,
                // Optional for the same reason: the bot is wired by the host, not by the API.
                [FromServices] Modbot.Core.Discord.IDiscordBotStatus? discordBot,
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
                    Run(diagnostics?.LastUserProfileRun),
                    settings?.AuditLogPolledAt,
                    settings?.GroupInfoPolledAt,
                    settings?.UserProfilePolledAt,
                    settings?.AuditLogCatchUpComplete ?? false,
                    settings?.AuditLogSyncedThrough,
                    !string.IsNullOrWhiteSpace(settings?.ManagedGroupId),
                    diagnostics?.UnmappedAuditEvents
                        .Select(e => new UnmappedEvent(
                            e.EventType, e.Count, e.FirstSeen, e.LastSeen,
                            e.SampleEntryId, e.SampleDescription))
                        .ToList() ?? [],
                    Horizon(diagnostics?.HistoryHorizonReached),
                    Profiles(diagnostics, queue),
                    discordBot?.Snapshot(),
                    clock.UtcNow));
            })
            .RequiresFlag(ModbotPermissions.ViewOperationalLog)
            .WithName("GetSyncHealth")
            .WithSummary("Producer poll rate, last runs, bucket budgets, and unmapped audit-log event types")
            .WithDescription(
                "`unmappedAuditEvents` lists audit-log event types VRChat has sent that Modbot has "
                + "no name for yet. Each is still recorded, as `modbot.unrecognised` with VRChat's "
                + "own wording kept, so nothing is lost — but each is also a mapping worth adding.\n\n"
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

    private static HistoryHorizonReport? Horizon(HistoryHorizon? horizon)
        => horizon is null
            ? null
            : new HistoryHorizonReport(horizon.EntriesRead, horizon.ReachedAt);

    /// <summary>
    /// The profile sync's own numbers. Null when it is not registered here -- the table counts
    /// come from its housekeeping pass, and a host with no producer never counts.
    /// </summary>
    private static UserProfileHealth? Profiles(SyncDiagnostics? diagnostics, UserRefreshQueue? queue)
    {
        if (diagnostics is null || queue is null)
            return null;

        var counts = diagnostics.UserProfileCounts;

        return new UserProfileHealth(
            counts?.KnownUsers ?? 0,
            counts?.NeverRefreshed ?? 0,
            counts?.NotFound ?? 0,
            counts?.OldestRefreshedAt,
            queue.Count,
            queue.CountByReason().ToDictionary(p => p.Key.ToString(), p => p.Value, StringComparer.Ordinal),
            queue.InProgress?.UserId,
            diagnostics.UserProfileRefreshesInLastHour,
            diagnostics.UserProfileLastRateLimitedAt,
            counts?.MeasuredAt);
    }
}
