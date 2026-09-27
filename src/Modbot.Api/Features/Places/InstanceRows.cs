using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Analytics.Instances;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Places;

/// <summary>
/// Turns <c>vrchat_instance</c> rows into the instance rows every screen lists.
/// </summary>
/// <remarks>
/// <para>
/// One projection, because four screens now show the same row — the Instances page's "open right
/// now" and "recent" lists, the instances in a world, and the instances one person was seen in. They must
/// agree about how long an instance has been open and what its world is called, and four copies of
/// this arithmetic would eventually not.
/// </para>
/// <para>
/// The world's name is looked up once for the whole page rather than once per row. A world Modbot
/// has only ever seen as an id has no name here and the screen shows the id, which is the truth
/// about what is known.
/// </para>
/// <para>
/// <strong>Closed by a moderator, or ended.</strong> <c>closed_by</c> says how Modbot noticed the
/// end (the list dropped it, or it went quiet), never who ended it. A moderator closing an
/// instance by hand looks the same to the list as an instance that emptied out. What tells them
/// apart is VRChat's own <c>group.instance.close</c> entry in the audit log, which it writes only
/// for a close by hand, so the rows look for one. A screen that said "closed" for every ended
/// instance contradicted a "Closed: 0" tile above it.
/// </para>
/// </remarks>
public static class InstanceRows
{
    /// <summary>
    /// How long after Modbot noticed an instance had ended a close entry can still be its own. The
    /// list is read every few seconds, so a moderator's close comes before Modbot notices the end;
    /// this only allows for VRChat's clock and Modbot's disagreeing a little.
    /// </summary>
    public static readonly TimeSpan CloseEntryLeeway = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Reads a page of instances and names their worlds.
    /// </summary>
    /// <param name="instances">Already filtered, ordered and limited by the caller.</param>
    /// <param name="now">From <c>IModbotClock</c>. An open instance's length is measured against it.</param>
    public static async Task<IReadOnlyList<InstanceRow>> ReadAsync(
        ModbotContext db,
        IQueryable<VRChatInstance> instances,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(instances);

        var page = await instances
            .Select(i => new
            {
                i.Id,
                i.Location,
                i.WorldId,
                i.VRChatInstanceId,
                i.Name,
                i.GroupAccessType,
                i.Region,
                i.OpenedAt,
                i.ClosedAt,
                i.ClosedBy,
                // The instance page's head count, not the group list's number, which counts group
                // members only (HeadCounts). Before the page has been read, the list's number.
                PeopleNow = i.HeadCount ?? i.LastUserCount,
                PeopleNowUnsure = i.HeadCount != null && i.HeadCountUnsure,
                i.PeakUserCount,
                i.PeakUnsure,
                i.LastSeenAt,
            })
            .ToListAsync(ct);

        var worldIds = page.Select(r => r.WorldId).Distinct(StringComparer.Ordinal).ToList();

        var closedByModerator = await ClosedByModeratorAsync(
            db,
            page.Select(r => (r.Id, r.WorldId, r.VRChatInstanceId, r.OpenedAt, r.ClosedAt)).ToList(),
            ct);

        var named = await db.VRChatWorlds
            .AsNoTracking()
            .Where(w => worldIds.Contains(w.WorldId))
            .Select(w => new { w.WorldId, w.Name, w.ThumbnailImageUrl, w.Capacity, w.Platforms })
            .ToDictionaryAsync(w => w.WorldId, w => w, StringComparer.Ordinal, ct);

        return page
            .Select(r =>
            {
                var world = named.GetValueOrDefault(r.WorldId);

                // An open instance counts to now; a closed one to when it closed. Never past `now`,
                // because a clock that disagrees with a stored time should not produce an instance
                // that has been open for minus four minutes.
                var until = r.ClosedAt ?? now;
                var minutes = until <= r.OpenedAt ? 0m : (decimal)(until - r.OpenedAt).TotalMinutes;

                return new InstanceRow(
                    r.Id,
                    r.Location,
                    r.WorldId,
                    world?.Name,
                    world?.ThumbnailImageUrl,
                    r.VRChatInstanceId,
                    r.Name,
                    r.GroupAccessType,
                    r.Region,
                    r.OpenedAt,
                    r.ClosedAt,
                    r.ClosedBy,
                    r.PeopleNow,
                    r.PeakUserCount,
                    Math.Round(minutes, 1),
                    world?.Capacity,
                    PlatformsOf(world?.Platforms),
                    closedByModerator.Contains(r.Id),
                    r.PeopleNowUnsure,
                    r.PeakUnsure);
            })
            .ToList();
    }

    /// <summary>
    /// Which of the ended instances a moderator closed by hand: those with a
    /// <c>group.instance.close</c> entry for the same world and instance number, dated between the
    /// instance's opening and a little after it ended.
    /// </summary>
    /// <remarks>
    /// VRChat hands instance numbers out again, so one entry could fall inside two rows' times only
    /// if the number was reissued within <see cref="CloseEntryLeeway"/> of the first ending. It then
    /// belongs to the later row that had opened by then, never to both.
    /// </remarks>
    private static async Task<IReadOnlySet<Guid>> ClosedByModeratorAsync(
        ModbotContext db,
        IReadOnlyList<(Guid Id, string WorldId, string? Number, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt)> rows,
        CancellationToken ct)
    {
        var ended = rows.Where(r => r.ClosedAt is not null && r.Number is not null).ToList();

        if (ended.Count == 0)
            return new HashSet<Guid>();

        var worlds = ended.Select(r => r.WorldId).Distinct(StringComparer.Ordinal).ToList();
        var numbers = ended.Select(r => r.Number!).Distinct(StringComparer.Ordinal).ToList();
        var earliest = ended.Min(r => r.OpenedAt);
        var latest = ended.Max(r => r.ClosedAt!.Value) + CloseEntryLeeway;

        var closes = await db.Events.AsNoTracking()
            .Where(e => e.Type == FactType.GroupInstanceClosed
                        && e.OccurredAt >= earliest && e.OccurredAt <= latest
                        && e.WorldId != null && worlds.Contains(e.WorldId)
                        && e.InstanceId != null && numbers.Contains(e.InstanceId))
            .Select(e => new { e.WorldId, e.InstanceId, e.OccurredAt })
            .ToListAsync(ct);

        var found = new HashSet<Guid>();

        foreach (var close in closes)
        {
            var owner = ended
                .Where(r => string.Equals(r.WorldId, close.WorldId, StringComparison.Ordinal)
                            && string.Equals(r.Number, close.InstanceId, StringComparison.Ordinal)
                            && r.OpenedAt <= close.OccurredAt
                            && close.OccurredAt <= r.ClosedAt!.Value + CloseEntryLeeway)
                .OrderByDescending(r => r.OpenedAt)
                .Select(r => (Guid?)r.Id)
                .FirstOrDefault();

            if (owner is { } id)
                found.Add(id);
        }

        return found;
    }

    /// <summary>
    /// An instance in words: the world and the instance's name in quotes -- <c>Murder 4 “6 killed 7”</c>
    /// -- or the world and VRChat's number when it has no name, <c>Murder 4 #16354</c>. The same
    /// rule the web app's <c>instanceName</c> follows.
    /// </summary>
    public static string Label(string? worldName, string worldId, string? number, string? name)
    {
        var world = worldName ?? worldId;

        if (!string.IsNullOrWhiteSpace(name))
            return $"{world} “{name}”";

        return number is { } n ? $"{world} #{n}" : world;
    }

    /// <summary>A world's stored platform list, or null when it has none or it cannot be read.</summary>
    internal static IReadOnlyList<string>? PlatformsOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
