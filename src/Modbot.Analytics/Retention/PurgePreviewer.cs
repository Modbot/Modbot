using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Analytics.Retention;

/// <summary>
/// The other account Modbot can honestly tie to this one, when a proved link says so.
/// </summary>
/// <param name="Platform">VRChat or Discord -- the platform this second account belongs to.</param>
/// <param name="SubjectId">The id on that platform. Opaque, exactly as it was stored.</param>
/// <param name="Name">Whatever name Modbot last saw for it, or null.</param>
public sealed record LinkedAccount(FactPlatform Platform, string SubjectId, string? Name);

/// <summary>
/// What a purge would remove, and what it would leave, counted before anybody presses anything.
/// </summary>
/// <remarks>
/// Every number here is counted from the same tables <see cref="UserPurger"/> writes to, so the
/// screen and the purge cannot disagree. Nothing is estimated: where Modbot cannot answer for a
/// platform -- a Discord account has no group ban list and no case files -- the field is null
/// rather than a zero that reads like an answer.
/// </remarks>
/// <param name="Name">The display name last seen, or null when Modbot has never fetched one.</param>
/// <param name="IsMember">In the group (or the Discord server) right now. Null when no row was ever seen.</param>
/// <param name="IsBanned">On the group's ban list right now. Null for a Discord account.</param>
/// <param name="Facts">Facts with this person as the subject.</param>
/// <param name="CountedDailyTotals">Counted-only daily total rows dimensioned on them -- the only copy of those numbers.</param>
/// <param name="Days">Days whose totals would be worked out again afterwards.</param>
/// <param name="Messages">Discord messages they wrote. Zero for a VRChat account.</param>
/// <param name="GiveawayEntries">Standing giveaway entries.</param>
/// <param name="GiveawayPlaces">Places in a past draw's entrant list that would lose their name and ids.</param>
/// <param name="ImportRecords">Rows saying a record about them came from an uploaded file.</param>
/// <param name="CaseFilesKept">Case files about them, which a purge keeps (evidence storage design §15.1).</param>
/// <param name="EvidenceFilesKept">Undestroyed evidence files hanging off those case files, which a purge keeps.</param>
/// <param name="LinkedAccount">Their other account, when a link proves one. A purge does not follow it.</param>
public sealed record PurgePreview(
    FactPlatform Platform,
    string SubjectId,
    string? Name,
    bool? IsMember,
    bool? IsBanned,
    int Facts,
    int CountedDailyTotals,
    int Days,
    int Messages,
    int GiveawayEntries,
    int GiveawayPlaces,
    int ImportRecords,
    int CaseFilesKept,
    int EvidenceFilesKept,
    LinkedAccount? LinkedAccount);

/// <summary>
/// Counts what a purge would destroy, without destroying anything.
/// </summary>
/// <remarks>
/// <para>
/// It lives beside <see cref="UserPurger"/> on purpose. The two have to stay in step -- a purge
/// that removes a table the count never mentioned is a screen that lied -- and a count sitting in
/// the API slice would drift from a purge sitting here the first time either grew a table.
/// </para>
/// <para>
/// Nothing is cached and nothing is held between the count and the purge. The numbers are a
/// moment old by the time anybody reads them, which is fine: they exist so a person can see the
/// size of what they are about to do, not so the purge can check them.
/// </para>
/// </remarks>
public sealed class PurgePreviewer
{
    private readonly ModbotContext _db;
    private readonly DailyTotalsJob _dailyTotals;

    public PurgePreviewer(ModbotContext db, DailyTotalsJob dailyTotals)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dailyTotals);

        _db = db;
        _dailyTotals = dailyTotals;
    }

    public async Task<PurgePreview> PreviewAsync(
        FactPlatform platform,
        string subjectId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectId);

        var facts = await _db.Events.AsNoTracking()
            .CountAsync(e => e.SubjectPlatform == platform && e.SubjectId == subjectId, ct);

        var dimensions = new[] { DailyTotalDimensions.ForUser(platform, subjectId), subjectId };

        var countedDailyTotals = await _db.DailyTotals.AsNoTracking()
            .CountAsync(
                d => d.Origin == DailyTotalOrigin.Counted && dimensions.Contains(d.Dimension), ct);

        // Counted the way the purge counts them: one list, duplicates removed. A day a person
        // both spoke on and was seen in an instance on is one day, not two.
        var days = await _dailyTotals.DaysTouchedBySubjectAsync(platform, subjectId, ct);
        var messages = 0;

        if (platform == FactPlatform.Discord)
        {
            messages = await _db.DiscordMessages.AsNoTracking()
                .CountAsync(m => m.AuthorId == subjectId, ct);

            days = days
                .Concat(await _dailyTotals.MessageDaysByAuthorAsync(subjectId, ct))
                .Distinct()
                .ToList();
        }

        var giveawayEntries = platform == FactPlatform.Discord
            ? await _db.GiveawayEntries.AsNoTracking().CountAsync(e => e.DiscordUserId == subjectId, ct)
            : 0;

        var giveawayPlaces = await _db.GiveawayEntrants.AsNoTracking()
            .CountAsync(
                e => !e.Purged && (e.VRChatUserId == subjectId || e.DiscordUserId == subjectId), ct);

        var importRecords = await _db.ImportRecords.AsNoTracking()
            .CountAsync(r => r.SubjectPlatform == platform && r.SubjectId == subjectId, ct);

        var (caseFiles, evidenceFiles) = await KeptAsync(platform, subjectId, ct);

        return new PurgePreview(
            platform,
            subjectId,
            await NameAsync(platform, subjectId, ct),
            await IsMemberAsync(platform, subjectId, ct),
            platform == FactPlatform.VRChat
                ? await _db.GroupBans.AsNoTracking()
                    .AnyAsync(b => b.UserId == subjectId && b.LiftedAt == null, ct)
                : null,
            facts,
            countedDailyTotals,
            days.Count,
            messages,
            giveawayEntries,
            giveawayPlaces,
            importRecords,
            caseFiles,
            evidenceFiles,
            await LinkedAsync(platform, subjectId, ct));
    }

    /// <summary>
    /// The case files about this person and the evidence still hanging off them.
    /// </summary>
    /// <remarks>
    /// Case files are keyed by VRChat user id, so a Discord account has none -- that is a real
    /// zero rather than a gap, because nothing about a Discord id could be a case file.
    /// </remarks>
    private async Task<(int CaseFiles, int EvidenceFiles)> KeptAsync(
        FactPlatform platform, string subjectId, CancellationToken ct)
    {
        if (platform != FactPlatform.VRChat)
            return (0, 0);

        var ids = await _db.CaseFiles.AsNoTracking()
            .Where(c => c.UserId == subjectId)
            .Select(c => c.Id.ToString())
            .ToListAsync(ct);

        if (ids.Count == 0)
            return (0, 0);

        var files = await _db.EvidenceBlobs.AsNoTracking()
            .CountAsync(b => b.ReportId != null && ids.Contains(b.ReportId) && b.DestroyedAt == null, ct);

        return (ids.Count, files);
    }

    private async Task<string?> NameAsync(FactPlatform platform, string subjectId, CancellationToken ct)
        => platform == FactPlatform.Discord
            ? await _db.DiscordMembers.AsNoTracking()
                .Where(m => m.UserId == subjectId)
                .Select(m => m.DisplayName)
                .FirstOrDefaultAsync(ct)
            : await _db.VRChatUsers.AsNoTracking()
                .Where(u => u.UserId == subjectId)
                .Select(u => u.DisplayName)
                .FirstOrDefaultAsync(ct);

    /// <summary>Null means no membership row was ever seen, which is not the same as "no longer in".</summary>
    private async Task<bool?> IsMemberAsync(FactPlatform platform, string subjectId, CancellationToken ct)
        => platform == FactPlatform.Discord
            ? await _db.DiscordMembers.AsNoTracking()
                .Where(m => m.UserId == subjectId)
                .Select(m => (bool?)(m.LeftAt == null))
                .FirstOrDefaultAsync(ct)
            : await _db.GroupMembers.AsNoTracking()
                .Where(m => m.UserId == subjectId)
                .Select(m => (bool?)(m.LeftAt == null))
                .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The other account a live link proves, so the operator can see there is a second one.
    /// </summary>
    /// <remarks>
    /// Named, never followed. A purge erases the account it was given; taking the link as licence
    /// to erase a second account the operator never typed would be the one place in Modbot where
    /// a destructive action widened itself.
    /// </remarks>
    private async Task<LinkedAccount?> LinkedAsync(
        FactPlatform platform, string subjectId, CancellationToken ct)
    {
        var link = platform == FactPlatform.Discord
            ? await _db.DiscordAccountLinks.AsNoTracking()
                .FirstOrDefaultAsync(l => l.DiscordUserId == subjectId && l.UnlinkedAt == null, ct)
            : await _db.DiscordAccountLinks.AsNoTracking()
                .FirstOrDefaultAsync(l => l.VRChatUserId == subjectId && l.UnlinkedAt == null, ct);

        if (link is null)
            return null;

        return platform == FactPlatform.Discord
            ? new LinkedAccount(FactPlatform.VRChat, link.VRChatUserId, link.VRChatDisplayName)
            : new LinkedAccount(FactPlatform.Discord, link.DiscordUserId, link.DiscordUsername);
    }
}
