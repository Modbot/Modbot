using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Audit;

/// <param name="Status">
/// <c>Banned</c> or <c>Unbanned</c> — and only as far as the recorded window can tell. A ban
/// issued before Modbot started syncing, then lifted afterwards, appears here as an unban with no
/// ban; a ban lifted before Modbot started appears not at all.
/// </param>
/// <param name="BannedBefore">Set when the ban's time is a window rather than an instant.</param>
public sealed record BanEntry(
    string SubjectPlatform,
    string SubjectId,
    string Status,
    DateTimeOffset? BannedAt,
    DateTimeOffset? BannedBefore,
    DateTimeOffset? UnbannedAt,
    string? ActorPlatform,
    string? ActorId,
    string? ActorName,
    string? Description,
    string? Source);

/// <summary>
/// What the ban list is derived from, and therefore what it cannot contain.
/// </summary>
/// <remarks>
/// <para>
/// This record exists because the list is <strong>incomplete in a way the reader cannot see</strong>.
/// Modbot's bans come from VRChat's group audit log, so they cover the period since this
/// deployment first synced, bounded above by however much history VRChat's own audit-log
/// retention still held at that moment. A group with three years of bans and a week-old Modbot
/// deployment has a week of them here.
/// </para>
/// <para>
/// The failure that matters is the false negative: a moderator reads the list, does not find
/// somebody, and concludes they are not banned. Nothing in a list of real rows signals that. So
/// the boundary travels with the data and the screen states it permanently.
/// </para>
/// </remarks>
/// <param name="EarliestRecord">Oldest ban or unban fact recorded, or null when there are none.</param>
/// <param name="LatestRecord">Newest.</param>
/// <param name="FirstSyncedAt">
/// When Modbot first recorded anything from VRChat's audit log. Before this point the list knows
/// only what the catch-up could still reach.
/// </param>
/// <param name="CatchUpComplete">
/// Whether the walk back through VRChat's remaining audit log has finished. While false the
/// coverage window is still growing backwards and the list is not yet at its full extent.
/// </param>
/// <param name="LastPolledAt">When the audit-log producer last completed a pass.</param>
public sealed record BanCoverage(
    DateTimeOffset? EarliestRecord,
    DateTimeOffset? LatestRecord,
    DateTimeOffset? FirstSyncedAt,
    bool CatchUpComplete,
    DateTimeOffset? LastPolledAt,
    int BannedCount,
    int UnbannedCount);

public sealed record BanListResponse(
    IReadOnlyList<BanEntry> Bans,
    int Total,
    int Offset,
    BanCoverage Coverage);

/// <summary>
/// The ban list, assembled from ban and unban facts.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is not the group's ban list.</strong> It is Modbot's record of the bans it
/// watched happen. The real list lives in VRChat and reaching it needs a sweep of
/// <c>groups.bans</c>, which is not built. Every response carries <see cref="BanCoverage"/> so
/// that distinction cannot be dropped on the way to the screen.
/// </para>
/// <para>
/// A subject's current state is the later of its newest ban and its newest unban. Derived rather
/// than stored, because the current-state tables spec 5.2 describes do not exist yet and a
/// half-maintained <c>GroupBan</c> table would be a worse answer than a query — it could go stale
/// silently, where this cannot.
/// </para>
/// </remarks>
public sealed class BanList(ModbotContext db)
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    public async Task<BanListResponse> ListAsync(
        int offset,
        int limit,
        bool includeUnbanned,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        offset = Math.Max(0, offset);

        // The newest ban and the newest unban per subject, as two aggregates rather than one
        // window function, so the whole query stays inside LINQ and inside the type index.
        var latestBans = await db.Events.AsNoTracking()
            .Where(e => e.Type == FactType.MemberBanned)
            .GroupBy(e => new { e.SubjectPlatform, e.SubjectId })
            .Select(g => new
            {
                g.Key.SubjectPlatform,
                g.Key.SubjectId,
                At = g.Max(e => e.OccurredAt),
            })
            .ToListAsync(ct);

        var latestUnbans = await db.Events.AsNoTracking()
            .Where(e => e.Type == FactType.MemberUnbanned)
            .GroupBy(e => new { e.SubjectPlatform, e.SubjectId })
            .Select(g => new
            {
                g.Key.SubjectPlatform,
                g.Key.SubjectId,
                At = g.Max(e => e.OccurredAt),
            })
            .ToListAsync(ct);

        var unbanBySubject = latestUnbans.ToDictionary(
            u => (u.SubjectPlatform, u.SubjectId),
            u => u.At);

        var subjects = latestBans
            .Select(b => (b.SubjectPlatform, b.SubjectId, BannedAt: (DateTimeOffset?)b.At))
            .ToList();

        // Somebody unbanned during the recorded window whose ban predates it. Dropping them would
        // be the more misleading choice: it is evidence the group had a ban Modbot never saw, and
        // the coverage note is the place that is explained.
        subjects.AddRange(latestUnbans
            .Where(u => !latestBans.Any(b =>
                b.SubjectPlatform == u.SubjectPlatform && b.SubjectId == u.SubjectId))
            .Select(u => (u.SubjectPlatform, u.SubjectId, BannedAt: (DateTimeOffset?)null)));

        var states = subjects
            .Select(s =>
            {
                var unbannedAt = unbanBySubject.TryGetValue((s.SubjectPlatform, s.SubjectId), out var at)
                    ? at
                    : (DateTimeOffset?)null;

                var banned = s.BannedAt is { } bannedAt
                    && (unbannedAt is null || unbannedAt < bannedAt);

                return (s.SubjectPlatform, s.SubjectId, s.BannedAt, UnbannedAt: unbannedAt, Banned: banned);
            })
            .Where(s => includeUnbanned || s.Banned)
            .OrderByDescending(s => s.BannedAt ?? s.UnbannedAt)
            .ToList();

        var total = states.Count;
        var page = states.Skip(offset).Take(limit).ToList();

        var entries = page.Count == 0 ? [] : await DetailAsync(page, ct);

        return new BanListResponse(entries, total, offset, await CoverageAsync(ct));
    }

    /// <summary>Attaches the actor and description from each subject's most recent ban fact.</summary>
    private async Task<IReadOnlyList<BanEntry>> DetailAsync(
        IReadOnlyList<(FactPlatform SubjectPlatform, string SubjectId, DateTimeOffset? BannedAt,
            DateTimeOffset? UnbannedAt, bool Banned)> page,
        CancellationToken ct)
    {
        var ids = page.Select(p => p.SubjectId).Distinct().ToList();

        var facts = await db.Events.AsNoTracking()
            .Where(e => (e.Type == FactType.MemberBanned || e.Type == FactType.MemberUnbanned)
                && ids.Contains(e.SubjectId))
            .ToListAsync(ct);

        return page
            .Select(p =>
            {
                // The fact that produced the state being shown -- the ban when they are banned,
                // the unban when they are not -- so the actor column names whoever did the thing
                // the row is reporting.
                var wanted = p.Banned ? FactType.MemberBanned : FactType.MemberUnbanned;
                var at = p.Banned ? p.BannedAt : p.UnbannedAt;

                var fact = facts
                    .Where(f => f.SubjectPlatform == p.SubjectPlatform
                        && f.SubjectId == p.SubjectId
                        && f.Type == wanted
                        && f.OccurredAt == at)
                    .OrderByDescending(f => f.Id)
                    .FirstOrDefault();

                var data = AuditJson.Parse(fact?.Data);

                return new BanEntry(
                    p.SubjectPlatform.ToString(),
                    p.SubjectId,
                    p.Banned ? "Banned" : "Unbanned",
                    p.BannedAt,
                    p.Banned ? fact?.OccurredBefore : null,
                    p.UnbannedAt,
                    fact?.ActorPlatform?.ToString(),
                    fact?.ActorId,
                    AuditJson.Text(data, "actorDisplayName"),
                    AuditJson.Text(data, "description"),
                    fact?.Source.ToString());
            })
            .ToList();
    }

    public async Task<BanCoverage> CoverageAsync(CancellationToken ct = default)
    {
        var banFacts = db.Events.AsNoTracking()
            .Where(e => e.Type == FactType.MemberBanned || e.Type == FactType.MemberUnbanned);

        var earliest = await banFacts.MinAsync(e => (DateTimeOffset?)e.OccurredAt, ct);
        var latest = await banFacts.MaxAsync(e => (DateTimeOffset?)e.OccurredAt, ct);

        var bannedCount = await db.Events.AsNoTracking()
            .CountAsync(e => e.Type == FactType.MemberBanned, ct);

        var unbannedCount = await db.Events.AsNoTracking()
            .CountAsync(e => e.Type == FactType.MemberUnbanned, ct);

        var firstSynced = await db.Events.AsNoTracking()
            .Where(e => e.Source == FactSource.AuditLog)
            .MinAsync(e => (DateTimeOffset?)e.ObservedAt, ct);

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        return new BanCoverage(
            earliest,
            latest,
            firstSynced,
            settings?.AuditLogCatchUpComplete ?? false,
            settings?.AuditLogPolledAt,
            bannedCount,
            unbannedCount);
    }
}
