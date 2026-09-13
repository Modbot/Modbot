using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Sync;

namespace Modbot.VRChat.Pacing;

/// <summary>
/// What an unconfigured field falls back to.
/// </summary>
/// <remarks>
/// Spec 4.2's defaults, unless the host composing Modbot supplied its own pollRate. The
/// distinction matters because the stored document is sparse: "not configured" has to resolve to
/// something, and resolving it to the compiled default would quietly overrule a host that had
/// deliberately passed a gentler one.
/// </remarks>
public sealed record SyncPacingBaseline(AuditLogSyncOptions AuditLog, GroupInfoSyncOptions GroupInfo);

/// <summary>
/// The pacing actually in force: spec 4.2's defaults with the operator's lowerings applied.
/// </summary>
/// <remarks>
/// <para>
/// The resolved counterpart to <see cref="SyncPacingDocument"/>. The document says what somebody
/// chose; this says what runs. They are separate types because the difference is load-bearing —
/// an absent field in the document means "track the spec", and collapsing the two would turn
/// every read into a write of that release's defaults.
/// </para>
/// <para>
/// Everything here has already been through <see cref="SyncPacingJson.Clamp"/>, so no consumer
/// needs to defend itself against a rate that is too fast. They do anyway:
/// <see cref="TokenBucket.EffectiveRatePerSecond"/> takes the minimum with spec 4.2's cap, and
/// the producers clamp their own intervals. Spec 4.3's asymmetry is the reason — undershooting
/// costs staler data, overshooting costs an opaque multi-minute outage.
/// </para>
/// </remarks>
public sealed record SyncPacing
{
    /// <summary>Spec 4.2's table, unmodified: what a deployment that has configured nothing runs.</summary>
    public static SyncPacing Defaults { get; } = Resolve(SyncPacingDocument.Empty);

    /// <summary>What the operator configured, already clamped.</summary>
    public required SyncPacingDocument Document { get; init; }

    /// <summary>The budgets these rates were resolved against.</summary>
    public required IReadOnlyDictionary<string, RateLimitClassOptions> Classes { get; init; }

    /// <summary>The share of each estimated limit Modbot will spend (spec 4.3.1).</summary>
    public required double BudgetFraction { get; init; }

    public required AuditLogSyncOptions AuditLog { get; init; }

    public required GroupInfoSyncOptions GroupInfo { get; init; }

    /// <summary>
    /// Resolves a stored document into the pacing that runs, clamping as it goes.
    /// </summary>
    /// <param name="document">What the operator configured. Null or empty means the baseline.</param>
    /// <param name="classes">The budgets to clamp against. Spec 4.2's table by default.</param>
    /// <param name="baseline">
    /// What an unconfigured field falls back to. Normally spec 4.2's defaults; a host that passed
    /// its own poll rate to <c>AddModbotVRChatSync</c> gets that instead, so a code-supplied value
    /// is a starting point the operator adjusts rather than something a stored document silently
    /// discards.
    /// </param>
    public static SyncPacing Resolve(
        SyncPacingDocument? document,
        IReadOnlyDictionary<string, RateLimitClassOptions>? classes = null,
        SyncPacingBaseline? baseline = null)
    {
        classes ??= VRChatRateLimits.Defaults;

        var clamped = SyncPacingJson.Clamp(document, out _, classes);
        var audit = baseline?.AuditLog ?? new AuditLogSyncOptions();
        var info = baseline?.GroupInfo ?? new GroupInfoSyncOptions();

        return new SyncPacing
        {
            Document = clamped,
            Classes = classes,
            BudgetFraction = clamped.BudgetFraction ?? RateLimitOptions.DefaultFraction,

            AuditLog = (audit with
            {
                MinInterval = Seconds(clamped.AuditLogMinIntervalSeconds) ?? audit.MinInterval,
                MaxInterval = Seconds(clamped.AuditLogMaxIntervalSeconds) ?? audit.MaxInterval,
                QuietBackoff = clamped.AuditLogQuietBackoff ?? audit.QuietBackoff,
                JitterFraction = clamped.AuditLogJitterFraction ?? audit.JitterFraction,
                PageSize = clamped.AuditLogPageSize ?? audit.PageSize,
                MaxPagesPerRun = clamped.AuditLogMaxPagesPerRun ?? audit.MaxPagesPerRun,
                Overlap = Seconds(clamped.AuditLogOverlapSeconds) ?? audit.Overlap,
                Backfill = clamped.AuditLogBackfill ?? audit.Backfill,
                MaxBackfillPages = clamped.AuditLogMaxBackfillPages ?? audit.MaxBackfillPages,
            }).Clamped(),

            GroupInfo = (info with
            {
                Interval = Seconds(clamped.GroupInfoIntervalSeconds) ?? info.Interval,
                RetryInterval = Seconds(clamped.GroupInfoRetryIntervalSeconds) ?? info.RetryInterval,
                RateLimitedInterval =
                    Seconds(clamped.GroupInfoRateLimitedIntervalSeconds) ?? info.RateLimitedInterval,
                JitterFraction = clamped.GroupInfoJitterFraction ?? info.JitterFraction,
            }).Clamped(),
        };
    }

    /// <summary>The estimate of VRChat's limit in force for one endpoint class.</summary>
    public double CeilingFor(string endpointClass)
    {
        ArgumentNullException.ThrowIfNull(endpointClass);

        if (Document.ClassCeilingsPerSecond is { } configured
            && configured.TryGetValue(endpointClass, out var ceiling))
        {
            return ceiling;
        }

        return Classes.TryGetValue(endpointClass, out var limits)
            ? limits.DefaultCeilingPerSecond
            : 0;
    }

    /// <summary>True when the operator has moved this class's rate off the default.</summary>
    public bool IsConfigured(string endpointClass) =>
        Document.ClassCeilingsPerSecond?.ContainsKey(endpointClass) == true;

    /// <summary>
    /// The rate a fresh bucket for this class would issue at: the configured share of the
    /// estimate, never above spec 4.2's cap.
    /// </summary>
    /// <remarks>
    /// This is the number a settings screen shows and sums. It deliberately excludes the AIMD
    /// multiplier, which is not configuration — it is the limiter's live opinion of the estimate
    /// (spec 4.3.1), and a slider that moved on its own because a 429 landed would be unreadable.
    /// The live figure is per-bucket gate health (spec 4.3.3), on the health screen.
    /// </remarks>
    public double EffectiveRatePerSecond(string endpointClass) =>
        Classes.TryGetValue(endpointClass, out var limits)
            ? Math.Min(limits.HardMaxPerSecond, CeilingFor(endpointClass) * BudgetFraction)
            : 0;

    private static TimeSpan? Seconds(double? value) =>
        value is { } seconds && double.IsFinite(seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
}
