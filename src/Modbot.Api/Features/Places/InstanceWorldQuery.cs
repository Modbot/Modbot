using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Places;

/// <summary>
/// How one instance compared with the other instances in its world while it was open.
/// </summary>
/// <remarks>
/// <para>
/// Read from <c>world_head_count</c>, which <c>WorldHeadCountSync</c> fills every two minutes while
/// the group has an instance open in a world. Each row keeps the world page's <c>instances</c> list
/// exactly as VRChat sent it (<see cref="WorldHeadCount.Instances"/>), and it is taken apart here,
/// when a popup asks, so that what is kept never depended on a guess about what the list holds.
/// </para>
/// <para>
/// <strong>Finding this instance in the list.</strong> By VRChat's number, the part of each entry
/// before the first <c>~</c>. VRChat reissues numbers, but never to two instances of one world at
/// the same moment, and every read here is from inside this instance's own open stretch.
/// </para>
/// <para>
/// See spec 2026-09-27-instance-against-its-world-design.md.
/// </para>
/// </remarks>
public static class InstanceWorldQuery
{
    /// <param name="now">From <c>IModbotClock</c>. An open instance is compared up to it.</param>
    /// <returns>Null when there is no such instance.</returns>
    public static async Task<InstanceWorldView?> ReadAsync(
        ModbotContext db,
        Guid id,
        string? managedGroupId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var row = await db.VRChatInstances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (row is null)
            return null;

        var from = row.OpenedAt;
        var to = row.ClosedAt ?? now;
        if (to < from)
            to = from;

        var stored = await db.WorldHeadCounts.AsNoTracking()
            .Where(w => w.WorldId == row.WorldId && w.CountedAt >= from && w.CountedAt <= to)
            .OrderByDescending(w => w.CountedAt)
            .ThenByDescending(w => w.Id)
            .Take(InstanceWorldView.ReadingsMost + 1)
            .ToListAsync(ct);

        var truncated = stored.Count > InstanceWorldView.ReadingsMost;
        if (truncated)
            stored.RemoveAt(stored.Count - 1);

        stored.Reverse();

        if (stored.Count == 0)
            return new InstanceWorldView(id, row.WorldId, from, to, [], [], 0, null, 0m, false);

        // This instance's own head counts, for the reads whose list leaves it out. The last change
        // before the first read is what held at that read.
        var firstRead = stored[0].CountedAt;
        var own = await OwnCountsAsync(db, id, firstRead, to, ct);

        var readings = new List<WorldReading>(stored.Count);
        var others = new Dictionary<string, OtherSeen>(StringComparer.Ordinal);
        var ownIndex = -1;

        foreach (var read in stored)
        {
            var entries = Entries(read.Instances);

            int? people = null;
            var unsure = false;
            var listed = false;
            var ranked = new List<int>(entries.Count);

            foreach (var (instanceId, count) in entries)
            {
                var number = NumberOf(instanceId);

                if (!listed && row.VRChatInstanceId is { } mine && string.Equals(number, mine, StringComparison.Ordinal))
                {
                    people = count;
                    listed = true;
                    continue;
                }

                ranked.Add(count);

                if (!others.TryGetValue(instanceId, out var seen))
                    others[instanceId] = seen = new OtherSeen(instanceId, number, read.CountedAt);

                seen.Add(read.CountedAt, count);
            }

            if (!listed)
            {
                while (ownIndex + 1 < own.Count && own[ownIndex + 1].At <= read.CountedAt)
                    ownIndex++;

                if (ownIndex >= 0)
                    (people, unsure) = (own[ownIndex].People, own[ownIndex].Unsure);
            }

            int? rank = people is { } mineNow ? 1 + ranked.Count(c => c > mineNow) : null;

            readings.Add(new WorldReading(
                read.CountedAt,
                read.Occupants,
                read.PublicOccupants,
                read.PrivateOccupants,
                people,
                unsure,
                listed,
                rank,
                ranked.Count + (people is null ? 0 : 1)));
        }

        var shown = others.Values
            .OrderByDescending(o => o.Peak)
            .ThenBy(o => o.FirstSeenAt)
            .ThenBy(o => o.InstanceId, StringComparer.Ordinal)
            .Take(InstanceWorldView.OthersShown)
            .ToList();

        var known = await KnownAsync(db, row.WorldId, shown, ct);

        var listedOthers = shown
            .Select(o =>
            {
                var parts = InstanceLocationParts.Split($"{row.WorldId}:{o.InstanceId}");
                var match = known.TryGetValue(o.InstanceId, out var m) ? m : ((Guid Id, string? Name)?)null;

                return new OtherInstance(
                    o.InstanceId,
                    o.Number,
                    parts.GroupId,
                    parts.GroupAccessType,
                    parts.Region,
                    managedGroupId is { Length: > 0 } && string.Equals(parts.GroupId, managedGroupId, StringComparison.Ordinal),
                    match?.Id,
                    match?.Name,
                    o.Peak,
                    o.FirstSeenAt,
                    o.LastSeenAt,
                    o.Readings);
            })
            .ToList();

        var atPeak = readings
            .Where(r => r.People is not null)
            .OrderByDescending(r => r.People)
            .ThenBy(r => r.At)
            .FirstOrDefault();

        return new InstanceWorldView(
            id,
            row.WorldId,
            from,
            to,
            readings,
            listedOthers,
            others.Count,
            atPeak,
            BusiestMinutes(readings, to),
            truncated);
    }

    /// <summary>
    /// How long the instance was the busiest in its world: each read where it ranked first, with
    /// others to rank against, counts until the next read, and never for longer than
    /// <see cref="InstanceWorldView.ReadingCoversAtMost"/>.
    /// </summary>
    public static decimal BusiestMinutes(IReadOnlyList<WorldReading> readings, DateTimeOffset to)
    {
        var total = TimeSpan.Zero;

        for (var i = 0; i < readings.Count; i++)
        {
            if (readings[i] is not { Rank: 1, Of: > 1 })
                continue;

            var until = i + 1 < readings.Count ? readings[i + 1].At : to;
            var span = until - readings[i].At;

            if (span > InstanceWorldView.ReadingCoversAtMost)
                span = InstanceWorldView.ReadingCoversAtMost;

            if (span > TimeSpan.Zero)
                total += span;
        }

        return Math.Round((decimal)total.TotalMinutes, 1);
    }

    /// <summary>
    /// The entries of a world page's <c>instances</c> list: an instance id and a head count each.
    /// </summary>
    /// <remarks>
    /// Never throws. VRChat sends each entry as a two-item array, <c>["16354~region(eu)", 12]</c>; an
    /// entry of any other shape is skipped rather than guessed at, and so is a second entry for an id
    /// already seen in the same read.
    /// </remarks>
    public static IReadOnlyList<(string InstanceId, int People)> Entries(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var entries = new List<(string, int)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 2)
                    continue;

                var idPart = entry[0];
                var countPart = entry[1];

                if (idPart.ValueKind != JsonValueKind.String
                    || idPart.GetString() is not { Length: > 0 } instanceId
                    || countPart.ValueKind != JsonValueKind.Number
                    || !countPart.TryGetInt32(out var people)
                    || people < 0)
                {
                    continue;
                }

                if (seen.Add(instanceId))
                    entries.Add((instanceId, people));
            }

            return entries;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>VRChat's number out of a list entry's id: everything before the first <c>~</c>.</summary>
    public static string? NumberOf(string instanceId)
    {
        var tilde = instanceId.IndexOf('~');
        var number = tilde < 0 ? instanceId : instanceId[..tilde];
        return number.Length == 0 ? null : number;
    }

    private static async Task<List<(DateTimeOffset At, int People, bool Unsure)>> OwnCountsAsync(
        ModbotContext db,
        Guid id,
        DateTimeOffset firstRead,
        DateTimeOffset to,
        CancellationToken ct)
    {
        var before = await db.InstanceHeadCounts.AsNoTracking()
            .Where(h => h.InstanceId == id && h.CountedAt <= firstRead)
            .OrderByDescending(h => h.CountedAt)
            .ThenByDescending(h => h.Id)
            .Select(h => new { h.CountedAt, h.HeadCount, Unsure = h.Source == HeadCounts.FromPage && h.UserCount == null })
            .FirstOrDefaultAsync(ct);

        var after = await db.InstanceHeadCounts.AsNoTracking()
            .Where(h => h.InstanceId == id && h.CountedAt > firstRead && h.CountedAt <= to)
            .OrderBy(h => h.CountedAt)
            .ThenBy(h => h.Id)
            .Select(h => new { h.CountedAt, h.HeadCount, Unsure = h.Source == HeadCounts.FromPage && h.UserCount == null })
            .ToListAsync(ct);

        var counts = new List<(DateTimeOffset, int, bool)>(after.Count + 1);

        if (before is not null)
            counts.Add((before.CountedAt, before.HeadCount, before.Unsure));

        counts.AddRange(after.Select(h => (h.CountedAt, h.HeadCount, h.Unsure)));
        return counts;
    }

    /// <summary>
    /// Modbot's own rows for the shown instances, where it has one: same world, same number, and
    /// open at some point while the list carried it. The latest opened wins, as it would for any
    /// reissued number.
    /// </summary>
    private static async Task<Dictionary<string, (Guid Id, string? Name)>> KnownAsync(
        ModbotContext db,
        string worldId,
        IReadOnlyList<OtherSeen> shown,
        CancellationToken ct)
    {
        var numbers = shown.Where(o => o.Number is not null).Select(o => o.Number!).Distinct(StringComparer.Ordinal).ToList();
        if (numbers.Count == 0)
            return new Dictionary<string, (Guid, string?)>(StringComparer.Ordinal);

        var rows = await db.VRChatInstances.AsNoTracking()
            .Where(i => i.WorldId == worldId && i.VRChatInstanceId != null && numbers.Contains(i.VRChatInstanceId))
            .Select(i => new { i.Id, i.VRChatInstanceId, i.Name, i.OpenedAt, EndsAt = i.ClosedAt ?? i.LastSeenAt })
            .ToListAsync(ct);

        var found = new Dictionary<string, (Guid, string?)>(StringComparer.Ordinal);

        foreach (var other in shown)
        {
            var match = rows
                .Where(r => string.Equals(r.VRChatInstanceId, other.Number, StringComparison.Ordinal)
                            && r.OpenedAt <= other.LastSeenAt
                            && r.EndsAt >= other.FirstSeenAt - InstanceWorldView.ReadingCoversAtMost)
                .OrderByDescending(r => r.OpenedAt)
                .FirstOrDefault();

            if (match is not null)
                found[other.InstanceId] = (match.Id, match.Name);
        }

        return found;
    }

    /// <summary>One other instance, gathered across the reads that carried it.</summary>
    private sealed class OtherSeen(string instanceId, string? number, DateTimeOffset firstSeenAt)
    {
        private readonly List<OtherReading> _readings = [];

        public string InstanceId { get; } = instanceId;

        public string? Number { get; } = number;

        public DateTimeOffset FirstSeenAt { get; } = firstSeenAt;

        public DateTimeOffset LastSeenAt { get; private set; } = firstSeenAt;

        public int Peak { get; private set; }

        public IReadOnlyList<OtherReading> Readings => _readings;

        public void Add(DateTimeOffset at, int people)
        {
            _readings.Add(new OtherReading(at, people));
            LastSeenAt = at;
            if (people > Peak)
                Peak = people;
        }
    }
}
