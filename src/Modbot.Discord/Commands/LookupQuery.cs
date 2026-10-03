using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Names;
using Modbot.Discord.Gateway;
using Modbot.Discord.ModerationLog;
using Modbot.Shared.Names;

namespace Modbot.Discord.Commands;

/// <summary>Somebody the lookup found: an id, and a name when the profile has been fetched.</summary>
public sealed record PersonMatch(string UserId, string? DisplayName, bool HasProfile);

/// <summary>
/// Which parts of a person's history one caller may be shown, beyond the profile <c>/lookup</c>
/// itself needs <see cref="ModbotPermissions.ViewProfile"/> for.
/// </summary>
/// <remarks>
/// Each is the permission the web page that shows the same thing asks for, so the bot never shows
/// anybody more than the web app would.
/// </remarks>
/// <param name="Notes">Notes are facts in the moderation log, read with <see cref="ModbotPermissions.ViewAuditLog"/>.</param>
/// <param name="JoinRequests">The join request list needs <see cref="ModbotPermissions.ViewJoinRequests"/>.</param>
public readonly record struct LookupSight(bool Notes, bool JoinRequests)
{
    public static LookupSight Of(ModbotPermissions held) => new(
        DiscordCommands.Allows(held, ModbotPermissions.ViewAuditLog),
        DiscordCommands.Allows(held, ModbotPermissions.ViewJoinRequests));

    /// <summary>Everything, for a caller who holds every permission.</summary>
    public static LookupSight All => new(true, true);
}

/// <summary>A person's Discord account as <c>/lookup</c> shows it.</summary>
/// <param name="Name">The name the server shows, or null when Modbot has never seen it.</param>
/// <param name="AvatarUrl">Discord's own address for the picture.</param>
/// <param name="InServer">In the server now, as far as the member list says.</param>
/// <param name="TimedOutUntil">When a timeout still running ends; null when there is none.</param>
/// <param name="Banned">Banned from the server now, as the ban list says.</param>
/// <param name="BanReason">The reason Discord holds for that ban.</param>
/// <param name="BannedAt">When the ban was made, or first seen.</param>
public sealed record DiscordAccountSummary(
    string UserId,
    string? Name,
    string? AvatarUrl,
    bool InServer,
    DateTimeOffset? TimedOutUntil,
    bool Banned,
    string? BanReason,
    DateTimeOffset? BannedAt);

/// <summary>
/// What <c>/lookup</c> shows about one person, all from Modbot's own tables: their VRChat account,
/// their Discord account, or both when the two are linked.
/// </summary>
/// <param name="UserId">The VRChat user id, or null when only a Discord account is known.</param>
/// <param name="Discord">The Discord account, or null when none is known for them.</param>
/// <param name="Notes">Notes still standing, or null when the caller may not see notes.</param>
/// <param name="JoinRequests">Times they asked to join the group, or null when the caller may not see that.</param>
public sealed record PersonSummary(
    string? UserId,
    VRChatUser? Profile,
    int Bans,
    int Kicks,
    int Warns,
    DateTimeOffset? LastBannedAt,
    DateTimeOffset? LastUnbannedAt,
    IReadOnlyList<ModerationEventView> Recent,
    DiscordAccountSummary? Discord = null,
    int DiscordBans = 0,
    int DiscordKicks = 0,
    int Timeouts = 0,
    int Flags = 0,
    int? Notes = null,
    int? JoinRequests = null)
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
/// Ids are matched as typed and never validated (foundation §3.1.1). Ban status is read the way the
/// Bans page reads it, from the newest ban and unban facts.
/// </para>
/// <para>
/// <strong>One person, both accounts.</strong> A VRChat account and a Discord account are the same
/// person only when the member proved it by linking (<see cref="AccountLinkLookup"/>), so the
/// summary takes in the other account through the link and nothing else: never a name that looks
/// the same. Each account's facts are read under its own platform, so a Discord id and a VRChat id
/// that happened to be the same text could never be mixed up.
/// </para>
/// </remarks>
public sealed class LookupQuery
{
    public const int MaxMatches = 6;
    public const int RecentPerPerson = 5;

    private static readonly string[] KickTypes = [FactType.MemberKicked, FactType.GroupInstanceKick];

    /// <summary>
    /// The moderation history <c>/lookup</c> and <c>/recent</c> show to anybody who may use them:
    /// VRChat's moderation actions, Discord's, and the flags Modbot's own rules raised.
    /// </summary>
    /// <remarks>
    /// Discord bans, kicks and timeouts were left out before, as were flags, so somebody timed out
    /// three times on Discord looked spotless.
    /// </remarks>
    public static IReadOnlyList<string> HistoryTypes { get; } =
    [
        .. ModerationLogEvents.Allowed,
        FactType.DiscordMemberBanned,
        FactType.DiscordMemberUnbanned,
        FactType.DiscordMemberKicked,
        FactType.DiscordMemberTimedOut,
        FactType.DiscordMemberTimeoutRemoved,
        FactType.AutoModFlag,
    ];

    /// <summary>The join requests that are history of their own, shown only with the permission for them.</summary>
    /// <remarks>
    /// A rejected or blocked request was already in <see cref="ModerationLogEvents.Allowed"/>, and
    /// stays there for everybody it was shown to before.
    /// </remarks>
    private static readonly string[] RequestTypes =
    [
        FactType.JoinRequestCreated,
        FactType.ActionJoinRequestApproved,
        FactType.ActionJoinRequestRejected,
    ];

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
        var (escaped, plainEscaped) = Patterns(q);

        var exact = await ByNameAsync(escaped, plainEscaped, MaxMatches, ct).ConfigureAwait(false);
        if (exact.Count > 0)
            return exact;

        var partial = await ByNameAsync("%" + escaped + "%", plainEscaped is null ? null : "%" + plainEscaped + "%", MaxMatches, ct)
            .ConfigureAwait(false);
        if (partial.Count > 0)
            return partial;

        // An id the log knows but the profile sync has not fetched yet is still a person.
        var inFacts = await _db.Events.AsNoTracking()
            .AnyAsync(e => e.SubjectPlatform == FactPlatform.VRChat && e.SubjectId == q, ct)
            .ConfigureAwait(false);

        return inFacts ? [new PersonMatch(q, null, false)] : [];
    }

    /// <summary>
    /// People whose stored name holds what has been typed so far, for the list Discord shows under
    /// the option: the name to read, the id to fill the option with.
    /// </summary>
    /// <remarks>
    /// Stored profiles only, the same as <see cref="FindAsync"/>, and nothing for nothing typed: an
    /// empty box is not a question about anybody. Picking one fills the option with the id, which
    /// then matches exactly one person.
    /// </remarks>
    public async Task<IReadOnlyList<DiscordSuggestion>> SuggestAsync(string typed, CancellationToken ct)
    {
        var q = (typed ?? string.Empty).Trim();
        if (q.Length == 0)
            return [];

        var (escaped, plainEscaped) = Patterns(q);

        var matches = await ByNameAsync(
                "%" + escaped + "%",
                plainEscaped is null ? null : "%" + plainEscaped + "%",
                DiscordSuggestion.Most,
                ct)
            .ConfigureAwait(false);

        return matches
            .Where(m => m.UserId.Length <= DiscordSuggestion.Longest && !string.IsNullOrWhiteSpace(m.DisplayName))
            .Select(m => new DiscordSuggestion(Cut(m.DisplayName!.Trim(), DiscordSuggestion.Longest), m.UserId))
            .ToList();
    }

    /// <summary>
    /// One person, from whichever account the moderator named: the other account through the link,
    /// then everything recorded about either.
    /// </summary>
    /// <param name="vrchatUserId">The VRChat account named, or null.</param>
    /// <param name="discordUserId">The Discord account named, or null.</param>
    /// <returns>Null when Modbot has nothing at all about a Discord account and it links to nothing.</returns>
    public async Task<PersonSummary?> SummarizeAsync(
        string? vrchatUserId, string? discordUserId, LookupSight sight, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vrchatUserId) && string.IsNullOrWhiteSpace(discordUserId))
            throw new ArgumentException("Name a VRChat account or a Discord account.", nameof(vrchatUserId));

        var vrchatId = Blank(vrchatUserId);
        var discordId = Blank(discordUserId);

        vrchatId ??= await _db.LinkedVRChatUserIdAsync(discordId!, ct).ConfigureAwait(false);
        discordId ??= await _db.LinkedDiscordUserIdAsync(vrchatId!, ct).ConfigureAwait(false);

        var profile = vrchatId is null
            ? null
            : await _db.VRChatUsers.AsNoTracking()
                .FirstOrDefaultAsync(u => u.UserId == vrchatId, ct)
                .ConfigureAwait(false);

        var discord = discordId is null ? null : await DiscordAccountAsync(discordId, ct).ConfigureAwait(false);

        // A Discord account Modbot has never seen, in the member list, the ban list or the log, and
        // that links to nothing: there is nobody to show.
        if (vrchatId is null && discord is null)
            return null;

        var facts = About(vrchatId, discordId);

        // Notes taken back stop being notes (notes design §3.2); both facts stay in the log.
        long[] takenBack = sight.Notes ? [.. await TakenBackAsync(facts, ct).ConfigureAwait(false)] : [];

        var counted = new List<string>
        {
            FactType.MemberBanned, FactType.MemberKicked, FactType.GroupInstanceKick, FactType.GroupInstanceWarn,
            FactType.DiscordMemberBanned, FactType.DiscordMemberKicked, FactType.DiscordMemberTimedOut,
            FactType.AutoModFlag,
        };

        if (sight.JoinRequests)
            counted.Add(FactType.JoinRequestCreated);

        var countedTypes = counted.ToArray();

        var counts = await facts
            .Where(e => countedTypes.Contains(e.Type))
            .GroupBy(e => e.Type)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToDictionaryAsync(c => c.Type, c => c.Count, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        int? notes = null;
        if (sight.Notes)
        {
            notes = await facts
                .CountAsync(e => e.Type == FactType.NoteAdded && !takenBack.Contains(e.Id), ct)
                .ConfigureAwait(false);
        }

        DateTimeOffset? lastBan = null;
        DateTimeOffset? lastUnban = null;

        if (vrchatId is not null)
        {
            var vrchat = _db.Events.AsNoTracking()
                .Where(e => e.SubjectPlatform == FactPlatform.VRChat && e.SubjectId == vrchatId);

            lastBan = await vrchat.Where(e => e.Type == FactType.MemberBanned)
                .MaxAsync(e => (DateTimeOffset?)e.OccurredAt, ct)
                .ConfigureAwait(false);

            lastUnban = await vrchat.Where(e => e.Type == FactType.MemberUnbanned)
                .MaxAsync(e => (DateTimeOffset?)e.OccurredAt, ct)
                .ConfigureAwait(false);
        }

        var shown = ShownTypes(sight);

        var recentRows = await facts
            .Where(e => shown.Contains(e.Type) && !takenBack.Contains(e.Id))
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Take(RecentPerPerson)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var recent = await WithNamesAsync(recentRows, ct).ConfigureAwait(false);

        return new PersonSummary(
            vrchatId,
            profile,
            Count(counts, FactType.MemberBanned),
            Count(counts, FactType.MemberKicked) + Count(counts, FactType.GroupInstanceKick),
            Count(counts, FactType.GroupInstanceWarn),
            lastBan,
            lastUnban,
            recent,
            discord,
            Count(counts, FactType.DiscordMemberBanned),
            Count(counts, FactType.DiscordMemberKicked),
            Count(counts, FactType.DiscordMemberTimedOut),
            Count(counts, FactType.AutoModFlag),
            notes,
            sight.JoinRequests && vrchatId is not null ? Count(counts, FactType.JoinRequestCreated) : null);
    }

    /// <summary>The newest moderation events across everybody, for <c>/recent</c>.</summary>
    public async Task<IReadOnlyList<ModerationEventView>> RecentAsync(int count, CancellationToken ct)
    {
        var types = HistoryTypes.ToArray();

        var rows = await _db.Events.AsNoTracking()
            .Where(e => types.Contains(e.Type))
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Take(Math.Clamp(count, 1, DiscordCommands.RecentMax))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return await WithNamesAsync(rows, ct).ConfigureAwait(false);
    }

    /// <summary>Every fact about either account, each under its own platform.</summary>
    private IQueryable<ModbotEvent> About(string? vrchatId, string? discordId)
    {
        var events = _db.Events.AsNoTracking();

        if (vrchatId is not null && discordId is not null)
        {
            return events.Where(e =>
                (e.SubjectPlatform == FactPlatform.VRChat && e.SubjectId == vrchatId)
                || (e.SubjectPlatform == FactPlatform.Discord && e.SubjectId == discordId));
        }

        return vrchatId is not null
            ? events.Where(e => e.SubjectPlatform == FactPlatform.VRChat && e.SubjectId == vrchatId)
            : events.Where(e => e.SubjectPlatform == FactPlatform.Discord && e.SubjectId == discordId);
    }

    /// <summary>The ids of the notes about this person that have been taken back.</summary>
    private static async Task<HashSet<long>> TakenBackAsync(IQueryable<ModbotEvent> facts, CancellationToken ct)
    {
        var rows = await facts
            .Where(e => e.Type == FactType.NoteTakenBack)
            .Select(e => e.Data)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var ids = new HashSet<long>();

        foreach (var data in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(data);

                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("noteFactId", out var id)
                    && id.ValueKind == JsonValueKind.Number
                    && id.TryGetInt64(out var value))
                {
                    ids.Add(value);
                }
            }
            catch (JsonException)
            {
                // A take-back that cannot be read takes nothing back.
            }
        }

        return ids;
    }

    /// <summary>The kinds of fact a caller is shown in a person's recent history.</summary>
    private static string[] ShownTypes(LookupSight sight)
    {
        var types = new List<string>(HistoryTypes);

        if (sight.Notes)
            types.Add(FactType.NoteAdded);

        if (sight.JoinRequests)
            types.AddRange(RequestTypes);

        return [.. types];
    }

    private async Task<DiscordAccountSummary?> DiscordAccountAsync(string discordUserId, CancellationToken ct)
    {
        var guildId = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordGuildId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // The server Modbot watches now; a row from a server it used to watch says nothing about
        // whether they are in this one.
        var member = guildId is null
            ? null
            : await _db.DiscordMembers.AsNoTracking()
                .Where(m => m.GuildId == guildId && m.UserId == discordUserId)
                .Select(m => new { m.DisplayName, m.AvatarUrl, m.LeftAt, m.TimedOutUntil })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

        var ban = guildId is null
            ? null
            : await _db.DiscordBans.AsNoTracking()
                .Where(b => b.GuildId == guildId && b.UserId == discordUserId && b.LiftedAt == null)
                .Select(b => new { b.DisplayName, b.Username, b.AvatarUrl, b.Reason, b.BannedAt, b.FirstSeenAt })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

        var face = member is null && ban is null
            ? (await DiscordPeople.LoadAsync(_db, [discordUserId], ct).ConfigureAwait(false)).GetValueOrDefault(discordUserId)
            : null;

        var linked = await _db.LinkedVRChatUserIdAsync(discordUserId, ct).ConfigureAwait(false) is not null;

        if (member is null && ban is null && face is null && !linked)
        {
            var inFacts = await _db.Events.AsNoTracking()
                .AnyAsync(e => e.SubjectPlatform == FactPlatform.Discord && e.SubjectId == discordUserId, ct)
                .ConfigureAwait(false);

            if (!inFacts)
                return null;
        }

        return new DiscordAccountSummary(
            discordUserId,
            Blank(member?.DisplayName) ?? Blank(ban?.DisplayName) ?? Blank(ban?.Username) ?? face?.Name,
            Blank(member?.AvatarUrl) ?? Blank(ban?.AvatarUrl) ?? face?.AvatarUrl,
            member is { LeftAt: null },
            member?.TimedOutUntil,
            ban is not null,
            Blank(ban?.Reason),
            ban?.BannedAt ?? ban?.FirstSeenAt);
    }

    /// <param name="pattern">Against the name as stored.</param>
    /// <param name="plain">Against the searchable name; null when the term folds to nothing.</param>
    private async Task<IReadOnlyList<PersonMatch>> ByNameAsync(string pattern, string? plain, int most, CancellationToken ct)
    {
        var users = _db.VRChatUsers.AsNoTracking();
        users = plain is null
            ? users.Where(u => u.DisplayName != null && EF.Functions.ILike(u.DisplayName, pattern, "\\"))
            : users.Where(u =>
                (u.DisplayName != null && EF.Functions.ILike(u.DisplayName, pattern, "\\"))
                || (u.DisplayNameSearchable != null && EF.Functions.ILike(u.DisplayNameSearchable, plain, "\\")));

        return await users
            .OrderByDescending(u => u.LastSeenAt)
            .Take(most)
            .Select(u => new PersonMatch(u.UserId, u.DisplayName, true))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>A typed name as a pattern, as typed and in its searchable form.</summary>
    private static (string Escaped, string? PlainEscaped) Patterns(string q)
    {
        var plain = NameNormalizer.Searchable(q);
        return (NameSearch.Escape(q), plain.Length == 0 ? null : NameSearch.Escape(plain));
    }

    private async Task<IReadOnlyList<ModerationEventView>> WithNamesAsync(List<ModbotEvent> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
            return [];

        var names = await DisplayNames.LoadAsync(
                _db,
                rows.Where(r => r.SubjectPlatform != FactPlatform.Discord).Select(r => r.SubjectId)
                    .Concat(rows.Where(r => r.ActorPlatform != FactPlatform.Discord).Select(r => r.ActorId)),
                ct)
            .ConfigureAwait(false);

        var discord = await DiscordPeople.LoadAsync(
                _db,
                rows.Where(r => r.SubjectPlatform == FactPlatform.Discord).Select(r => r.SubjectId)
                    .Concat(rows.Where(r => r.ActorPlatform == FactPlatform.Discord).Select(r => r.ActorId)),
                ct)
            .ConfigureAwait(false);

        var discordNames = discord.ToDictionary(d => d.Key, d => d.Value.Name, StringComparer.Ordinal);
        return rows.Select(r => ModerationEventView.From(r, names, discordNames: discordNames)).ToList();
    }

    private static int Count(Dictionary<string, int> counts, string type) => counts.GetValueOrDefault(type);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Cut to a length without splitting a pair of characters that make one letter.</summary>
    private static string Cut(string text, int max)
    {
        if (text.Length <= max)
            return text;

        var end = max;
        if (char.IsHighSurrogate(text[end - 1]))
            end--;

        return text[..end];
    }
}
