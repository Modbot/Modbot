using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Audit;

/// <param name="Types">Already narrowed to what the caller may see. Never empty here.</param>
/// <param name="Before">Keyset cursor; null for the first page.</param>
/// <param name="WorldId">Only facts that happened in this world.</param>
/// <param name="InstanceId">Only facts that happened in an instance with this VRChat number.</param>
/// <param name="Precision">Only facts whose time is exact, or only those known to a window.</param>
/// <param name="HasActor">Only facts somebody did, or only facts nobody is named for.</param>
/// <param name="Text">A word or phrase to find in the payload, the subject id or the actor id.</param>
/// <param name="Account">
/// One Modbot account's whole history: facts about it and facts it did, together.
/// </param>
public sealed record AuditRequest(
    IReadOnlyList<string> Types,
    IReadOnlyList<FactSource> Sources,
    string? SubjectId,
    FactPlatform? SubjectPlatform,
    string? ActorId,
    FactPlatform? ActorPlatform,
    DateTimeOffset? From,
    DateTimeOffset? To,
    AuditCursor? Before,
    int Limit,
    string? WorldId = null,
    string? InstanceId = null,
    TimePrecision? Precision = null,
    bool? HasActor = null,
    string? Text = null,
    string? Account = null);

/// <summary>
/// Reads the merged timeline out of the fact log.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.9: there is no second audit system, so this is a query over <c>modbot_event</c> rather
/// than over a table of its own. Everything it takes has already been through
/// <see cref="AuditVisibility"/>; nothing here re-checks a permission, and nothing here may be
/// called with an unfiltered type list.
/// </para>
/// <para>
/// <strong>Paging is keyset, not offset.</strong> The log grows at the head continuously, so an
/// offset page two is a different page two every time — entries shift down between requests and
/// a reader scrolling backwards silently skips whatever arrived in between. Ordering by
/// <c>(occurred_at DESC, id DESC)</c> and carrying both halves of the last row forward is stable
/// under insertion. The id is required as well as the timestamp because VRChat's audit entries
/// share timestamps freely — a cursor on time alone drops every entry that happened in the same
/// second as the page boundary.
/// </para>
/// </remarks>
/// <param name="held">
/// The caller's permissions, for the links an entry carries to a case file or a review (see
/// <see cref="AuditRecords"/>). Which entries they may read is settled by the type list, not this.
/// </param>
public sealed class AuditQuery(ModbotContext db, ModbotPermissions held = ModbotPermissions.None)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    /// <summary>
    /// How far back the actor filter list looks, and how many names it offers.
    /// </summary>
    /// <remarks>
    /// A filter dropdown is not a member list. Aggregating every actor over all recorded history
    /// would scan every partition to populate a control, and the answer people want from it is
    /// "who has been doing things lately" anyway.
    /// </remarks>
    public static readonly TimeSpan ActorWindow = TimeSpan.FromDays(90);

    public const int MaxActors = 50;

    /// <summary>How many entries "Around this" shows either side of the one opened.</summary>
    public const int AroundEach = 5;

    /// <summary>
    /// How far either side "Around this" looks. Bounded so the read touches the partitions near
    /// the entry rather than every one the table has; five actions further apart than this are not
    /// "around" anything.
    /// </summary>
    public static readonly TimeSpan AroundWindow = TimeSpan.FromDays(90);

    public async Task<AuditPage> PageAsync(AuditRequest request, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var limit = Math.Clamp(request.Limit, 1, MaxLimit);
        var rows = await Filtered(request)
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            // One more than asked for, so "is there another page" is answered by the same query
            // rather than by a second count over a partitioned table.
            .Take(limit + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > limit;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);

        // Names for the whole page in three queries, not three per row. A timeline that prints
        // ids is a timeline nobody reads. The facts that came from the same decision as a row on
        // this page go through the same pass, so they are named too.
        var entries = await WithLinkedAsync(rows, ct);

        var next = hasMore && rows.Count > 0
            ? new AuditCursor(rows[^1].OccurredAt, rows[^1].Id)
            : null;

        return new AuditPage(entries, next, await CoverageAsync(request.Types, ct), now);
    }

    /// <summary>
    /// One entry by its id, or null when it does not exist or is not one of <paramref name="types"/>.
    /// </summary>
    /// <remarks>
    /// For a link to a single entry, such as a source chip under a Chat answer. The type list is
    /// the caller's own visible set, so an entry they may not read answers the same as one that is
    /// not there.
    /// </remarks>
    public async Task<AuditEntry?> EntryAsync(long id, IReadOnlyList<string> types, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(types);

        if (types.Count == 0)
            return null;

        var row = await db.Events.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id && types.Contains(e.Type), ct);

        if (row is null)
            return null;

        var named = await WithLinkedAsync([row], ct);
        return named.Count == 0 ? null : named[0];
    }

    /// <summary>
    /// What else happened around one entry, or null when it does not exist or is not one of
    /// <paramref name="types"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Finding 20 of the 2026-09-25 UX review: seeing the same moderator's actions around a
    /// questionable ban meant building the filters by hand, and the id was needed to do it. This is
    /// the two lists that question asks for, each with the entry in its place.
    /// </para>
    /// <para>
    /// The lists follow the timeline's rules: only types this caller may see, and a fact that is
    /// the second record of a decision is not a line of its own. Four small reads, each on the
    /// actor or subject index and bounded by <see cref="AroundWindow"/>, then one naming pass for
    /// all of them.
    /// </para>
    /// </remarks>
    public async Task<AuditAround?> AroundAsync(long id, IReadOnlyList<string> types, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(types);

        if (types.Count == 0)
            return null;

        var row = await db.Events.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id && types.Contains(e.Type), ct);

        if (row is null)
            return null;

        var near = new AuditRequest(
            types, [], null, null, null, null,
            row.OccurredAt - AroundWindow,
            row.OccurredAt + AroundWindow,
            null,
            AroundEach);

        List<ModbotEvent>? byActor = null;
        if (row.ActorId is not null && row.ActorPlatform is not null)
            byActor = await AroundOneAsync(row, near with { ActorId = row.ActorId, ActorPlatform = row.ActorPlatform }, ct);

        List<ModbotEvent>? aboutSubject = null;
        if (FactSubjects.For(row.Type) == SubjectKind.Person)
            aboutSubject = await AroundOneAsync(row, near with { SubjectId = row.SubjectId, SubjectPlatform = row.SubjectPlatform }, ct);

        var all = (byActor ?? []).Concat(aboutSubject ?? []).DistinctBy(e => e.Id).ToList();
        var named = (await WithLinkedAsync(all, ct)).ToDictionary(e => e.Id);

        return new AuditAround(
            byActor?.Select(e => named[e.Id]).ToList(),
            aboutSubject?.Select(e => named[e.Id]).ToList());
    }

    /// <summary>Up to <see cref="AroundEach"/> after the entry, the entry, and up to as many before: newest first.</summary>
    private async Task<List<ModbotEvent>> AroundOneAsync(ModbotEvent row, AuditRequest request, CancellationToken ct)
    {
        var before = await Filtered(request with { Before = new AuditCursor(row.OccurredAt, row.Id) })
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Take(AroundEach)
            .ToListAsync(ct);

        var after = await Filtered(request)
            .Where(e => e.OccurredAt > row.OccurredAt || (e.OccurredAt == row.OccurredAt && e.Id > row.Id))
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Id)
            .Take(AroundEach)
            .ToListAsync(ct);

        after.Reverse();
        return [.. after, row, .. before];
    }

    /// <summary>
    /// A page of facts with the rest of each one's decision hanging off it (spec 5.3.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two extra queries for the page, however many rows it has: the link rows, and the facts they
    /// name. The names for both the rows and their linked facts are then resolved in the one pass
    /// that was already happening, because a linked fact is shown in full and an id with no name
    /// is as unreadable inside an entry as it is on its own line.
    /// </para>
    /// <para>
    /// A linked fact is looked up whether or not this caller could have reached it by filtering —
    /// it is part of the entry they may read, and hiding half a decision from somebody who can see
    /// the other half tells them less than showing nothing would.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<AuditEntry>> WithLinkedAsync(List<ModbotEvent> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
            return [];

        var ids = rows.Select(r => r.Id).ToList();

        var links = await db.LinkedFacts.AsNoTracking()
            .Where(l => ids.Contains(l.MainFactId))
            .Select(l => new { l.FactId, l.OccurredAt, l.MainFactId })
            .ToListAsync(ct);

        if (links.Count == 0)
            return await AuditRecords.AttachAsync(db, await AuditNaming.ResolveAsync(db, rows.Select(Project).ToList(), ct), held, ct);

        var linkedIds = links.Select(l => l.FactId).ToList();
        var earliest = links.Min(l => l.OccurredAt);
        var latest = links.Max(l => l.OccurredAt);

        // Bounded by time as well as by id, so the read touches the partitions the decision fell
        // in rather than every partition the table has.
        var linkedRows = await db.Events.AsNoTracking()
            .Where(e => linkedIds.Contains(e.Id) && e.OccurredAt >= earliest && e.OccurredAt <= latest)
            .ToListAsync(ct);

        var mainOf = links.ToDictionary(l => l.FactId, l => l.MainFactId);

        var all = rows.Select(Project).Concat(linkedRows.Select(Project)).ToList();
        var named = await AuditRecords.AttachAsync(db, await AuditNaming.ResolveAsync(db, all, ct), held, ct);

        var byMain = named
            .Skip(rows.Count)
            .GroupBy(e => mainOf[e.Id])
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AuditEntry>)g.OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).ToList());

        return named
            .Take(rows.Count)
            .Select(e => byMain.TryGetValue(e.Id, out var linked) ? e with { Linked = linked } : e)
            .ToList();
    }

    /// <summary>
    /// Where this caller's timeline actually starts, and whether it is still moving.
    /// </summary>
    /// <remarks>
    /// Scoped to the types the caller may see, because an operator who can read the operational
    /// log has facts going back to first boot while a moderator's oldest visible fact is whatever
    /// the catch-up reached. One number for both would be wrong for one of them.
    /// </remarks>
    public async Task<AuditCoverage> CoverageAsync(
        IReadOnlyList<string> types,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(types);

        var settings = await db.Settings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct);

        if (types.Count == 0)
            return new AuditCoverage(null, null, settings?.AuditLogCatchUpComplete ?? false);

        var oldest = await db.Events.AsNoTracking()
            .Where(e => types.Contains(e.Type))
            .MinAsync(e => (DateTimeOffset?)e.OccurredAt, ct);

        // When Modbot first learned anything from VRChat's own audit log. Not the same as the
        // oldest fact: the catch-up records entries that happened long before this deployment
        // existed, and the gap between the two numbers is the whole of the ban list's caveat.
        var firstObserved = await db.Events.AsNoTracking()
            .Where(e => e.Source == FactSource.AuditLog)
            .MinAsync(e => (DateTimeOffset?)e.ObservedAt, ct);

        return new AuditCoverage(
            oldest,
            firstObserved,
            settings?.AuditLogCatchUpComplete ?? false);
    }

    /// <summary>Distinct actors in the recent visible history, busiest first.</summary>
    public async Task<IReadOnlyList<AuditActor>> ActorsAsync(
        IReadOnlyList<string> types,
        DateTimeOffset since,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(types);

        if (types.Count == 0)
            return [];

        var grouped = await db.Events.AsNoTracking()
            .Where(e => types.Contains(e.Type)
                && e.ActorId != null
                && e.ActorPlatform != null
                && e.OccurredAt >= since)
            .GroupBy(e => new { e.ActorPlatform, e.ActorId })
            .Select(g => new
            {
                g.Key.ActorPlatform,
                g.Key.ActorId,
                Actions = g.Count(),
                LatestId = g.Max(e => e.Id),
            })
            .OrderByDescending(g => g.Actions)
            .Take(MaxActors)
            .ToListAsync(ct);

        if (grouped.Count == 0)
            return [];

        // The display name as of that actor's most recent action. Names change; showing the one
        // recorded at the time is the same rule the timeline itself follows.
        var latestIds = grouped.Select(g => g.LatestId).ToList();

        var payloads = await db.Events.AsNoTracking()
            .Where(e => latestIds.Contains(e.Id))
            .Select(e => new { e.Id, e.Data })
            .ToListAsync(ct);

        var names = payloads.ToDictionary(
            p => p.Id,
            p => AuditJson.Text(AuditJson.Parse(p.Data), "actorDisplayName"));

        return grouped
            .Select(g => new AuditActor(
                g.ActorPlatform!.Value.ToString(),
                g.ActorId!,
                names.TryGetValue(g.LatestId, out var name) ? name : null,
                g.Actions))
            .ToList();
    }

    private IQueryable<ModbotEvent> Filtered(AuditRequest request)
    {
        var types = request.Types.ToList();
        var query = Searched(request.Text).AsNoTracking().Where(e => types.Contains(e.Type));

        // A fact that is the second record of a decision is not a line of its own: it is shown
        // inside the entry for the decision, by WithLinkedAsync (spec 5.3.2). Two rows a second
        // apart that a reader has to join in their head is exactly what this replaces.
        //
        // Only when the main fact is one this request would have shown. Filter the log down to
        // instance kicks alone and every kick appears, including the ones that came with a ban --
        // hiding a row because of something the filters just excluded would look like a bug.
        var sources = request.Sources.ToList();

        query = query.Where(e => !db.LinkedFacts.Any(l =>
            l.FactId == e.Id
            && db.Events.Any(m => m.Id == l.MainFactId
                               && m.OccurredAt == l.MainOccurredAt
                               && types.Contains(m.Type)
                               && (sources.Count == 0 || sources.Contains(m.Source)))));

        if (request.Sources.Count > 0)
            query = query.Where(e => request.Sources.Contains(e.Source));

        if (!string.IsNullOrWhiteSpace(request.WorldId))
            query = query.Where(e => e.WorldId == request.WorldId);

        if (!string.IsNullOrWhiteSpace(request.InstanceId))
            query = query.Where(e => e.InstanceId == request.InstanceId);

        if (request.Precision is { } precision)
        {
            query = precision == TimePrecision.Exact
                ? query.Where(e => e.OccurredBefore == null)
                : query.Where(e => e.OccurredBefore != null);
        }

        if (request.HasActor is { } hasActor)
            query = hasActor ? query.Where(e => e.ActorId != null) : query.Where(e => e.ActorId == null);

        // A Modbot account's history is both halves at once. The log records what was done to an
        // account against the subject -- a sign-in, a role change, being disabled -- and what the
        // account did against the actor, including every kick and ban pressed in Modbot. Either
        // half alone is half the story, and there is no second log to keep the other half in
        // (spec 5.9), so the merge happens here.
        if (!string.IsNullOrWhiteSpace(request.Account))
        {
            var account = request.Account;

            query = query.Where(e =>
                (e.SubjectPlatform == FactPlatform.Modbot && e.SubjectId == account)
                || (e.ActorPlatform == FactPlatform.Modbot && e.ActorId == account));
        }

        // Ids are matched, never parsed or normalised (spec 3.1.1). An id that does not look like
        // a VRChat id is a legacy id, not a mistake.
        if (!string.IsNullOrWhiteSpace(request.SubjectId))
            query = query.Where(e => e.SubjectId == request.SubjectId);

        if (request.SubjectPlatform is { } subjectPlatform)
            query = query.Where(e => e.SubjectPlatform == subjectPlatform);

        if (!string.IsNullOrWhiteSpace(request.ActorId))
            query = query.Where(e => e.ActorId == request.ActorId);

        if (request.ActorPlatform is { } actorPlatform)
            query = query.Where(e => e.ActorPlatform == actorPlatform);

        // Filtered on occurred_at, the lower bound, so an imprecise fact whose window straddles
        // the boundary is included rather than silently dropped out of both adjacent ranges.
        if (request.From is { } from)
            query = query.Where(e => e.OccurredAt >= from);

        if (request.To is { } to)
            query = query.Where(e => e.OccurredAt < to);

        if (request.Before is { } cursor)
        {
            query = query.Where(e =>
                e.OccurredAt < cursor.OccurredAt
                || (e.OccurredAt == cursor.OccurredAt && e.Id < cursor.Id));
        }

        return query;
    }

    /// <summary>
    /// The fact table, narrowed to rows whose payload, subject id or actor id contains the text.
    /// </summary>
    /// <remarks>
    /// The payload is <c>jsonb</c>, which PostgreSQL will not compare with <c>ILIKE</c> until it
    /// is cast to text, and the provider does not write that cast for a mapped string column. So
    /// this one clause is SQL, and everything else composes over it as usual. The text is matched
    /// literally: a typed <c>%</c> or <c>_</c> means that character (see
    /// <see cref="Members.MemberEndpoints.Pattern"/>).
    /// </remarks>
    private IQueryable<ModbotEvent> Searched(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return db.Events;

        var pattern = Members.MemberEndpoints.Pattern(text.Trim());

        return db.Events.FromSqlInterpolated(
            $"""
             SELECT * FROM modbot_event
             WHERE data::text ILIKE {pattern} ESCAPE '\'
                OR subject_id ILIKE {pattern} ESCAPE '\'
                OR actor_id ILIKE {pattern} ESCAPE '\'
             """);
    }

    internal static AuditEntry Project(ModbotEvent e)
    {
        var data = AuditJson.Parse(e.Data);

        return new AuditEntry(
            e.Id,
            e.OccurredAt,
            e.OccurredBefore,
            e.ObservedAt,
            e.OccurredBefore is null ? TimePrecision.Exact : TimePrecision.Window,
            e.Type,
            e.TypeRaw,
            AuditVisibility.CategoryOf(e.Type),
            e.Source.ToString(),
            e.SubjectPlatform.ToString(),
            e.SubjectId,
            FactSubjects.For(e.Type),
            // Names and the instance are filled in for the whole page at once by AuditNaming, which
            // is the only way they can be looked up without a query per row.
            SubjectName: null,
            e.ActorPlatform?.ToString(),
            e.ActorId,
            AuditJson.Text(data, "actorDisplayName"),
            e.WorldId,
            WorldName: null,
            e.InstanceId,
            ModbotInstanceId: null,
            InstanceName: null,
            AuditJson.Text(data, "description"),
            data);
    }
}
