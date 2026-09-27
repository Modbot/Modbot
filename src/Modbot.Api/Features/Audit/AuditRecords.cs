using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Audit;

/// <summary>
/// Finds the case file or review a page of entries leads to, so the log can link to them.
/// </summary>
/// <remarks>
/// <para>
/// A ban and its write-up are two records that point at each other: the case file names the ban
/// fact it was written for, and until now nothing pointed back. Working out what a moderator did
/// meant noticing a ban, opening Bans, and finding the person again. The same held for a flag and
/// the review somebody opened for it.
/// </para>
/// <para>
/// Two queries for the page at most, however many rows it has: one for case files, one for flags.
/// A case file fact and a review fact already carry their id in the payload, so those cost nothing.
/// The flag's review is read from the flag as it stands now, because a review is usually opened
/// after the flag fact was written, and the payload's copy is empty then.
/// </para>
/// </remarks>
public static class AuditRecords
{
    private static readonly HashSet<string> Bans = [FactType.MemberBanned, FactType.ActionBan];

    private static readonly HashSet<string> CaseFileFacts =
    [
        FactType.ReportCreated,
        FactType.ReportUpdated,
        FactType.ReportWithdrawn,
        FactType.ReportSnapshotRecaptured,
    ];

    private static readonly HashSet<string> ReviewFacts = [FactType.ReviewOpened, FactType.ReviewClosed];

    private static readonly HashSet<string> Flags =
    [
        FactType.AutoModFlag,
        FactType.AutoModFlagConfirmed,
        FactType.AutoModFlagDismissed,
    ];

    /// <summary>Fills in <see cref="AuditEntry.CaseFileId"/> and <see cref="AuditEntry.ReviewId"/>.</summary>
    /// <param name="held">
    /// The caller's permissions. Case files need <c>ViewProfile</c> and reviews <c>ReviewTickets</c>,
    /// the same as the pages the links open.
    /// </param>
    public static async Task<IReadOnlyList<AuditEntry>> AttachAsync(
        ModbotContext db,
        IReadOnlyList<AuditEntry> entries,
        ModbotPermissions held,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
            return entries;

        var admin = held.HasFlag(ModbotPermissions.Administrator);
        var caseFiles = admin || held.HasFlag(ModbotPermissions.ViewProfile);
        var reviews = admin || held.HasFlag(ModbotPermissions.ReviewTickets);

        if (!caseFiles && !reviews)
            return entries;

        var written = caseFiles ? await CaseFilesAsync(db, entries, ct) : new Dictionary<long, Guid>();
        var flagged = reviews ? await FlagReviewsAsync(db, entries, ct) : new Dictionary<Guid, Guid>();

        return entries
            .Select(e => e with
            {
                CaseFileId = caseFiles ? CaseFileOf(e, written) : null,
                ReviewId = reviews ? ReviewOf(e, flagged) : null,
            })
            .ToList();
    }

    private static Guid? CaseFileOf(AuditEntry entry, IReadOnlyDictionary<long, Guid> written)
    {
        if (CaseFileFacts.Contains(entry.Type))
            return IdIn(entry, "caseId");

        return Bans.Contains(entry.Type) && written.TryGetValue(entry.Id, out var id) ? id : null;
    }

    private static Guid? ReviewOf(AuditEntry entry, IReadOnlyDictionary<Guid, Guid> flagged)
    {
        if (ReviewFacts.Contains(entry.Type))
            return IdIn(entry, "reviewId");

        if (!Flags.Contains(entry.Type))
            return null;

        return IdIn(entry, "flagId") is { } flag && flagged.TryGetValue(flag, out var review)
            ? review
            : IdIn(entry, "reviewId");
    }

    /// <summary>The case file written for each ban fact on the page, keyed by the ban's fact id.</summary>
    /// <remarks>
    /// One ban has one case file (a second is refused), and a withdrawn one is still the record of
    /// what was written, so it is linked the same. Should two ever share a ban, the newest wins.
    /// </remarks>
    private static async Task<Dictionary<long, Guid>> CaseFilesAsync(
        ModbotContext db, IReadOnlyList<AuditEntry> entries, CancellationToken ct)
    {
        var bans = entries.Where(e => Bans.Contains(e.Type)).Select(e => (long?)e.Id).ToList();
        if (bans.Count == 0)
            return [];

        var rows = await db.CaseFiles.AsNoTracking()
            .Where(c => bans.Contains(c.BanFactId))
            .Select(c => new { BanFactId = c.BanFactId!.Value, c.Id, c.CreatedAt })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.BanFactId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.CreatedAt).First().Id);
    }

    /// <summary>The review each flag on the page has now, keyed by the flag's id.</summary>
    private static async Task<Dictionary<Guid, Guid>> FlagReviewsAsync(
        ModbotContext db, IReadOnlyList<AuditEntry> entries, CancellationToken ct)
    {
        var flags = entries
            .Where(e => Flags.Contains(e.Type))
            .Select(e => IdIn(e, "flagId"))
            .OfType<Guid>()
            .Distinct()
            .ToList();

        if (flags.Count == 0)
            return [];

        var rows = await db.ModerationFlags.AsNoTracking()
            .Where(f => flags.Contains(f.Id) && f.ReviewId != null)
            .Select(f => new { f.Id, ReviewId = f.ReviewId!.Value })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Id, r => r.ReviewId);
    }

    private static Guid? IdIn(AuditEntry entry, string key)
        => Guid.TryParse(AuditJson.Text(entry.Data, key), out var id) ? id : null;
}
