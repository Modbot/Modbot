using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Data;

/// <summary>One instance's identity and times, enough to look for a close entry that belongs to it.</summary>
/// <param name="Id">Modbot's own id for the instance (<c>vrchat_instance.id</c>).</param>
/// <param name="Number">VRChat's number for the instance. Null when Modbot never learned it, so nothing can match.</param>
/// <param name="ClosedAt">When Modbot noticed it had ended. Null while it is open.</param>
public readonly record struct InstanceEndRow(
    Guid Id,
    string WorldId,
    string? Number,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt);

/// <summary>
/// Which ended instances a moderator closed by hand, told apart from the ones that ended on their own.
/// </summary>
/// <remarks>
/// <para>
/// <c>vrchat_instance.closed_by</c> says how Modbot noticed an end (the group's list dropped the
/// instance, or it went quiet), never who ended it: a moderator closing an instance by hand looks
/// the same to the list as one that emptied out. What tells them apart is VRChat's own
/// <c>group.instance.close</c> entry in the audit log, which VRChat writes only for a close by hand.
/// </para>
/// <para>
/// One rule, here, because three places have to agree about it: the rows that say "ended" or
/// "closed", the Stats page's "Manually closed" and "Naturally ended" counts, and the sync that writes
/// Modbot's "ended on its own" entry. Two copies of it would sooner or later put an instance in both
/// counts, or in neither.
/// </para>
/// </remarks>
public static class InstanceCloseEntries
{
    /// <summary>
    /// How long after Modbot noticed an instance had ended a close entry can still be its own. The
    /// list is read every few seconds, so a moderator's close comes before Modbot notices the end;
    /// this only allows for VRChat's clock and Modbot's disagreeing a little.
    /// </summary>
    public static readonly TimeSpan Leeway = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Which of the ended instances a moderator closed by hand: those with a
    /// <c>group.instance.close</c> entry for the same world and instance number, dated between the
    /// instance's opening and a little after it ended.
    /// </summary>
    /// <remarks>
    /// VRChat hands instance numbers out again, so one entry could fall inside two rows' times only
    /// if the number was reissued within <see cref="Leeway"/> of the first ending. It then
    /// belongs to the later row that had opened by then, never to both.
    /// </remarks>
    public static async Task<IReadOnlySet<Guid>> ClosedByHandAsync(
        ModbotContext db,
        IReadOnlyList<InstanceEndRow> rows,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rows);

        var ended = rows.Where(r => r.ClosedAt is not null && r.Number is not null).ToList();

        if (ended.Count == 0)
            return new HashSet<Guid>();

        var worlds = ended.Select(r => r.WorldId).Distinct(StringComparer.Ordinal).ToList();
        var numbers = ended.Select(r => r.Number!).Distinct(StringComparer.Ordinal).ToList();
        var earliest = ended.Min(r => r.OpenedAt);
        var latest = ended.Max(r => r.ClosedAt!.Value) + Leeway;

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
                            && close.OccurredAt <= r.ClosedAt!.Value + Leeway)
                .OrderByDescending(r => r.OpenedAt)
                .Select(r => (Guid?)r.Id)
                .FirstOrDefault();

            if (owner is { } id)
                found.Add(id);
        }

        return found;
    }
}
