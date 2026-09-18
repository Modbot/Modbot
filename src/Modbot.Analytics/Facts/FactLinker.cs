using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Analytics.Facts;

/// <summary>
/// Works out which facts belong to the same decision and records it in <c>modbot_linked_fact</c>.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.3.2. The pairs themselves, and why each one exists, are in <see cref="LinkedActions"/>;
/// this is only the finding and the writing.
/// </para>
/// <para>
/// <strong>Order of arrival does not matter.</strong> Every fact, as it lands, looks both ways:
/// if it is a follower it looks for its main, and if it is a main it looks for followers already
/// waiting. So a ban that arrives from VRChat's audit log a poll after the moderator's own client
/// reported the kick links just as well as the other way round, and neither fact is touched to do
/// it -- the link is a row beside them (facts are never updated in place; see
/// <see cref="ModbotEvent"/>).
/// </para>
/// <para>
/// <strong>Nothing here may merge two decisions.</strong> Three rules do that work: the pair must
/// be inside <see cref="LinkedActions.Window"/>, which is ten seconds and nowhere near the
/// ban-unban-ban case; a fact whose time is only known to a window never links at all, because
/// "at the same moment" is not a question an inferred time can answer; and a fact already in a
/// decision is never taken into a second one.
/// </para>
/// </remarks>
public static class FactLinker
{
    /// <summary>
    /// Which set of pairs the stored links were found under.
    /// </summary>
    /// <remarks>
    /// <strong>Raise this whenever <see cref="LinkedActions.Pairs"/> changes.</strong> A
    /// deployment whose <c>modbot_review_run_state.link_version</c> is behind reads the whole log
    /// again on its next run, so the history is linked under the new set rather than only what
    /// arrives from now on. That is also how facts recorded before linking existed get their
    /// links: every deployment starts at zero.
    /// </remarks>
    public const int Version = 1;

    /// <summary>
    /// Links one freshly written fact to its partner, if it has one. Returns the rows written.
    /// </summary>
    /// <remarks>
    /// Cheap to call on every fact: a type that is in no pair -- which is almost every fact,
    /// because presence reports are the bulk of the log -- returns before touching the database.
    /// </remarks>
    public static async Task<int> LinkAsync(
        ModbotContext db,
        ModbotEvent fact,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(fact);

        if (fact.OccurredBefore is not null)
            return 0;

        var mains = LinkedActions.MainsOf(fact.Type);
        var followers = LinkedActions.FollowersOf(fact.Type);

        if (mains.Length == 0 && followers.Length == 0)
            return 0;

        var written = mains.Length > 0 ? await AsFollowerAsync(db, fact, mains, now, ct) : 0;
        written += followers.Length > 0 ? await AsMainAsync(db, fact, followers, now, ct) : 0;

        if (written > 0)
            await db.SaveChangesAsync(ct);

        return written;
    }

    /// <summary>
    /// Re-reads everything learned since <paramref name="observedSince"/> and links what it can.
    /// </summary>
    /// <remarks>
    /// The safety net under <see cref="LinkAsync"/>. A fact written outside a transaction is
    /// committed before its link is, so a process that stops in between leaves a decision
    /// half-linked; an import writes thousands of facts whose partners may be a file apart. Both
    /// are put right here, on the schedule the standings are rebuilt on, because a link nobody
    /// re-checks is a count that stays wrong.
    /// </remarks>
    public static async Task<int> RelinkAsync(
        ModbotContext db,
        DateTimeOffset? observedSince,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var followerTypes = LinkedActions.Pairs.Select(p => p.Follower).Distinct(StringComparer.Ordinal).ToArray();

        var query = db.Events.AsNoTracking()
            .Where(e => followerTypes.Contains(e.Type) && e.OccurredBefore == null);

        if (observedSince is { } since)
            query = query.Where(e => e.ObservedAt > since);

        var candidates = await query
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        var written = 0;

        foreach (var candidate in candidates)
            written += await AsFollowerAsync(db, candidate, LinkedActions.MainsOf(candidate.Type), now, ct);

        if (written > 0)
            await db.SaveChangesAsync(ct);

        return written;
    }

    /// <summary>Throws every link away and finds them all again from the facts.</summary>
    public static async Task<int> RebuildAsync(ModbotContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        await db.LinkedFacts.ExecuteDeleteAsync(ct);

        // Only the link rows are forgotten. Clearing the whole change tracker would throw away
        // whatever the caller is part way through -- the review run's own state row, for one.
        foreach (var entry in db.ChangeTracker.Entries<LinkedFact>().ToList())
            entry.State = EntityState.Detached;

        return await RelinkAsync(db, observedSince: null, now, ct);
    }

    /// <summary>
    /// Drops link rows whose facts are gone, so retention and purges do not leave the table
    /// describing decisions nobody can read any more.
    /// </summary>
    public static Task<int> PruneAsync(ModbotContext db, DateTimeOffset before, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.LinkedFacts.Where(l => l.OccurredAt < before).ExecuteDeleteAsync(ct);
    }

    private static async Task<int> AsFollowerAsync(
        ModbotContext db,
        ModbotEvent follower,
        string[] mains,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (mains.Length == 0)
            return 0;

        // Already part of a decision. A fact is never taken into a second one -- that is what
        // stops a busy minute from collapsing into one entry.
        var linked = await db.LinkedFacts.AsNoTracking().AnyAsync(l => l.FactId == follower.Id, ct)
            || db.ChangeTracker.Entries<LinkedFact>().Any(e => e.Entity.FactId == follower.Id);

        if (linked)
            return 0;

        var candidates = await CandidatesAsync(db, follower, mains, ct);

        var main = candidates
            .Where(m => m.Id != follower.Id && LinkedActions.CouldBeOneDecision(m, follower))
            // Nearest in time wins, and the earlier row breaks a tie, the same rule the
            // deduplication window follows (spec 5.7.1): whichever fact was there first.
            .OrderBy(m => (m.OccurredAt - follower.OccurredAt).Duration())
            .ThenBy(m => m.Id)
            .FirstOrDefault();

        if (main is null)
            return 0;

        Add(db, follower, main, now);
        return 1;
    }

    private static async Task<int> AsMainAsync(
        ModbotContext db,
        ModbotEvent main,
        string[] followers,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var candidates = await CandidatesAsync(db, main, followers, ct);

        if (candidates.Count == 0)
            return 0;

        var ids = candidates.Select(c => c.Id).ToList();

        var alreadyLinked = await db.LinkedFacts.AsNoTracking()
            .Where(l => ids.Contains(l.FactId))
            .Select(l => l.FactId)
            .ToListAsync(ct);

        var taken = new HashSet<long>(alreadyLinked);
        foreach (var entry in db.ChangeTracker.Entries<LinkedFact>())
            taken.Add(entry.Entity.FactId);

        var written = 0;

        // One decision can leave more than one follower behind -- a Discord ban raises both a ban
        // and a leave, and a group ban can arrive beside its instance kick and Modbot's own record
        // of the press. Each unclaimed one joins the decision; none of them is counted again.
        foreach (var follower in candidates.OrderBy(c => (c.OccurredAt - main.OccurredAt).Duration()).ThenBy(c => c.Id))
        {
            if (follower.Id == main.Id || taken.Contains(follower.Id))
                continue;

            if (!LinkedActions.CouldBeOneDecision(main, follower))
                continue;

            Add(db, follower, main, now);
            taken.Add(follower.Id);
            written++;
        }

        return written;
    }

    /// <summary>
    /// Facts of the wanted types about the same person, inside the window.
    /// </summary>
    /// <remarks>
    /// The <c>occurred_at</c> range is what keeps this to one or two partitions of a table that
    /// holds years; the subject index answers the rest.
    /// </remarks>
    private static Task<List<ModbotEvent>> CandidatesAsync(
        ModbotContext db,
        ModbotEvent fact,
        string[] types,
        CancellationToken ct)
    {
        var from = fact.OccurredAt - LinkedActions.Window;
        var to = fact.OccurredAt + LinkedActions.Window;

        return db.Events.AsNoTracking()
            .Where(e => e.SubjectPlatform == fact.SubjectPlatform
                     && e.SubjectId == fact.SubjectId
                     && types.Contains(e.Type)
                     && e.OccurredBefore == null
                     && e.OccurredAt >= from
                     && e.OccurredAt <= to)
            .ToListAsync(ct);
    }

    private static void Add(ModbotContext db, ModbotEvent follower, ModbotEvent main, DateTimeOffset now)
        => db.LinkedFacts.Add(new LinkedFact
        {
            FactId = follower.Id,
            OccurredAt = follower.OccurredAt,
            MainFactId = main.Id,
            MainOccurredAt = main.OccurredAt,
            LinkedAt = now,
        });
}
