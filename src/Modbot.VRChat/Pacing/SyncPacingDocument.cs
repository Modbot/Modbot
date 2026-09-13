using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Sync;

namespace Modbot.VRChat.Pacing;

/// <summary>
/// One value the operator asked for that Modbot refused to store as written.
/// </summary>
/// <param name="Field">The JSON path of the field, as the settings screen names it.</param>
/// <param name="Requested">What arrived.</param>
/// <param name="Stored">What was written instead. Never faster than <paramref name="Requested"/>.</param>
/// <param name="Reason">Why, in a sentence an operator can act on.</param>
/// <remarks>
/// Spec 4.2.1 puts the cap on the write rather than in the UI, which means a request can be
/// partially honoured — and a settings screen that silently stored something other than what was
/// typed would be the same defect as a slider that discarded its value. So every clamp is named
/// and handed back.
/// </remarks>
public sealed record PacingAdjustment(string Field, double Requested, double Stored, string Reason);

/// <summary>
/// What the operator has actually configured — every field optional, absent meaning "spec 4.2's
/// default".
/// </summary>
/// <remarks>
/// <para>
/// This is both the wire shape of <c>PUT /api/settings/sync</c> and the shape of the
/// <c>sync_pacing</c> <c>jsonb</c> column, deliberately. Keeping the persisted form sparse is
/// what makes an untouched deployment track the spec: if §4.2 revises a rate, a deployment that
/// never moved that slider picks the new value up, while one that deliberately lowered it keeps
/// its own choice. A document that stored every field would freeze the defaults of whichever
/// release first wrote it.
/// </para>
/// <para>
/// Flat rather than nested because the common operation is a partial update — a screen that
/// changes one slider sends one field — and merging one level of nullable scalars is something
/// that can be read and checked. Nested optional objects would need "absent" and "present but
/// empty" to mean different things at two levels.
/// </para>
/// </remarks>
public sealed record SyncPacingDocument
{
    /// <summary>Nothing configured: every rate and interval is spec 4.2's.</summary>
    public static SyncPacingDocument Empty { get; } = new();

    /// <summary>
    /// Per-endpoint-class estimates of VRChat's real limit, keyed by endpoint class
    /// (spec 4.3.1). Only classes the operator actually changed appear.
    /// </summary>
    /// <remarks>
    /// The <em>estimate</em>, not the rate Modbot issues: Modbot runs at
    /// <see cref="BudgetFraction"/> of it and never bursts to it. The rate that results is
    /// clamped to spec 4.2's cap in two independent places — here on write, and again in
    /// <see cref="TokenBucket.EffectiveRatePerSecond"/> — because a configuration path that could
    /// raise a rate is the one bug in this file whose cost is a punitive ban rather than a slow
    /// sync.
    /// </remarks>
    public IReadOnlyDictionary<string, double>? ClassCeilingsPerSecond { get; init; }

    /// <summary>The fraction of each estimate Modbot is willing to spend (spec 4.3.1).</summary>
    public double? BudgetFraction { get; init; }

    public double? AuditLogMinIntervalSeconds { get; init; }
    public double? AuditLogMaxIntervalSeconds { get; init; }
    public double? AuditLogQuietBackoff { get; init; }
    public double? AuditLogJitterFraction { get; init; }
    public int? AuditLogPageSize { get; init; }
    public int? AuditLogMaxPagesPerRun { get; init; }
    public double? AuditLogOverlapSeconds { get; init; }
    // The two catch-up fields keep the key names they were first stored under. This document
    // is the settings table's sync_pacing column as well as a wire shape, and a renamed key
    // would silently drop a setting an operator had already saved.
    [JsonPropertyName("auditLogCatchUp")]
    public bool? AuditLogCatchUp { get; init; }

    [JsonPropertyName("auditLogMaxCatchUpPages")]
    public int? AuditLogMaxCatchUpPages { get; init; }

    public double? GroupInfoIntervalSeconds { get; init; }
    public double? GroupInfoRetryIntervalSeconds { get; init; }
    public double? GroupInfoRateLimitedIntervalSeconds { get; init; }
    public double? GroupInfoJitterFraction { get; init; }

    /// <summary>True when nothing at all has been configured.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        (ClassCeilingsPerSecond is null || ClassCeilingsPerSecond.Count == 0)
        && BudgetFraction is null
        && AuditLogMinIntervalSeconds is null
        && AuditLogMaxIntervalSeconds is null
        && AuditLogQuietBackoff is null
        && AuditLogJitterFraction is null
        && AuditLogPageSize is null
        && AuditLogMaxPagesPerRun is null
        && AuditLogOverlapSeconds is null
        && AuditLogCatchUp is null
        && AuditLogMaxCatchUpPages is null
        && GroupInfoIntervalSeconds is null
        && GroupInfoRetryIntervalSeconds is null
        && GroupInfoRateLimitedIntervalSeconds is null
        && GroupInfoJitterFraction is null;

    /// <summary>
    /// Overlays every field <paramref name="change"/> actually sets onto this document.
    /// </summary>
    /// <remarks>
    /// Null means "leave alone", never "reset to default". A screen that sends one slider must
    /// not wipe the other fourteen, and a client that omits a field it does not understand — an
    /// older web build talking to a newer server — must not silently undo a setting it could not
    /// render.
    /// </remarks>
    public SyncPacingDocument MergedWith(SyncPacingDocument? change)
    {
        if (change is null)
            return this;

        var ceilings = ClassCeilingsPerSecond;

        if (change.ClassCeilingsPerSecond is { Count: > 0 })
        {
            var merged = ceilings is null
                ? new Dictionary<string, double>(StringComparer.Ordinal)
                : new Dictionary<string, double>(ceilings, StringComparer.Ordinal);

            foreach (var (name, value) in change.ClassCeilingsPerSecond)
                merged[name] = value;

            ceilings = merged;
        }

        return this with
        {
            ClassCeilingsPerSecond = ceilings,
            BudgetFraction = change.BudgetFraction ?? BudgetFraction,
            AuditLogMinIntervalSeconds = change.AuditLogMinIntervalSeconds ?? AuditLogMinIntervalSeconds,
            AuditLogMaxIntervalSeconds = change.AuditLogMaxIntervalSeconds ?? AuditLogMaxIntervalSeconds,
            AuditLogQuietBackoff = change.AuditLogQuietBackoff ?? AuditLogQuietBackoff,
            AuditLogJitterFraction = change.AuditLogJitterFraction ?? AuditLogJitterFraction,
            AuditLogPageSize = change.AuditLogPageSize ?? AuditLogPageSize,
            AuditLogMaxPagesPerRun = change.AuditLogMaxPagesPerRun ?? AuditLogMaxPagesPerRun,
            AuditLogOverlapSeconds = change.AuditLogOverlapSeconds ?? AuditLogOverlapSeconds,
            AuditLogCatchUp = change.AuditLogCatchUp ?? AuditLogCatchUp,
            AuditLogMaxCatchUpPages = change.AuditLogMaxCatchUpPages ?? AuditLogMaxCatchUpPages,
            GroupInfoIntervalSeconds = change.GroupInfoIntervalSeconds ?? GroupInfoIntervalSeconds,
            GroupInfoRetryIntervalSeconds = change.GroupInfoRetryIntervalSeconds ?? GroupInfoRetryIntervalSeconds,
            GroupInfoRateLimitedIntervalSeconds =
                change.GroupInfoRateLimitedIntervalSeconds ?? GroupInfoRateLimitedIntervalSeconds,
            GroupInfoJitterFraction = change.GroupInfoJitterFraction ?? GroupInfoJitterFraction,
        };
    }
}

/// <summary>
/// Reads and writes the <c>sync_pacing</c> column, and applies spec 4.2.1's cap on the way past.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Clamped on read as well as on write.</strong> The write-side clamp is what spec 4.2.1
/// asks for; the read-side one is what makes it true of a row this build did not write — a
/// hand-edited database, a restored backup, a value written by a version whose cap was looser.
/// Neither is redundant: without the write clamp the stored number would lie about what is
/// running, and without the read clamp the stored number would be trusted.
/// </para>
/// <para>
/// A value that cannot be made sense of at all — a negative rate, a NaN, a class Modbot does not
/// have a budget for — is dropped rather than coerced, so the field falls back to spec 4.2's
/// default. Coercing a negative rate to a small positive one would invent a number nobody chose;
/// falling back to the default at least lands on a number the spec argued for.
/// </para>
/// </remarks>
public static class SyncPacingJson
{
    /// <summary>
    /// The floor on <see cref="SyncPacingDocument.BudgetFraction"/>.
    /// </summary>
    /// <remarks>
    /// Not zero. A fraction of zero produces an effective rate of zero, and a bucket at zero
    /// never issues anything again — <c>TimeUntilToken</c> returns <c>TimeSpan.MaxValue</c> — so
    /// "gentler" would become "stopped, permanently, with no cold stop to explain it". One
    /// percent of the estimate is as gentle as the settings screen allows anyone to be.
    /// </remarks>
    public const double MinimumFraction = 0.01;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Parses the column. Anything unreadable resolves to "nothing configured".</summary>
    /// <remarks>
    /// Swallowed rather than thrown, because the caller is a producer loop or a limiter on the
    /// request path. A malformed settings blob must degrade to spec 4.2's defaults, which are
    /// safe; throwing would stop the audit log over a bad character.
    /// </remarks>
    public static SyncPacingDocument Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return SyncPacingDocument.Empty;

        try
        {
            return JsonSerializer.Deserialize<SyncPacingDocument>(json, Options)
                ?? SyncPacingDocument.Empty;
        }
        catch (JsonException)
        {
            return SyncPacingDocument.Empty;
        }
    }

    /// <summary>Serialises the column. An empty document is stored as SQL null, not as <c>{}</c>.</summary>
    public static string? Write(SyncPacingDocument? document) =>
        document is null || document.IsEmpty
            ? null
            : JsonSerializer.Serialize(document, Options);

    /// <summary>
    /// Applies spec 4.2.1: every configured value may make Modbot gentler and nothing may make it
    /// faster than the table in spec 4.2.
    /// </summary>
    public static SyncPacingDocument Clamp(
        SyncPacingDocument? document,
        out IReadOnlyList<PacingAdjustment> adjustments,
        IReadOnlyDictionary<string, RateLimitClassOptions>? classes = null)
    {
        var found = new List<PacingAdjustment>();
        adjustments = found;

        if (document is null || document.IsEmpty)
            return SyncPacingDocument.Empty;

        classes ??= VRChatRateLimits.Defaults;

        var fraction = ClampFraction(document.BudgetFraction, found);
        var effectiveFraction = fraction ?? RateLimitOptions.DefaultFraction;

        return document with
        {
            BudgetFraction = fraction,
            ClassCeilingsPerSecond = ClampCeilings(
                document.ClassCeilingsPerSecond, effectiveFraction, classes, found),

            // Intervals: raising one is always allowed (staleness is the only cost) and lowering
            // one past the endpoint class's pacing cap is exactly what spec 4.2.1 forbids.
            AuditLogMinIntervalSeconds = AtLeast(
                document.AuditLogMinIntervalSeconds,
                AuditLogSyncOptions.PacingFloor.TotalSeconds,
                "auditLogMinIntervalSeconds",
                "spec 4.2 paces groups.auditlog at one request per 8 seconds",
                found),

            AuditLogMaxIntervalSeconds = AtLeast(
                document.AuditLogMaxIntervalSeconds,
                AuditLogSyncOptions.PacingFloor.TotalSeconds,
                "auditLogMaxIntervalSeconds",
                "the slowest interval cannot be faster than the pacing cap",
                found),

            AuditLogQuietBackoff = AtLeast(
                document.AuditLogQuietBackoff, 1.0, "auditLogQuietBackoff",
                "a backoff below 1 would make each quiet poll faster than the last", found),

            AuditLogJitterFraction = Within(
                document.AuditLogJitterFraction, 0, 0.5, "auditLogJitterFraction",
                "jitter is a fraction of the interval (spec 4.2.2)", found),

            AuditLogPageSize = Within(
                document.AuditLogPageSize, 1, 100, "auditLogPageSize",
                "VRChat caps the audit-log page at 100 entries", found),

            AuditLogMaxPagesPerRun = AtLeast(
                document.AuditLogMaxPagesPerRun, 1, "auditLogMaxPagesPerRun",
                "a pass that may read no pages would never catch up", found),

            AuditLogOverlapSeconds = AtLeast(
                document.AuditLogOverlapSeconds, 0, "auditLogOverlapSeconds",
                "the re-read window cannot be negative", found),

            AuditLogMaxCatchUpPages = AtLeast(
                document.AuditLogMaxCatchUpPages, 1, "auditLogMaxCatchUpPages",
                "a catch-up limited to no pages would never start", found),

            GroupInfoIntervalSeconds = AtLeast(
                document.GroupInfoIntervalSeconds,
                GroupInfoSyncOptions.PacingFloor.TotalSeconds,
                "groupInfoIntervalSeconds",
                "spec 4.2 paces groups.read at one request per 10 seconds",
                found),

            GroupInfoRetryIntervalSeconds = AtLeast(
                document.GroupInfoRetryIntervalSeconds,
                GroupInfoSyncOptions.PacingFloor.TotalSeconds,
                "groupInfoRetryIntervalSeconds",
                "a retry is a request like any other and obeys the same cap",
                found),

            GroupInfoRateLimitedIntervalSeconds = AtLeast(
                document.GroupInfoRateLimitedIntervalSeconds,
                GroupInfoSyncOptions.PacingFloor.TotalSeconds,
                "groupInfoRateLimitedIntervalSeconds",
                "polling harder while cold-stopped is what spec 4.3.1 forbids",
                found),

            GroupInfoJitterFraction = Within(
                document.GroupInfoJitterFraction, 0, 0.5, "groupInfoJitterFraction",
                "jitter is a fraction of the interval (spec 4.2.2)", found),
        };
    }

    private static double? ClampFraction(double? requested, List<PacingAdjustment> found)
    {
        if (requested is not { } value)
            return null;

        if (!double.IsFinite(value) || value <= 0)
        {
            found.Add(new PacingAdjustment(
                "budgetFraction", value, RateLimitOptions.DefaultFraction,
                "A budget fraction must be a positive number; spec 4.3.1's default was used instead."));

            return null;
        }

        var clamped = Math.Clamp(value, MinimumFraction, 1.0);

        if (Math.Abs(clamped - value) > double.Epsilon)
        {
            found.Add(new PacingAdjustment(
                "budgetFraction", value, clamped,
                $"The budget fraction is a share of the estimated limit, between {MinimumFraction} and 1."));
        }

        return clamped;
    }

    private static IReadOnlyDictionary<string, double>? ClampCeilings(
        IReadOnlyDictionary<string, double>? requested,
        double fraction,
        IReadOnlyDictionary<string, RateLimitClassOptions> classes,
        List<PacingAdjustment> found)
    {
        if (requested is not { Count: > 0 })
            return null;

        var kept = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var (name, value) in requested)
        {
            var field = $"classCeilingsPerSecond.{name}";

            if (!classes.TryGetValue(name, out var limits))
            {
                // Spec 4.3.4 is a standing instruction: a class Modbot has no budget for is a
                // question to ask, never a rate to invent from a settings write.
                found.Add(new PacingAdjustment(
                    field, value, 0,
                    $"'{name}' is not an endpoint class Modbot budgets, so it was discarded."));

                continue;
            }

            if (!double.IsFinite(value) || value <= 0)
            {
                found.Add(new PacingAdjustment(
                    field, value, limits.DefaultCeilingPerSecond,
                    "A rate must be a positive number; a bucket at zero would never issue again. "
                    + "Spec 4.2's default was used instead."));

                continue;
            }

            // The estimate whose configured share is exactly spec 4.2's cap. Anything above it
            // would be a number that cannot affect the rate, stored on a screen whose entire job
            // is to say what the rate is.
            var maximum = limits.HardMaxPerSecond / fraction;

            if (value > maximum)
            {
                found.Add(new PacingAdjustment(
                    field, value, maximum,
                    $"Spec 4.2 caps {name} at {limits.HardMaxPerSecond:0.###} req/s, and "
                    + $"configuration may only lower a rate. At a budget fraction of "
                    + $"{fraction:0.###} that is an estimate of {maximum:0.###} req/s."));

                kept[name] = maximum;
                continue;
            }

            kept[name] = value;
        }

        return kept.Count == 0 ? null : kept;
    }

    private static double? AtLeast(
        double? requested, double floor, string field, string why, List<PacingAdjustment> found)
    {
        if (requested is not { } value)
            return null;

        if (!double.IsFinite(value))
        {
            found.Add(new PacingAdjustment(field, value, floor, $"Not a number; {why}."));
            return null;
        }

        if (value >= floor)
            return value;

        found.Add(new PacingAdjustment(
            field, value, floor, $"Raised to {floor:0.###}: {why}."));

        return floor;
    }

    private static int? AtLeast(
        int? requested, int floor, string field, string why, List<PacingAdjustment> found)
    {
        if (requested is not { } value || value >= floor)
            return requested;

        found.Add(new PacingAdjustment(field, value, floor, $"Raised to {floor}: {why}."));

        return floor;
    }

    private static double? Within(
        double? requested, double low, double high, string field, string why, List<PacingAdjustment> found)
    {
        if (requested is not { } value)
            return null;

        if (!double.IsFinite(value))
        {
            found.Add(new PacingAdjustment(field, value, low, $"Not a number; {why}."));
            return null;
        }

        var clamped = Math.Clamp(value, low, high);

        if (Math.Abs(clamped - value) <= double.Epsilon)
            return value;

        found.Add(new PacingAdjustment(
            field, value, clamped, $"Clamped to {low}–{high}: {why}."));

        return clamped;
    }

    private static int? Within(
        int? requested, int low, int high, string field, string why, List<PacingAdjustment> found)
    {
        if (requested is not { } value)
            return null;

        var clamped = Math.Clamp(value, low, high);

        if (clamped == value)
            return value;

        found.Add(new PacingAdjustment(
            field, value, clamped, $"Clamped to {low}–{high}: {why}."));

        return clamped;
    }
}
