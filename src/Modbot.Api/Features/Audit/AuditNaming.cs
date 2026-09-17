using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Audit;

/// <summary>
/// Puts names to the ids on a page of facts, and finds the room each one happened in.
/// </summary>
/// <remarks>
/// <para>
/// A log that prints <c>wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b</c> is a log nobody reads. The
/// names are not in the facts — a fact records an id, and what that id is called is a separate,
/// changing thing — so they are looked up here, against the tables that hold them now.
/// </para>
/// <para>
/// <strong>Once per page, never once per row.</strong> Everything below is three queries however
/// many entries arrive: the people, the worlds, and the rooms. A lookup per row would turn a
/// fifty-row page into a hundred and fifty round trips, and the page that noticed would be the
/// one somebody left open.
/// </para>
/// <para>
/// <strong>The name shown is the name now; the name in the payload is the name then.</strong>
/// Both are kept: <c>actorName</c> still carries the display name VRChat recorded at the time, and
/// this only fills it in where the payload had none. A moderator comparing a year-old entry with
/// what they see in game needs the current name, and a moderator asking what the entry actually
/// said needs the recorded one.
/// </para>
/// </remarks>
public static class AuditNaming
{
    /// <summary>Fills in subject names, actor names, world names and room ids on a page of facts.</summary>
    public static async Task<IReadOnlyList<AuditEntry>> ResolveAsync(
        ModbotContext db,
        IReadOnlyList<AuditEntry> entries,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
            return entries;

        var people = await PeopleAsync(db, entries, ct);
        var discord = await DiscordPeopleAsync(db, entries, ct);
        var accounts = await AccountsAsync(db, entries, ct);
        var worlds = await WorldsAsync(db, entries, ct);
        var rooms = await RoomsAsync(db, entries, ct);

        return entries
            .Select(e => e with
            {
                SubjectName = NameOf(e, people.Names, discord, accounts),
                ActorName = e.ActorName ?? (e.ActorId is { } actor
                    ? (IsDiscord(e.ActorPlatform) ? discord : people.Names).GetValueOrDefault(actor)
                    : null),
                SubjectTrustRank = e.SubjectKind == SubjectKind.Person && !IsDiscord(e.SubjectPlatform)
                    ? people.Ranks.GetValueOrDefault(e.SubjectId)
                    : null,
                ActorTrustRank = e.ActorId is { } ranked && !IsDiscord(e.ActorPlatform)
                    ? people.Ranks.GetValueOrDefault(ranked)
                    : null,
                WorldName = e.WorldId is { } world ? worlds.GetValueOrDefault(world) : null,
                RoomId = RoomOf(e, rooms),
            })
            .ToList();
    }

    private static string? NameOf(
        AuditEntry entry,
        IReadOnlyDictionary<string, string> people,
        IReadOnlyDictionary<string, string> discord,
        IReadOnlyDictionary<Guid, string> accounts)
    {
        if (entry.SubjectKind == SubjectKind.Account)
            return Guid.TryParse(entry.SubjectId, out var id) ? accounts.GetValueOrDefault(id) : null;

        if (entry.SubjectKind != SubjectKind.Person)
            return null;

        return (IsDiscord(entry.SubjectPlatform) ? discord : people).GetValueOrDefault(entry.SubjectId);
    }

    private static bool IsDiscord(string? platform)
        => string.Equals(platform, nameof(FactPlatform.Discord), StringComparison.Ordinal);

    /// <summary>What the stored profiles say about the VRChat people on a page: names, and trust ranks.</summary>
    private sealed record People(
        IReadOnlyDictionary<string, string> Names,
        IReadOnlyDictionary<string, TrustRank?> Ranks);

    /// <summary>
    /// Display names and trust ranks for every VRChat person named as a subject or an actor. One
    /// query for both, because the rank rides on the same row as the name.
    /// </summary>
    private static async Task<People> PeopleAsync(
        ModbotContext db,
        IReadOnlyList<AuditEntry> entries,
        CancellationToken ct)
    {
        var ids = entries
            .Where(e => e.SubjectKind == SubjectKind.Person && !IsDiscord(e.SubjectPlatform))
            .Select(e => e.SubjectId)
            .Concat(entries.Where(e => e.ActorId is not null && !IsDiscord(e.ActorPlatform)).Select(e => e.ActorId!))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ids.Count == 0)
            return new People(Empty<string>(), Empty<TrustRank?>());

        var rows = await db.VRChatUsers.AsNoTracking()
            .Where(u => ids.Contains(u.UserId) && (u.DisplayName != null || u.TrustRank != null))
            .Select(u => new { u.UserId, u.DisplayName, u.TrustRank })
            .ToListAsync(ct);

        return new People(
            rows.Where(r => r.DisplayName != null)
                .ToDictionary(r => r.UserId, r => r.DisplayName!, StringComparer.Ordinal),
            rows.Where(r => r.TrustRank != null)
                .ToDictionary(r => r.UserId, r => r.TrustRank, StringComparer.Ordinal));
    }

    /// <summary>
    /// The name the Discord server shows for every Discord person named as a subject or an actor.
    /// </summary>
    /// <remarks>
    /// From the stored member list, which keeps people who left. Not narrowed to the server in
    /// settings: a person from a server Modbot used to watch is still better named than shown as an
    /// id, and the newest row wins where there are several.
    /// </remarks>
    private static async Task<IReadOnlyDictionary<string, string>> DiscordPeopleAsync(
        ModbotContext db,
        IReadOnlyList<AuditEntry> entries,
        CancellationToken ct)
    {
        var ids = entries
            .Where(e => e.SubjectKind == SubjectKind.Person && IsDiscord(e.SubjectPlatform))
            .Select(e => e.SubjectId)
            .Concat(entries.Where(e => e.ActorId is not null && IsDiscord(e.ActorPlatform)).Select(e => e.ActorId!))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ids.Count == 0)
            return Empty<string>();

        var rows = await db.DiscordMembers.AsNoTracking()
            .Where(m => ids.Contains(m.UserId))
            .OrderByDescending(m => m.UpdatedAt)
            .Select(m => new { m.UserId, m.DisplayName })
            .ToListAsync(ct);

        var names = Empty<string>();
        foreach (var row in rows)
            names.TryAdd(row.UserId, row.DisplayName);

        return names;
    }

    /// <summary>Usernames for the Modbot accounts Modbot's own entries are about.</summary>
    private static async Task<IReadOnlyDictionary<Guid, string>> AccountsAsync(
        ModbotContext db,
        IReadOnlyList<AuditEntry> entries,
        CancellationToken ct)
    {
        var ids = entries
            .Where(e => e.SubjectKind == SubjectKind.Account)
            .Select(e => Guid.TryParse(e.SubjectId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
            return new Dictionary<Guid, string>();

        return await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct);
    }

    private static async Task<IReadOnlyDictionary<string, string>> WorldsAsync(
        ModbotContext db,
        IReadOnlyList<AuditEntry> entries,
        CancellationToken ct)
    {
        var ids = entries
            .Where(e => e.WorldId is not null)
            .Select(e => e.WorldId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ids.Count == 0)
            return Empty<string>();

        return await db.VRChatWorlds.AsNoTracking()
            .Where(w => ids.Contains(w.WorldId) && w.Name != null)
            .Select(w => new { w.WorldId, w.Name })
            .ToDictionaryAsync(w => w.WorldId, w => w.Name!, StringComparer.Ordinal, ct);
    }

    /// <summary>
    /// Every room that has ever carried one of the world-and-number pairs on this page.
    /// </summary>
    /// <remarks>
    /// Deliberately not narrowed by time in the query. There are only ever a handful of rooms per
    /// number, and picking the right one is a comparison against each room's own open and close
    /// times, which <see cref="RoomOf"/> does in memory rather than as one query per fact.
    /// </remarks>
    private static async Task<IReadOnlyList<RoomWindow>> RoomsAsync(
        ModbotContext db,
        IReadOnlyList<AuditEntry> entries,
        CancellationToken ct)
    {
        var worldIds = entries
            .Where(e => e.WorldId is not null && e.InstanceId is not null)
            .Select(e => e.WorldId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (worldIds.Count == 0)
            return [];

        var numbers = entries
            .Where(e => e.WorldId is not null && e.InstanceId is not null)
            .Select(e => e.InstanceId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return await db.VRChatInstances.AsNoTracking()
            .Where(i => worldIds.Contains(i.WorldId)
                && i.VRChatInstanceId != null
                && numbers.Contains(i.VRChatInstanceId))
            .Select(i => new RoomWindow(
                i.Id, i.WorldId, i.VRChatInstanceId!, i.OpenedAt, i.ClosedAt, i.LastSeenAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Which room a fact belongs to: the one whose life the fact's time falls inside.
    /// </summary>
    /// <remarks>
    /// VRChat reuses a room number once the room has closed, so the number alone names several
    /// evenings. The time is what tells them apart, and a fact that falls inside none of them gets
    /// no room rather than the nearest guess — an id that opens the wrong evening is worse than no
    /// link at all.
    /// </remarks>
    private static Guid? RoomOf(AuditEntry entry, IReadOnlyList<RoomWindow> rooms)
    {
        if (entry.WorldId is null || entry.InstanceId is null || rooms.Count == 0)
            return null;

        foreach (var room in rooms)
        {
            if (!string.Equals(room.WorldId, entry.WorldId, StringComparison.Ordinal)
                || !string.Equals(room.Number, entry.InstanceId, StringComparison.Ordinal))
            {
                continue;
            }

            var endsAt = room.ClosedAt ?? room.LastSeenAt;

            if (entry.OccurredAt >= room.OpenedAt && entry.OccurredAt <= endsAt)
                return room.Id;
        }

        return null;
    }

    private static Dictionary<string, T> Empty<T>() => new(StringComparer.Ordinal);

    private sealed record RoomWindow(
        Guid Id,
        string WorldId,
        string Number,
        DateTimeOffset OpenedAt,
        DateTimeOffset? ClosedAt,
        DateTimeOffset LastSeenAt);
}
