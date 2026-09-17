using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Names;
using Modbot.Discord.ModerationLog;
using Modbot.Shared.Names;

namespace Modbot.Discord.Commands;

/// <summary>Somebody the lookup found: an id, and a name when the profile has been fetched.</summary>
public sealed record PersonMatch(string UserId, string? DisplayName, bool HasProfile);

/// <summary>
/// What <c>/lookup</c> shows about one person, all from Modbot's own tables.
/// </summary>
public sealed record PersonSummary(
    string UserId,
    VRChatUser? Profile,
    int Bans,
    int Kicks,
    int Warns,
    DateTimeOffset? LastBannedAt,
    DateTimeOffset? LastUnbannedAt,
    IReadOnlyList<ModerationEventView> Recent)
{
    /// <summary>Same reading as the Bans page: the newest ban stands unless a newer unban follows it.</summary>
    public bool IsBanned => LastBannedAt is { } banned && (LastUnbannedAt is null || LastUnbannedAt < banned);
}

/// <summary>
/// The database side of the lookup commands.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Never the VRChat search endpoint.</strong> Foundation §4.2.5.1 keeps that for a person
/// typing in the web app, and the bot does not need it: a moderator asking Discord about
/// somebody is asking what Modbot already knows. Names are matched against <c>vrchat_user</c>
/// only, exact first and then contains, and an id Modbot has never seen is answered with "not in
/// the records" rather than a fetch.
/// </para>
/// <para>
/// Ids are matched as typed and never validated (foundation §3.1.1). There is no member or ban
/// table in this tree; ban status is read the way the Bans page reads it, from the newest ban
/// and unban facts.
/// </para>
/// </remarks>
public sealed class LookupQuery
{
    public const int MaxMatches = 6;
    public const int RecentPerPerson = 5;

    private static readonly string[] ModerationTypes = ModerationLogEvents.Allowed.ToArray();
    private static readonly string[] KickTypes = [FactType.MemberKicked, FactType.GroupInstanceKick];

    private readonly ModbotContext _db;

    public LookupQuery(ModbotContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public async Task<IReadOnlyList<PersonMatch>> FindAsync(string query, CancellationToken ct)
    {
        var q = (query ?? string.Empty).Trim();
        if (q.Length == 0)
            return [];

        var byId = await _db.VRChatUsers.AsNoTracking()
            .Where(u => u.UserId == q)
            .Select(u => new PersonMatch(u.UserId, u.DisplayName, true))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (byId is not null)
            return [byId];

        // The whole name first, then part of one; each as typed and in its searchable form, so
        // "adderall" finds Addеrаll and "alex" finds 𝕬𝖑𝖊𝖝.
        var escaped = NameSearch.Escape(q);
        var plain = NameNormalizer.Searchable(q);
        var plainEscaped = plain.Length == 0 ? null : NameSearch.Escape(plain);

        var exact = await ByNameAsync(escaped, plainEscaped, ct).ConfigureAwait(false);
        if (exact.Count > 0)
            return exact;

        var partial = await ByNameAsync("%" + escaped + "%", plainEscaped is null ? null : "%" + plainEscaped + "%", ct)
            .ConfigureAwait(false);
        if (partial.Count > 0)
            return partial;

        // An id the log knows but the profile sync has not fetched yet is still a person.
        var inFacts = await _db.Events.AsNoTracking()
            .AnyAsync(e => e.SubjectPlatform == FactPlatform.VRChat && e.SubjectId == q, ct)
            .ConfigureAwait(false);

        return inFacts ? [new PersonMatch(q, null, false)] : [];
    }

    public async Task<PersonSummary> SummarizeAsync(string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var profile = await _db.VRChatUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserId == userId, ct)
            .ConfigureAwait(false);

        var subject = _db.Events.AsNoTracking()
            .Where(e => e.SubjectPlatform == FactPlatform.VRChat && e.SubjectId == userId);

        var bans = await subject.CountAsync(e => e.Type == FactType.MemberBanned, ct).ConfigureAwait(false);
        var kicks = await subject.CountAsync(e => KickTypes.Contains(e.Type), ct).ConfigureAwait(false);
        var warns = await subject.CountAsync(e => e.Type == FactType.GroupInstanceWarn, ct).ConfigureAwait(false);

        var lastBan = await subject.Where(e => e.Type == FactType.MemberBanned)
            .MaxAsync(e => (DateTimeOffset?)e.OccurredAt, ct)
            .ConfigureAwait(false);

        var lastUnban = await subject.Where(e => e.Type == FactType.MemberUnbanned)
            .MaxAsync(e => (DateTimeOffset?)e.OccurredAt, ct)
            .ConfigureAwait(false);

        var recentRows = await subject
            .Where(e => ModerationTypes.Contains(e.Type))
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Take(RecentPerPerson)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var recent = await WithNamesAsync(recentRows, ct).ConfigureAwait(false);

        return new PersonSummary(userId, profile, bans, kicks, warns, lastBan, lastUnban, recent);
    }

    public async Task<IReadOnlyList<ModerationEventView>> RecentAsync(int count, CancellationToken ct)
    {
        var rows = await _db.Events.AsNoTracking()
            .Where(e => ModerationTypes.Contains(e.Type))
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Take(Math.Clamp(count, 1, DiscordCommands.RecentMax))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return await WithNamesAsync(rows, ct).ConfigureAwait(false);
    }

    /// <param name="pattern">Against the name as stored.</param>
    /// <param name="plain">Against the searchable name; null when the term folds to nothing.</param>
    private async Task<IReadOnlyList<PersonMatch>> ByNameAsync(string pattern, string? plain, CancellationToken ct)
    {
        var users = _db.VRChatUsers.AsNoTracking();
        users = plain is null
            ? users.Where(u => u.DisplayName != null && EF.Functions.ILike(u.DisplayName, pattern, "\\"))
            : users.Where(u =>
                (u.DisplayName != null && EF.Functions.ILike(u.DisplayName, pattern, "\\"))
                || (u.DisplayNameSearchable != null && EF.Functions.ILike(u.DisplayNameSearchable, plain, "\\")));

        return await users
            .OrderByDescending(u => u.LastSeenAt)
            .Take(MaxMatches)
            .Select(u => new PersonMatch(u.UserId, u.DisplayName, true))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ModerationEventView>> WithNamesAsync(List<ModbotEvent> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
            return [];

        var names = await DisplayNames.LoadAsync(
                _db, rows.Select(r => r.SubjectId).Concat(rows.Select(r => r.ActorId)), ct)
            .ConfigureAwait(false);

        return rows.Select(r => ModerationEventView.From(r, names)).ToList();
    }
}
