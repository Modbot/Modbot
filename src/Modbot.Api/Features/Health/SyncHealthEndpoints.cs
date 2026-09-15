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
                    diagnostics is null ? null : new SweepHealth(
                        diagnostics.MemberSweep.Phase,
                        settings?.MemberSweepCompletedAt,
                        settings?.MemberSweepStartedAt,
                        settings?.MemberSweepOffset ?? 0,
                        settings?.MemberSweepCount ?? 0,
                        diagnostics.MemberSweep.PagesWalked,
                        diagnostics.MemberSweep.RowsChanged,
                        diagnostics.MemberSweep.FactsWritten,
                        diagnostics.MemberSweep.FactsDeduplicated,
                        diagnostics.MemberSweep.NextPassAt,
                        ColdStopped(buckets, VRChatEndpointClass.GroupsMembers),
                        settings?.MemberSweepPolledAt,
                        Run(diagnostics.LastMemberSweepRun)),
                    diagnostics is null ? null : new SweepHealth(
                        diagnostics.BanSweep.Phase,
                        settings?.BanSweepCompletedAt,
                        settings?.BanSweepStartedAt,
                        settings?.BanSweepOffset ?? 0,
                        settings?.BanSweepCount ?? 0,
                        diagnostics.BanSweep.PagesWalked,
                        diagnostics.BanSweep.RowsChanged,
                        diagnostics.BanSweep.FactsWritten,
                        diagnostics.BanSweep.FactsDeduplicated,
                        diagnostics.BanSweep.NextPassAt,
                        ColdStopped(buckets, VRChatEndpointClass.GroupsBans),
                        settings?.BanSweepPolledAt,
                        Run(diagnostics.LastBanSweepRun)),
                    clock.UtcNow,
                    await DiscordChannelProblemsAsync(db, ct)));
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

    /// <summary>
    /// Each channel an enabled route sends to where the bot is missing a permission it needs, the
    /// channel is gone, or the last post was refused.
    /// </summary>
    private static async Task<IReadOnlyList<DiscordChannelProblem>> DiscordChannelProblemsAsync(
        ModbotContext db, CancellationToken ct)
    {
        var channelIds = await db.DiscordEventRoutes.AsNoTracking()
            .Where(r => r.Enabled && r.ChannelId != "")
            .Select(r => r.ChannelId)
            .Distinct()
            .ToListAsync(ct);

        if (channelIds.Count == 0)
            return [];

        var known = await db.DiscordChannels.AsNoTracking()
            .Where(c => channelIds.Contains(c.ChannelId))
            .ToDictionaryAsync(c => c.ChannelId, ct);

        var places = await db.DiscordEventChannels.AsNoTracking()
            .Where(p => channelIds.Contains(p.ChannelId))
            .ToDictionaryAsync(p => p.ChannelId, ct);

        var problems = new List<DiscordChannelProblem>();

        foreach (var id in channelIds.Order(StringComparer.Ordinal))
        {
            known.TryGetValue(id, out var channel);
            places.TryGetValue(id, out var place);

            // A channel the bot has never listed is not reported as missing permissions: before
            // the bot first connects nothing is listed, and the refusal, if any, says the rest.
            var missing = new List<string>();
            if (channel is not null && channel.RemovedAt is null)
            {
                if (!channel.BotCanView) missing.Add("View Channel");
                if (!channel.BotCanSend) missing.Add("Send Messages");
                if (!channel.BotCanEmbedLinks) missing.Add("Embed Links");
            }

            var removed = channel?.RemovedAt is not null;

            if (missing.Count == 0 && !removed && place?.LastError is null)
                continue;

            problems.Add(new DiscordChannelProblem(
                id, channel?.Name, missing, removed, place?.LastError, place?.LastErrorAt));
        }

        return problems;
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

    /// <summary>Whether any bucket of this class -- the class bucket or the group's own -- is refusing to send.</summary>
    private static bool ColdStopped(IReadOnlyList<BucketHealth> buckets, string endpointClass) =>
        buckets.Any(b => b.IsColdStopped && string.Equals(b.EndpointClass, endpointClass, StringComparison.Ordinal));

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
