using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Audit;

/// <param name="Types">Already narrowed to what the caller may see. Never empty here.</param>
/// <param name="Before">Keyset cursor; null for the first page.</param>
public sealed record AuditRequest(
    IReadOnlyList<FactType> Types,
    IReadOnlyList<FactSource> Sources,
    string? SubjectId,
    FactPlatform? SubjectPlatform,
    string? ActorId,
    FactPlatform? ActorPlatform,
    DateTimeOffset? From,
    DateTimeOffset? To,
    AuditCursor? Before,
    int Limit);

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
public sealed class AuditQuery(ModbotContext db)
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

    public async Task<AuditPage> PageAsync(AuditRequest request, CancellationToken ct = default)
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

        var entries = rows.Select(Project).ToList();

        var next = hasMore && rows.Count > 0
            ? new AuditCursor(rows[^1].OccurredAt, rows[^1].Id)
            : null;

        return new AuditPage(entries, next, await CoverageAsync(request.Types, ct));
    }

    /// <summary>
    /// Where this caller's timeline actually starts, and whether it is still moving.
    /// </summary>
    /// <remarks>
    /// Scoped to the types the caller may see, because an operator who can read the operational
    /// log has facts going back to first boot while a moderator's oldest visible fact is whatever
    /// the backfill reached. One number for both would be wrong for one of them.
    /// </remarks>
    public async Task<AuditCoverage> CoverageAsync(
        IReadOnlyList<FactType> types,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(types);

        var settings = await db.Settings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct);

        if (types.Count == 0)
            return new AuditCoverage(null, null, settings?.AuditLogBackfillComplete ?? false);

        var oldest = await db.Events.AsNoTracking()
            .Where(e => types.Contains(e.Type))
            .MinAsync(e => (DateTimeOffset?)e.OccurredAt, ct);

        // When Modbot first learned anything from VRChat's own audit log. Not the same as the
        // oldest fact: the backfill records entries that happened long before this deployment
        // existed, and the gap between the two numbers is the whole of the ban list's caveat.
        var firstObserved = await db.Events.AsNoTracking()
            .Where(e => e.Source == FactSource.AuditLog)
            .MinAsync(e => (DateTimeOffset?)e.ObservedAt, ct);

        return new AuditCoverage(
            oldest,
            firstObserved,
            settings?.AuditLogBackfillComplete ?? false);
    }

    /// <summary>Distinct actors in the recent visible history, busiest first.</summary>
    public async Task<IReadOnlyList<AuditActor>> ActorsAsync(
        IReadOnlyList<FactType> types,
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
        var query = db.Events.AsNoTracking().Where(e => request.Types.Contains(e.Type));

        if (request.Sources.Count > 0)
            query = query.Where(e => request.Sources.Contains(e.Source));

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

    internal static AuditEntry Project(ModbotEvent e)
    {
        var data = AuditJson.Parse(e.Data);

        return new AuditEntry(
            e.Id,
            e.OccurredAt,
            e.OccurredBefore,
            e.ObservedAt,
            e.OccurredBefore is null ? TimePrecision.Exact : TimePrecision.Window,
            e.Type.ToString(),
            AuditVisibility.CategoryOf(e.Type),
            e.Source.ToString(),
            e.SubjectPlatform.ToString(),
            e.SubjectId,
            e.ActorPlatform?.ToString(),
            e.ActorId,
            AuditJson.Text(data, "actorDisplayName"),
            e.WorldId,
            e.InstanceId,
            AuditJson.Text(data, "description"),
            data);
    }
}
