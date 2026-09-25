using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Audit;

/// <summary>
/// Puts names to the ids on a page of facts, and finds the instance each one happened in.
/// </summary>
/// <remarks>
/// <para>
/// A log that prints <c>wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b</c> is a log nobody reads. The
/// names are not in the facts — a fact records an id, and what that id is called is a separate,
/// changing thing — so they are looked up here, against the tables that hold them now.
/// </para>
/// <para>
/// <strong>Once per page, never once per row.</strong> Everything below is three queries however
/// many entries arrive: the people, the worlds, and the instances. A lookup per row would turn a
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
    /// <summary>Fills in subject names, actor names, world names and instance ids on a page of facts.</summary>
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
        var instances = await InstancesAsync(db, entries, ct);
        var reporters = await ReportersAsync(db, entries, ct);

        return entries
            .Select(e => e with
            {
                ReportedBy = reporters.GetValueOrDefault(e.Id),
                SubjectName = NameOf(e, people.Names, discord, accounts),
                ActorName = e.ActorName ?? ActorNameOf(e, people.Names, discord, accounts),
                SubjectTrustRank = e.SubjectKind == SubjectKind.Person && !IsDiscord(e.SubjectPlatform)
                    ? people.Ranks.GetValueOrDefault(e.SubjectId)
                    : null,
                ActorTrustRank = e.ActorId is { } ranked && !IsDiscord(e.ActorPlatform)
                    ? people.Ranks.GetValueOrDefault(ranked)
                    : null,
                WorldName = e.WorldId is { } world ? worlds.GetValueOrDefault(world) : null,
                ModbotInstanceId = InstanceOf(e, instances),
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

    /// <summary>
    /// The actor's name now, for a fact whose payload kept none. A Modbot account is named from the
    /// accounts, not from VRChat's people: its id is an account id, and VRChat has never heard of it.
    /// </summary>
    private static string? ActorNameOf(
        AuditEntry entry,
        IReadOnlyDictionary<string, string> people,
        IReadOnlyDictionary<string, string> discord,
        IReadOnlyDictionary<Guid, string> accounts)
    {
        if (entry.ActorId is not { } actor)
            return null;

        if (IsModbot(entry.ActorPlatform))
            return Guid.TryParse(actor, out var id) ? accounts.GetValueOrDefault(id) : null;

        return (IsDiscord(entry.ActorPlatform) ? discord : people).GetValueOrDefault(actor);
    }

    private static bool IsDiscord(string? platform)
        => string.Equals(platform, nameof(FactPlatform.Discord), StringComparison.Ordinal);

    private static bool IsModbot(string? platform)
        => string.Equals(platform, nameof(FactPlatform.Modbot), StringComparison.Ordinal);

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

    /// <summary>Usernames for the Modbot accounts on a page: the ones entries are about, and the ones that acted without their name kept.</summary>
    private static async Task<IReadOnlyDictionary<Guid, string>> AccountsAsync(
        ModbotContext db,
        IReadOnlyList<AuditEntry> entries,
        CancellationToken ct)
    {
        var ids = entries
            .Where(e => e.SubjectKind == SubjectKind.Account)
            .Select(e => e.SubjectId)
            .Concat(entries.Where(e => e.ActorId is not null && e.ActorName is null && IsModbot(e.ActorPlatform)).Select(e => e.ActorId!))
            .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
            return new Dictionary<Guid, string>();

        return await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct);
    }

    /// <summary>
    /// Whose clients reported each client-reported fact on the page, oldest report first.
    /// </summary>
    /// <remarks>
    /// <para>The fact names the client whose report became it; <c>modbot_event_report</c> names the
    /// clients that reported the same thing afterwards and were deduplicated into it. Both are
    /// device ids, and a device id means nothing to a moderator, so both are turned into the
    /// username of the account the device was issued to.</para>
    /// <para>Two queries for the page however many entries it holds, and none at all for a page
    /// with no client-reported fact on it -- which is most pages outside an instance's own log.</para>
    /// <para>A device Modbot can no longer put an account to is left out rather than shown as an
    /// id. The id is still in the entry's payload for anybody who needs it.</para>
    /// </remarks>
    private static async Task<IReadOnlyDictionary<long, IReadOnlyList<AuditReporter>>> ReportersAsync(
        ModbotContext db,
        IReadOnlyList<AuditEntry> entries,
        CancellationToken ct)
    {
        var wrote = entries
            .Select(e => (e.Id, e.ObservedAt, Device: DeviceOn(e)))
            .Where(e => e.Device is not null)
            .ToList();

        if (wrote.Count == 0)
            return new Dictionary<long, IReadOnlyList<AuditReporter>>();

        var factIds = wrote.Select(e => e.Id).ToList();

        var also = await db.EventReports.AsNoTracking()
            .Where(r => factIds.Contains(r.FactId))
            .Select(r => new { r.FactId, r.DeviceId, r.ReportedAt })
            .ToListAsync(ct);

        var devices = wrote.Select(e => e.Device!.Value)
            .Concat(also.Select(r => r.DeviceId))
            .Distinct()
            .ToList();

        var owners = await (
                from device in db.CompanionDevices.AsNoTracking()
                join user in db.Users.AsNoTracking() on device.IssuedToUserId equals user.Id
                where devices.Contains(device.Id)
                select new { Device = device.Id, Account = user.Id, user.Username })
            .ToDictionaryAsync(o => o.Device, o => new { o.Account, o.Username }, ct);

        var byFact = also
            .GroupBy(r => r.FactId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.ReportedAt).ToList());

        var resolved = new Dictionary<long, IReadOnlyList<AuditReporter>>();

        foreach (var (id, observedAt, device) in wrote)
        {
            var reporters = new List<AuditReporter>();

            if (owners.TryGetValue(device!.Value, out var first))
                reporters.Add(new AuditReporter(first.Account, first.Username, observedAt));

            foreach (var extra in byFact.GetValueOrDefault(id) ?? [])
            {
                if (owners.TryGetValue(extra.DeviceId, out var owner))
                    reporters.Add(new AuditReporter(owner.Account, owner.Username, extra.ReportedAt));
            }

            if (reporters.Count > 0)
                resolved[id] = reporters;
        }

        return resolved;
    }

    /// <summary>The client named on an entry's payload, or null for a fact no client reported.</summary>
    private static Guid? DeviceOn(AuditEntry entry)
        => Guid.TryParse(AuditJson.Text(entry.Data, ClientReport.DeviceIdKey), out var device)
            ? device
            : null;

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
    /// Every instance that has ever carried one of the world-and-number pairs on this page.
    /// </summary>
    /// <remarks>
    /// Deliberately not narrowed by time in the query. There are only ever a handful of instances per
    /// number, and picking the right one is a comparison against each instance's own open and close
    /// times, which <see cref="InstanceOf"/> does in memory rather than as one query per fact.
    /// </remarks>
    private static async Task<IReadOnlyList<InstanceWindow>> InstancesAsync(
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
            .Select(i => new InstanceWindow(
                i.Id, i.WorldId, i.VRChatInstanceId!, i.OpenedAt, i.ClosedAt, i.LastSeenAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Which instance a fact belongs to: the one whose life the fact's time falls inside.
    /// </summary>
    /// <remarks>
    /// VRChat reuses an instance number once the instance has closed, so the number alone names several
    /// evenings. The time is what tells them apart, and a fact that falls inside none of them gets
    /// no instance rather than the nearest guess — an id that opens the wrong evening is worse than no
    /// link at all.
    /// </remarks>
    private static Guid? InstanceOf(AuditEntry entry, IReadOnlyList<InstanceWindow> instances)
    {
        if (entry.WorldId is null || entry.InstanceId is null || instances.Count == 0)
            return null;

        foreach (var instance in instances)
        {
            if (!string.Equals(instance.WorldId, entry.WorldId, StringComparison.Ordinal)
                || !string.Equals(instance.Number, entry.InstanceId, StringComparison.Ordinal))
            {
                continue;
            }

            var endsAt = instance.ClosedAt ?? instance.LastSeenAt;

            if (entry.OccurredAt >= instance.OpenedAt && entry.OccurredAt <= endsAt)
                return instance.Id;
        }

        return null;
    }

    private static Dictionary<string, T> Empty<T>() => new(StringComparer.Ordinal);

    private sealed record InstanceWindow(
        Guid Id,
        string WorldId,
        string Number,
        DateTimeOffset OpenedAt,
        DateTimeOffset? ClosedAt,
        DateTimeOffset LastSeenAt);
}
