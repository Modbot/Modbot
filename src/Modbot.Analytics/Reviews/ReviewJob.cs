using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Analytics.Reviews;

/// <summary>
/// The detection run: repeat-offender counts, moderator baselines, and the pattern checks that
/// open reviews (spec 5.8.4, 5.8.5).
/// </summary>
/// <remarks>
/// <para>
/// Runs after the daily totals, on their schedule, because the baselines are summed from them.
/// An incremental run looks only at what arrived since the last one: the people with new facts,
/// the moderators who acted, and the days those actions fell on. A rebuild does the whole log.
/// Both produce the same rows, because every table here is a cache and every review is keyed so
/// that re-finding the same pattern is a no-op (see <see cref="Review"/>).
/// </para>
/// <para>
/// <strong>The idempotence rule, in one place.</strong> For each finding: if a review for the same
/// moderator, signal and thing is open, its numbers are refreshed and nothing else happens. If
/// not, and a closed one exists whose window already reaches the finding's last fact, nothing
/// happens. Otherwise a review opens, and a fact records that it did. Closing is the API's job
/// and is also a fact.
/// </para>
/// <para>
/// The whole run is one transaction under an advisory lock, so two runs cannot interleave and a
/// failure part-way leaves the caches as they were.
/// </para>
/// </remarks>
public sealed class ReviewJob
{
    /// <summary>Same reason as the daily totals': <c>observed_at</c> is stamped before the commit that makes the fact visible.</summary>
    public static readonly TimeSpan WatermarkOverlap = TimeSpan.FromMinutes(1);

    private const long ReviewLockKey = 0x4D4F44_524556; // "MOD" "REV"

    private readonly ModbotContext _db;
    private readonly ReviewFacts _facts;
    private readonly IModbotClock _clock;

    public ReviewJob(ModbotContext db, ReviewFacts facts, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _facts = facts;
        _clock = clock;
    }

    /// <summary>Looks at everything learned since the last run.</summary>
    public Task<ReviewRunResult> RunIncrementalAsync(CancellationToken ct = default)
        => RunAsync(rebuild: false, ct);

    /// <summary>Throws the caches away and recomputes them from the whole fact log.</summary>
    public Task<ReviewRunResult> RebuildAsync(CancellationToken ct = default)
        => RunAsync(rebuild: true, ct);

    private async Task<ReviewRunResult> RunAsync(bool rebuild, CancellationToken ct)
    {
        var now = _clock.UtcNow;

        var ownsTransaction = _db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await _db.Database.BeginTransactionAsync(ct) : null;

        try
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({ReviewLockKey})", ct);

            var thresholds = await ThresholdsAsync(ct);
            var state = await StateAsync(ct);
            var since = rebuild ? null : state.ObservedThrough - WatermarkOverlap;
            var highWater = await MaxObservedAtAsync(ct);

            // 0. Which facts are the same decision (spec 5.3.2). Before the counting, because the
            // counts leave a decision's second fact out, and a link found a run late would mean a
            // person sat above the threshold for a day on a decision that was only ever one.
            //
            // The whole log is read when this is a rebuild, and also the first time a deployment
            // runs a version whose set of pairs it has not read under -- which includes every
            // deployment upgrading to the first version that links anything at all. Otherwise only
            // what has arrived since the last run.
            if (rebuild || state.LinkVersion < FactLinker.Version)
            {
                await FactLinker.RebuildAsync(_db, now, ct);
                state.LinkVersion = FactLinker.Version;
            }
            else
            {
                await FactLinker.RelinkAsync(_db, since, now, ct);
            }

            // 1. Repeat offenders: the people touched, or everybody.
            var subjects = rebuild ? null : await RepeatOffenderCounter.SubjectsDueAsync(_db, since, now, ct);
            var offenderRows = await RepeatOffenderCounter.RecomputeAsync(_db, subjects, now, thresholds, ct);

            // 2. Baselines, from the daily totals. Cheap enough to do whole every time.
            var team = await ModeratorBaselines.RecomputeAsync(_db, now, thresholds, ct);

            // 3. The checks, over the moderators and days touched -- or everything recent.
            var actors = rebuild ? null : await ActorsTouchedAsync(since, ct);
            var days = await DaysTouchedAsync(rebuild ? null : since, now, thresholds, ct);

            var findings = new List<Finding>();
            findings.AddRange(await PatternChecks.SamePersonAsync(_db, actors, now, thresholds, ct));
            findings.AddRange(await PatternChecks.FarAboveTeamAsync(_db, days, team, now, thresholds, ct));

            var opened = 0;
            var refreshed = 0;

            foreach (var finding in findings)
            {
                switch (await ApplyAsync(finding, now, ct))
                {
                    case Applied.Opened: opened++; break;
                    case Applied.Refreshed: refreshed++; break;
                }
            }

            // Never move the mark backwards: an empty log must not undo an earlier run.
            if (highWater is not null && (state.ObservedThrough is null || highWater > state.ObservedThrough))
                state.ObservedThrough = highWater;
            state.UpdatedAt = now;

            await _db.SaveChangesAsync(ct);

            if (transaction is not null)
                await transaction.CommitAsync(ct);

            return new ReviewRunResult(offenderRows, opened, refreshed);
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private enum Applied { Nothing, Opened, Refreshed }

    private async Task<Applied> ApplyAsync(Finding finding, DateTimeOffset now, CancellationToken ct)
    {
        var open = await _db.Reviews.FirstOrDefaultAsync(r =>
            r.ModeratorPlatform == finding.ModeratorPlatform
            && r.ModeratorId == finding.ModeratorId
            && r.Signal == finding.Signal
            && r.About == finding.About
            && r.State == ReviewState.Open, ct);

        var evidence = finding.Evidence.ToJsonString();

        if (open is not null)
        {
            // Same pattern, still open: the numbers move, the review does not multiply. Compared
            // as JSON rather than as text, because jsonb comes back reformatted.
            if (open.Summary == finding.Summary && SameJson(open.Evidence, finding.Evidence))
                return Applied.Nothing;

            open.Summary = finding.Summary;
            open.Evidence = evidence;
            open.WindowStart = finding.WindowStart;
            open.WindowEnd = finding.WindowEnd;
            open.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);

            return Applied.Refreshed;
        }

        var closedThrough = await _db.Reviews
            .Where(r => r.ModeratorPlatform == finding.ModeratorPlatform
                     && r.ModeratorId == finding.ModeratorId
                     && r.Signal == finding.Signal
                     && r.About == finding.About
                     && r.State == ReviewState.Closed)
            .MaxAsync(r => (DateTimeOffset?)r.WindowEnd, ct);

        // Already reviewed and closed, and nothing newer than what that review covered.
        if (closedThrough is { } through && finding.WindowEnd <= through)
            return Applied.Nothing;

        var review = new Review
        {
            ModeratorPlatform = finding.ModeratorPlatform,
            ModeratorId = finding.ModeratorId,
            Signal = finding.Signal,
            About = finding.About,
            WindowStart = finding.WindowStart,
            WindowEnd = finding.WindowEnd,
            Summary = finding.Summary,
            Evidence = evidence,
            State = ReviewState.Open,
            OpenedAt = now,
            UpdatedAt = now,
        };

        _db.Reviews.Add(review);
        await _db.SaveChangesAsync(ct);
        await _facts.OpenedAsync(review, ct);

        return Applied.Opened;
    }

    private static bool SameJson(string stored, System.Text.Json.Nodes.JsonObject fresh)
    {
        try
        {
            return System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(stored), fresh);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>The moderators with a new action since the watermark.</summary>
    private async Task<IReadOnlyList<string>> ActorsTouchedAsync(DateTimeOffset? since, CancellationToken ct)
    {
        var observed = since is null ? string.Empty : "AND e.observed_at > @since";

        var sql = $"""
            SELECT DISTINCT e.actor_id
            FROM modbot_event e
            WHERE e.type = ANY(@types) AND e.actor_platform = @vrchat AND e.actor_id IS NOT NULL {observed}
            """;

        var parameters = new List<(string, object?)>
        {
            ("types", ActionsOnPeople.Types),
            ("vrchat", (short)FactPlatform.VRChat),
        };

        if (since is not null)
            parameters.Add(("since", since.Value));

        return await ReviewSql.ReadAsync(_db, sql, r => r.GetString(0), ct, parameters.ToArray());
    }

    /// <summary>
    /// The UTC days with a new action since the watermark, limited to the recent past. The
    /// catch-up walk hands over months-old history; a review about a day last spring would be a
    /// question nobody can usefully answer now.
    /// </summary>
    private async Task<IReadOnlyList<DateOnly>> DaysTouchedAsync(
        DateTimeOffset? since,
        DateTimeOffset now,
        ReviewThresholds thresholds,
        CancellationToken ct)
    {
        var observed = since is null ? string.Empty : "AND e.observed_at > @since";

        var sql = $"""
            SELECT DISTINCT (e.occurred_at AT TIME ZONE 'UTC')::date
            FROM modbot_event e
            WHERE e.type = ANY(@types) AND e.actor_platform = @vrchat AND e.actor_id IS NOT NULL
              AND e.occurred_at > @recent AND e.occurred_at <= @now {observed}
            """;

        var parameters = new List<(string, object?)>
        {
            ("types", ActionsOnPeople.Types),
            ("vrchat", (short)FactPlatform.VRChat),
            ("recent", now.AddDays(-thresholds.SamePersonDays)),
            ("now", now),
        };

        if (since is not null)
            parameters.Add(("since", since.Value));

        return await ReviewSql.ReadAsync(_db, sql, r => DateOnly.FromDateTime(r.GetDateTime(0)), ct, parameters.ToArray());
    }

    private async Task<ReviewThresholds> ThresholdsAsync(CancellationToken ct)
    {
        // Read, never created: detection can run before onboarding writes the row.
        var json = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.ReviewThresholds)
            .FirstOrDefaultAsync(ct);

        return ReviewThresholds.Read(json);
    }

    private async Task<ReviewRunState> StateAsync(CancellationToken ct)
    {
        var state = await _db.ReviewRunState.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (state is not null)
            return state;

        state = new ReviewRunState { Id = 1 };
        _db.ReviewRunState.Add(state);

        return state;
    }

    private async Task<DateTimeOffset?> MaxObservedAtAsync(CancellationToken ct)
        => (await ReviewSql.ReadAsync(_db, "SELECT MAX(observed_at) FROM modbot_event", r => ReviewSql.InstantOrNull(r, 0), ct))
            .FirstOrDefault();
}

/// <param name="RepeatOffenderRows">Rows written to <c>modbot_repeat_offender</c>.</param>
/// <param name="ReviewsOpened">New reviews, each with a fact behind it.</param>
/// <param name="ReviewsRefreshed">Open reviews whose numbers moved.</param>
public readonly record struct ReviewRunResult(int RepeatOffenderRows, int ReviewsOpened, int ReviewsRefreshed);
