using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.VRChat;
using Modbot.VRChat.Pacing;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.Settings;

/// <param name="PacingFloorSeconds">
/// Spec 4.2's cap for this producer's endpoint class. Configuration may raise an interval past it
/// but never lower one below it — spec 4.2.1's rule that configuration may only make Modbot
/// gentler.
/// </param>
public sealed record AuditLogPollRateSettings(
    double MinIntervalSeconds,
    double MaxIntervalSeconds,
    double PacingFloorSeconds,
    double QuietBackoff,
    double JitterFraction,
    int PageSize,
    int MaxPagesPerRun,
    double OverlapSeconds,
    bool CatchUp,
    int MaxCatchUpPages);

public sealed record GroupInfoPollRateSettings(
    double IntervalSeconds,
    double RetryIntervalSeconds,
    double RateLimitedIntervalSeconds,
    double PacingFloorSeconds,
    double JitterFraction);

/// <summary>The profile sync's pace and its windows (user profile sync design §5).</summary>
/// <param name="IntervalSeconds">Time between refreshes while somebody is waiting. Floored at the users lane's cap.</param>
/// <param name="StaleAfterSeconds">How old a profile is before it is refreshed just for being old.</param>
/// <param name="RecentWindowSeconds">How long a sighting keeps somebody near the front of the queue.</param>
/// <param name="FreshEnoughWhenOpenedSeconds">A profile fetched more recently than this is not queued again when opened in Modbot.</param>
/// <param name="FreshEnoughWhenSeenInInstanceSeconds">The same gap for a presence sighting. Shorter.</param>
public sealed record UserProfilePollRateSettings(
    double IntervalSeconds,
    double PacingFloorSeconds,
    double StaleAfterSeconds,
    double RecentWindowSeconds,
    double FreshEnoughWhenOpenedSeconds,
    double FreshEnoughWhenSeenInInstanceSeconds,
    double RateLimitedIntervalSeconds);

/// <summary>How a member or ban sweep is paced (member and ban sync design §3).</summary>
/// <param name="PageDelaySeconds">Time between one page and the next while a sweep runs. Floored at the class cap.</param>
/// <param name="RestSeconds">Time between the end of one full sweep and the start of the next.</param>
/// <param name="PacingFloorSeconds">Spec 4.2's cap for the class: one request per 2 seconds.</param>
public sealed record SweepPollRateSettings(
    double PageDelaySeconds,
    double RestSeconds,
    double RetryIntervalSeconds,
    double RateLimitedIntervalSeconds,
    double PacingFloorSeconds,
    double JitterFraction,
    int PageSize);

/// <summary>One endpoint class's budget, as configured and as it will actually be issued.</summary>
/// <param name="HardMaxPerSecond">
/// Spec 4.2's cap. Read-only: no write raises the rate past it, and the limiter takes the minimum
/// with it again when it paces (spec 4.2.1).
/// </param>
/// <param name="CeilingPerSecond">
/// The operator's estimate of VRChat's real limit for this class — the number the slider moves.
/// </param>
/// <param name="EffectiveRatePerSecond">
/// What Modbot will issue at: <c>min(hardMax, ceiling × budgetFraction)</c>. This is the number
/// spec 4.2's table gives, and the one to show.
/// </param>
/// <param name="Configured">False when this class is still on spec 4.2's default.</param>
/// <param name="Scheduled">
/// Whether background sync drives this class. The scheduled ones are what
/// <see cref="SyncRateSettings.ScheduledTotalPerSecond"/> sums.
/// </param>
/// <param name="Backstop">
/// The backstop bucket this class also draws from: <c>global</c> for background sync,
/// <c>interactive</c> for what a moderator presses, null for the exempt user reads (spec 4.3.5).
/// </param>
public sealed record EndpointClassRate(
    string EndpointClass,
    string Lane,
    double HardMaxPerSecond,
    double DefaultCeilingPerSecond,
    double CeilingPerSecond,
    double EffectiveRatePerSecond,
    bool Configured,
    bool Scheduled,
    bool CountsAgainstGlobal,
    bool ResourceScoped,
    int BurstTokens,
    string? Backstop = null);

/// <param name="BudgetFraction">The share of each estimate Modbot spends (spec 4.3.1).</param>
/// <param name="ScheduledTotalPerSecond">
/// The sum of the effective rates of the classes background sync schedules — spec 4.2's 1.450
/// req/s, recomputed from whatever the operator has configured.
/// </param>
/// <param name="GlobalCeilingPerSecond">The backstop's effective rate (spec 4.2: 2 req/s).</param>
/// <param name="InteractiveRoomLeftPerSecond">
/// What is left under the ceiling for moderation actions, onboarding and a moderator's live
/// queries. Spec 4.2 is explicit that this is not spare capacity for faster sync.
/// </param>
public sealed record SyncRateSettings(
    double BudgetFraction,
    double DefaultBudgetFraction,
    double MinimumBudgetFraction,
    double ScheduledTotalPerSecond,
    double GlobalCeilingPerSecond,
    double GlobalHardMaxPerSecond,
    double InteractiveRoomLeftPerSecond,
    IReadOnlyList<EndpointClassRate> Classes);

/// <summary>A value that was stored as something other than what was asked for.</summary>
public sealed record SyncSettingsAdjustment(
    string Field, double Requested, double Stored, string Reason);

/// <param name="Editable">
/// True. Every rate and interval below can be lowered here, and the cap is applied on write
/// (spec 4.2.1).
/// </param>
/// <param name="Running">
/// Whether the producers are registered in this host at all. When false the intervals below are
/// what <em>would</em> be used, and nothing is polling.
/// </param>
/// <param name="RestartRequired">
/// False, always. Recorded as a field rather than left implicit because "does this need a
/// restart" is the question an operator actually has, and a screen that does not answer it makes
/// them guess.
/// </param>
/// <param name="Adjustments">
/// What the last write changed on the way in. Empty on a read.
/// </param>
public sealed record SyncSettingsResponse(
    AuditLogPollRateSettings AuditLog,
    GroupInfoPollRateSettings GroupInfo,
    UserProfilePollRateSettings UserProfile,
    SweepPollRateSettings MemberSweep,
    SweepPollRateSettings BanSweep,
    SyncRateSettings Rates,
    bool Editable,
    bool Running,
    bool RestartRequired,
    IReadOnlyList<SyncSettingsAdjustment> Adjustments);

/// <summary>Poll rate fields to change. Every one optional; null means "leave alone".</summary>
public sealed record AuditLogPollRateUpdate(
    double? MinIntervalSeconds = null,
    double? MaxIntervalSeconds = null,
    double? QuietBackoff = null,
    double? JitterFraction = null,
    int? PageSize = null,
    int? MaxPagesPerRun = null,
    double? OverlapSeconds = null,
    bool? CatchUp = null,
    int? MaxCatchUpPages = null);

/// <summary>Group-info poll rate fields to change. Every one optional.</summary>
public sealed record GroupInfoPollRateUpdate(
    double? IntervalSeconds = null,
    double? RetryIntervalSeconds = null,
    double? RateLimitedIntervalSeconds = null,
    double? JitterFraction = null);

/// <summary>Profile sync fields to change. Every one optional.</summary>
public sealed record UserProfilePollRateUpdate(
    double? IntervalSeconds = null,
    double? StaleAfterSeconds = null,
    double? RecentWindowSeconds = null,
    double? FreshEnoughWhenOpenedSeconds = null,
    double? FreshEnoughWhenSeenInInstanceSeconds = null,
    double? RateLimitedIntervalSeconds = null);

/// <summary>Sweep fields to change. Every one optional.</summary>
public sealed record SweepPollRateUpdate(
    double? PageDelaySeconds = null,
    double? RestSeconds = null,
    double? RetryIntervalSeconds = null,
    double? RateLimitedIntervalSeconds = null,
    double? JitterFraction = null,
    int? PageSize = null);

/// <param name="ClassCeilingsPerSecond">
/// Estimates of VRChat's limit, keyed by endpoint class. Only the classes named are touched.
/// </param>
/// <param name="Reset">
/// Start from spec 4.2's defaults and apply the rest of this request on top. The way back to the
/// shipped pacing without knowing which fields were ever changed.
/// </param>
public sealed record SyncSettingsUpdate(
    IReadOnlyDictionary<string, double>? ClassCeilingsPerSecond = null,
    double? BudgetFraction = null,
    AuditLogPollRateUpdate? AuditLog = null,
    GroupInfoPollRateUpdate? GroupInfo = null,
    UserProfilePollRateUpdate? UserProfile = null,
    SweepPollRateUpdate? MemberSweep = null,
    SweepPollRateUpdate? BanSweep = null,
    bool Reset = false);

/// <summary>
/// The sync poll rate and the per-endpoint budgets: what they are, and how to lower them.
/// </summary>
/// <remarks>
/// <para>
/// Spec 4.2.1: every rate in spec 4.2's table, plus the global ceiling, is operator-configurable,
/// <strong>downward only</strong>, with the cap enforced server-side on write rather than in the
/// UI so that it cannot be raised by editing a request. That is what <c>PUT</c> does here — it
/// clamps, stores the clamped value, and hands back every clamp it applied, because a screen that
/// silently stored something other than what was typed would be the same defect as the slider
/// that discarded its value.
/// </para>
/// <para>
/// <strong>Lowering is the only direction that needs to work.</strong> The asymmetry is spec
/// 4.3's: dialling a rate down costs staler data, and dialling one up past what VRChat tolerates
/// costs an opaque multi-minute outage that a retry makes worse. So the cap is applied in three
/// independent places — here on write, again when the stored document is read, and once more in
/// <c>TokenBucket.EffectiveRatePerSecond</c> — and none of them is redundant, because they fail
/// in different ways: a hand-edited row skips the first, a restored backup skips the second.
/// </para>
/// <para>
/// <strong>No restart.</strong> The producers re-read the pacing at the top of every tick and the
/// limiter re-applies it when the version changes, so a lowered rate takes effect on the next
/// poll. A change made through this endpoint is published to the running process directly; a row
/// edited in the database instead is picked up within <c>SyncPacingProvider.CacheFor</c>.
/// </para>
/// <para>
/// The live poll rate <em>decision</em> — the interval in force right now and the producer's reason
/// for it — is on the health screen rather than here, because it changes every poll and is a
/// diagnostic, not a setting.
/// </para>
/// </remarks>
public static class SyncSettingsEndpoints
{
    public static IEndpointRouteBuilder MapSyncSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/settings/sync", async (
                HttpContext http,
                ModbotContext db,
                // Optional so a host without the producers still answers. Explicit [FromServices]
                // because an unattributed concrete type is bound as the request body, and on a GET
                // that throws while the route is mapped and takes the rest of the host with it.
                [FromServices] AuditLogSyncOptions? auditLog,
                [FromServices] GroupInfoSyncOptions? groupInfo,
                [FromServices] SyncPacingBaseline? baseline,
                CancellationToken ct) =>
            {
                if (Forbidden(http))
                    return Results.Forbid();

                var stored = await StoredAsync(db, ct);

                return Results.Ok(Describe(
                    SyncPacing.Resolve(stored, classes: null, baseline),
                    running: auditLog is not null || groupInfo is not null,
                    adjustments: []));
            })
            .RequireAuthorization()
            .WithTags("Settings")
            .WithName("GetSyncSettings")
            // How Modbot paces its VRChat requests: an administrator's internal, left out of the
            // public API reference.
            .ExcludeFromDescription()
            .WithSummary("How often the producers poll, and what each endpoint class is budgeted")
            .WithDescription(
                "Every rate is shown three ways: spec 4.2's hard cap, the operator's configured "
                + "estimate of VRChat's limit, and the effective rate that results. Only the "
                + "middle one is writable, and only downward.")
            .Produces<SyncSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPut("/api/settings/sync", async (
                HttpContext http,
                ModbotContext db,
                [FromBody] SyncSettingsUpdate body,
                [FromServices] AuditLogSyncOptions? auditLog,
                [FromServices] GroupInfoSyncOptions? groupInfo,
                [FromServices] SyncPacingBaseline? baseline,
                [FromServices] ISyncPacingSource? pacing,
                CancellationToken ct) =>
            {
                if (Forbidden(http))
                    return Results.Forbid();

                if (Invalid(body) is { } complaint)
                    return Results.BadRequest(new { error = complaint });

                var stored = body.Reset ? SyncPacingDocument.Empty : await StoredAsync(db, ct);
                var merged = stored.MergedWith(Flatten(body));
                var clamped = SyncPacingJson.Clamp(merged, out var adjustments);
                var json = SyncPacingJson.Write(clamped);

                var settings = await db.GetSettingsAsync(ct);
                settings.SyncPacing = json;
                await db.SaveChangesAsync(ct);

                // Published rather than left for the cache to notice: the operator is watching
                // this request, and "it will apply within half a minute" is not an answer when
                // the process that has to apply it is the one serving the response.
                pacing?.Publish(json);

                return Results.Ok(Describe(
                    SyncPacing.Resolve(clamped, classes: null, baseline),
                    running: auditLog is not null || groupInfo is not null,
                    adjustments: [.. adjustments.Select(a =>
                        new SyncSettingsAdjustment(a.Field, a.Requested, a.Stored, a.Reason))]));
            })
            .RequireAuthorization()
            .WithTags("Settings")
            .WithName("SetSyncSettings")
            // How Modbot paces its VRChat requests: an administrator's internal, left out of the
            // public API reference.
            .ExcludeFromDescription()
            .WithSummary("Lower a sync rate or a poll interval")
            .WithDescription(
                "Partial: every field is optional and an omitted one is left alone. A rate above "
                + "spec 4.2's cap is stored at the cap and named in `adjustments` — the write "
                + "succeeds, gently, rather than failing. A rate of zero or below is rejected "
                + "outright: it is not a gentler setting, it is a bucket that never issues again. "
                + "Takes effect from the next poll; no restart.")
            .Produces<SyncSettingsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<SyncPacingDocument> StoredAsync(ModbotContext db, CancellationToken ct)
    {
        var json = await db.Settings
            .AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.SyncPacing)
            .FirstOrDefaultAsync(ct);

        return SyncPacingJson.Read(json);
    }

    /// <summary>
    /// Rejects the values that are not "gentler" but "broken".
    /// </summary>
    /// <remarks>
    /// Clamping is the right answer for a rate that is merely too fast — spec 4.2.1 wants the
    /// operator's intent honoured as far as it safely can be. It is the wrong answer for zero: a
    /// bucket with an effective rate of zero waits forever for a token it will never be given, so
    /// the sync stops with no cold stop and no incident to explain it. Guessing a positive number
    /// on the operator's behalf would be worse still, so this says no.
    /// </remarks>
    private static string? Invalid(SyncSettingsUpdate body)
    {
        if (body.ClassCeilingsPerSecond is { } ceilings)
        {
            foreach (var (name, value) in ceilings)
            {
                if (!VRChatRateLimits.Defaults.ContainsKey(name))
                {
                    return $"'{name}' is not an endpoint class Modbot budgets.";
                }

                if (Broken(value))
                    return $"The rate for '{name}' must be a positive number. A bucket at zero would never issue again.";
            }
        }

        if (body.BudgetFraction is { } fraction && Broken(fraction))
            return "The budget fraction must be a positive share of the estimated limit.";

        if (body.AuditLog is { } audit)
        {
            if (Broken(audit.MinIntervalSeconds) || Broken(audit.MaxIntervalSeconds))
                return "Audit-log intervals must be positive numbers of seconds.";

            if (Broken(audit.QuietBackoff))
                return "The quiet backoff must be a positive multiplier.";

            if (Negative(audit.JitterFraction) || Negative(audit.OverlapSeconds))
                return "Jitter and the re-read overlap cannot be negative.";

            if (audit.PageSize is < 1 || audit.MaxPagesPerRun is < 1 || audit.MaxCatchUpPages is < 1)
                return "Page counts must be at least 1.";
        }

        if (body.GroupInfo is { } info)
        {
            if (Broken(info.IntervalSeconds)
                || Broken(info.RetryIntervalSeconds)
                || Broken(info.RateLimitedIntervalSeconds))
            {
                return "Group-info intervals must be positive numbers of seconds.";
            }

            if (Negative(info.JitterFraction))
                return "Jitter cannot be negative.";
        }

        if (body.UserProfile is { } profile)
        {
            if (Broken(profile.IntervalSeconds) || Broken(profile.RateLimitedIntervalSeconds))
                return "Profile sync intervals must be positive numbers of seconds.";

            if (Broken(profile.StaleAfterSeconds))
                return "The stale-after window must be a positive number of seconds.";

            if (Negative(profile.RecentWindowSeconds)
                || Negative(profile.FreshEnoughWhenOpenedSeconds)
                || Negative(profile.FreshEnoughWhenSeenInInstanceSeconds))
            {
                return "The recent and fresh-enough windows cannot be negative.";
            }
        }

        if (InvalidSweep(body.MemberSweep, "Member sweep") is { } members)
            return members;

        if (InvalidSweep(body.BanSweep, "Ban sweep") is { } bans)
            return bans;

        return null;
    }

    private static string? InvalidSweep(SweepPollRateUpdate? sweep, string name)
    {
        if (sweep is null)
            return null;

        if (Broken(sweep.PageDelaySeconds) || Broken(sweep.RestSeconds)
            || Broken(sweep.RetryIntervalSeconds) || Broken(sweep.RateLimitedIntervalSeconds))
        {
            return $"{name} intervals must be positive numbers of seconds.";
        }

        if (Negative(sweep.JitterFraction))
            return "Jitter cannot be negative.";

        if (sweep.PageSize is < 1)
            return "A page must hold at least 1 entry.";

        return null;
    }

    private static bool Broken(double? value) =>
        value is { } v && (!double.IsFinite(v) || v <= 0);

    private static bool Negative(double? value) =>
        value is { } v && (!double.IsFinite(v) || v < 0);

    /// <summary>Turns the nested wire shape into the flat document that is stored and merged.</summary>
    private static SyncPacingDocument Flatten(SyncSettingsUpdate body) => new()
    {
        ClassCeilingsPerSecond = body.ClassCeilingsPerSecond,
        BudgetFraction = body.BudgetFraction,

        AuditLogMinIntervalSeconds = body.AuditLog?.MinIntervalSeconds,
        AuditLogMaxIntervalSeconds = body.AuditLog?.MaxIntervalSeconds,
        AuditLogQuietBackoff = body.AuditLog?.QuietBackoff,
        AuditLogJitterFraction = body.AuditLog?.JitterFraction,
        AuditLogPageSize = body.AuditLog?.PageSize,
        AuditLogMaxPagesPerRun = body.AuditLog?.MaxPagesPerRun,
        AuditLogOverlapSeconds = body.AuditLog?.OverlapSeconds,
        AuditLogCatchUp = body.AuditLog?.CatchUp,
        AuditLogMaxCatchUpPages = body.AuditLog?.MaxCatchUpPages,

        GroupInfoIntervalSeconds = body.GroupInfo?.IntervalSeconds,
        GroupInfoRetryIntervalSeconds = body.GroupInfo?.RetryIntervalSeconds,
        GroupInfoRateLimitedIntervalSeconds = body.GroupInfo?.RateLimitedIntervalSeconds,
        GroupInfoJitterFraction = body.GroupInfo?.JitterFraction,

        UserProfileIntervalSeconds = body.UserProfile?.IntervalSeconds,
        UserProfileStaleAfterSeconds = body.UserProfile?.StaleAfterSeconds,
        UserProfileRecentWindowSeconds = body.UserProfile?.RecentWindowSeconds,
        UserProfileFreshEnoughWhenOpenedSeconds = body.UserProfile?.FreshEnoughWhenOpenedSeconds,
        UserProfileFreshEnoughWhenSeenInInstanceSeconds = body.UserProfile?.FreshEnoughWhenSeenInInstanceSeconds,
        UserProfileRateLimitedIntervalSeconds = body.UserProfile?.RateLimitedIntervalSeconds,

        MemberSweepPageDelaySeconds = body.MemberSweep?.PageDelaySeconds,
        MemberSweepRestSeconds = body.MemberSweep?.RestSeconds,
        MemberSweepRetryIntervalSeconds = body.MemberSweep?.RetryIntervalSeconds,
        MemberSweepRateLimitedIntervalSeconds = body.MemberSweep?.RateLimitedIntervalSeconds,
        MemberSweepJitterFraction = body.MemberSweep?.JitterFraction,
        MemberSweepPageSize = body.MemberSweep?.PageSize,

        BanSweepPageDelaySeconds = body.BanSweep?.PageDelaySeconds,
        BanSweepRestSeconds = body.BanSweep?.RestSeconds,
        BanSweepRetryIntervalSeconds = body.BanSweep?.RetryIntervalSeconds,
        BanSweepRateLimitedIntervalSeconds = body.BanSweep?.RateLimitedIntervalSeconds,
        BanSweepJitterFraction = body.BanSweep?.JitterFraction,
        BanSweepPageSize = body.BanSweep?.PageSize,
    };

    private static SyncSettingsResponse Describe(
        SyncPacing pacing, bool running, IReadOnlyList<SyncSettingsAdjustment> adjustments)
    {
        var scheduled = VRChatRateLimits.Scheduled.ToHashSet(StringComparer.Ordinal);

        var classes = pacing.Classes.Values
            .Select(limits => new EndpointClassRate(
                limits.Name,
                limits.Lane,
                limits.HardMaxPerSecond,
                limits.DefaultCeilingPerSecond,
                pacing.CeilingFor(limits.Name),
                pacing.EffectiveRatePerSecond(limits.Name),
                pacing.IsConfigured(limits.Name),
                scheduled.Contains(limits.Name),
                limits.CountsAgainstGlobal,
                limits.ResourceScoped,
                limits.BurstTokens,
                limits.Backstop))
            .OrderBy(c => c.EndpointClass, StringComparer.Ordinal)
            .ToList();

        var scheduledTotal = VRChatRateLimits.Scheduled.Sum(pacing.EffectiveRatePerSecond);
        var global = pacing.EffectiveRatePerSecond(VRChatEndpointClass.Global);

        return new SyncSettingsResponse(
            new AuditLogPollRateSettings(
                pacing.AuditLog.MinInterval.TotalSeconds,
                pacing.AuditLog.MaxInterval.TotalSeconds,
                AuditLogSyncOptions.PacingFloor.TotalSeconds,
                pacing.AuditLog.QuietBackoff,
                pacing.AuditLog.JitterFraction,
                pacing.AuditLog.PageSize,
                pacing.AuditLog.MaxPagesPerRun,
                pacing.AuditLog.Overlap.TotalSeconds,
                pacing.AuditLog.CatchUp,
                pacing.AuditLog.MaxCatchUpPages),
            new GroupInfoPollRateSettings(
                pacing.GroupInfo.Interval.TotalSeconds,
                pacing.GroupInfo.RetryInterval.TotalSeconds,
                pacing.GroupInfo.RateLimitedInterval.TotalSeconds,
                GroupInfoSyncOptions.PacingFloor.TotalSeconds,
                pacing.GroupInfo.JitterFraction),
            new UserProfilePollRateSettings(
                pacing.UserProfile.Interval.TotalSeconds,
                UserProfileSyncOptions.PacingFloor.TotalSeconds,
                pacing.UserProfile.StaleAfter.TotalSeconds,
                pacing.UserProfile.RecentWindow.TotalSeconds,
                pacing.UserProfile.FreshEnoughWhenOpened.TotalSeconds,
                pacing.UserProfile.FreshEnoughWhenSeenInInstance.TotalSeconds,
                pacing.UserProfile.RateLimitedInterval.TotalSeconds),
            new SweepPollRateSettings(
                pacing.MemberSweep.PageDelay.TotalSeconds,
                pacing.MemberSweep.RestBetweenSweeps.TotalSeconds,
                pacing.MemberSweep.RetryInterval.TotalSeconds,
                pacing.MemberSweep.RateLimitedInterval.TotalSeconds,
                GroupMemberSyncOptions.PacingFloor.TotalSeconds,
                pacing.MemberSweep.JitterFraction,
                pacing.MemberSweep.PageSize),
            new SweepPollRateSettings(
                pacing.BanSweep.PageDelay.TotalSeconds,
                pacing.BanSweep.RestBetweenSweeps.TotalSeconds,
                pacing.BanSweep.RetryInterval.TotalSeconds,
                pacing.BanSweep.RateLimitedInterval.TotalSeconds,
                GroupBanSyncOptions.PacingFloor.TotalSeconds,
                pacing.BanSweep.JitterFraction,
                pacing.BanSweep.PageSize),
            new SyncRateSettings(
                pacing.BudgetFraction,
                RateLimitOptions.DefaultFraction,
                SyncPacingJson.MinimumFraction,
                scheduledTotal,
                global,
                pacing.Classes[VRChatEndpointClass.Global].HardMaxPerSecond,
                global - scheduledTotal,
                classes),
            Editable: true,
            running,
            RestartRequired: false,
            adjustments);
    }

    /// <remarks>
    /// Same permission the Data screen uses: every setting here changes how much traffic Modbot
    /// sends to somebody else's API under the deployment's own account.
    /// </remarks>
    private static bool Forbidden(HttpContext http)
    {
        var held = ModbotAuth.PermissionsOf(http.User);

        return !held.HasFlag(ModbotPermissions.Administrator)
            && !held.HasFlag(ModbotPermissions.ManageSettings);
    }
}
