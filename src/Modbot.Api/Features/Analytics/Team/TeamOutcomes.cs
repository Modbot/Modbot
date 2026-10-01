using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Analytics.Team;

/// <summary>
/// What came of the team's actions: whether the people acted on came back to be acted on again,
/// and how many bans were lifted and why (<see cref="ActedOnAgain"/>, <see cref="BansLifted"/>).
/// </summary>
/// <remarks>
/// Team numbers only, never per moderator. An undo or an overturn names a decision somebody got
/// wrong; that belongs on the case file and in a review, not in a column beside their name.
/// </remarks>
public sealed class TeamOutcomes(AnalyticsSql sql)
{
    /// <summary>
    /// The window both numbers look across: thirty days, the number the repeat-offender rule
    /// already uses and moderators already say ("fourth kick in thirty days").
    /// </summary>
    public const int Days = 30;

    /// <summary>
    /// People acted on in the window (VRChat), and how many of them had an earlier action of a
    /// counted kind within <see cref="Days"/> days before one of those.
    /// </summary>
    public async Task<ActedOnAgain> ActedOnAgainAsync(
        DateOnly from,
        DateOnly to,
        IReadOnlyList<string> countedTypes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(countedTypes);

        const string Sql = """
            SELECT COUNT(*)::int, COUNT(*) FILTER (WHERE again)::int
            FROM (
                SELECT e.subject_id,
                       bool_or(EXISTS (
                           SELECT 1 FROM modbot_event p
                           WHERE p.type = ANY(@types)
                             AND p.subject_platform = e.subject_platform
                             AND p.subject_id = e.subject_id
                             AND p.occurred_at < e.occurred_at
                             AND p.occurred_at >= e.occurred_at - @reach)) AS again
                FROM modbot_event e
                WHERE e.type = ANY(@types)
                  AND e.subject_platform = @vrchat
                  AND e.subject_id IS NOT NULL
                  AND e.occurred_at >= @from AND e.occurred_at < @to
                GROUP BY e.subject_id
            ) people
            """;

        var rows = await sql.ReadAsync(
            Sql,
            r => new ActedOnAgain(r.GetInt32(0), r.GetInt32(1), Days),
            ct,
            ("types", countedTypes.ToArray()),
            ("vrchat", (short)FactPlatform.VRChat),
            ("reach", TimeSpan.FromDays(Days)),
            ("from", AnalyticsSql.DayStart(from)),
            ("to", AnalyticsSql.DayEnd(to)));

        return rows.Count > 0 ? rows[0] : new ActedOnAgain(0, 0, Days);
    }

    /// <summary>
    /// Bans in the window and how many were lifted within <see cref="Days"/> days, from VRChat's
    /// audit log; and the reasons given on the case files lifted from Modbot in the window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reasons are counted by what each one says, not sorted into "overturned" and "not": a
    /// group writes its own reason list, and whether "Time served" is an overturn is theirs to read.
    /// A ban lifted in VRChat itself carries no reason and is in the first number only.
    /// </para>
    /// <para>
    /// The lifted share is out of the bans at least <see cref="Days"/> days old only. A ban from
    /// last week has not had its thirty days; counted as one that stood, it would make the share
    /// smaller the closer the range ends to today.
    /// </para>
    /// </remarks>
    public async Task<BansLifted> BansLiftedAsync(DateOnly from, DateOnly to, DateTimeOffset now, CancellationToken ct)
    {
        var start = AnalyticsSql.DayStart(from);
        var end = AnalyticsSql.DayEnd(to);
        var reach = TimeSpan.FromDays(Days);

        const string Sql = """
            SELECT COUNT(*)::int,
                   COUNT(*) FILTER (WHERE old_enough)::int,
                   COUNT(*) FILTER (WHERE old_enough AND lifted)::int
            FROM (
                SELECT b.occurred_at <= @oldest AS old_enough,
                       EXISTS (
                           SELECT 1 FROM modbot_event u
                           WHERE u.type = @unbanned
                             AND u.subject_platform = b.subject_platform
                             AND u.subject_id = b.subject_id
                             AND u.occurred_at > b.occurred_at
                             AND u.occurred_at <= b.occurred_at + @reach) AS lifted
                FROM modbot_event b
                WHERE b.type = @banned
                  AND b.subject_platform = @vrchat
                  AND b.subject_id IS NOT NULL
                  AND b.occurred_at >= @from AND b.occurred_at < @to
            ) bans
            """;

        var counts = await sql.ReadAsync(
            Sql,
            r => (Bans: r.GetInt32(0), OldEnough: r.GetInt32(1), Lifted: r.GetInt32(2)),
            ct,
            ("banned", FactType.MemberBanned),
            ("unbanned", FactType.MemberUnbanned),
            ("vrchat", (short)FactPlatform.VRChat),
            ("reach", reach),
            ("oldest", now - reach),
            ("from", start),
            ("to", end));

        var lifts = await sql.Db.CaseFiles.AsNoTracking()
            .Where(c => c.LiftedAt >= start && c.LiftedAt < end)
            .Select(c => c.LiftReasonIds)
            .ToListAsync(ct);

        var picked = lifts.Select(ReasonIds).ToList();
        var ids = picked.SelectMany(p => p).Distinct().ToList();

        var labels = await sql.Db.BanReasons.AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .Select(r => new { r.Id, r.Label })
            .ToDictionaryAsync(r => r.Id, r => r.Label, ct);

        var reasons = picked
            .SelectMany(p => p)
            .Where(labels.ContainsKey)
            .GroupBy(id => labels[id], StringComparer.Ordinal)
            .Select(g => new LiftReason(g.Key, g.Count()))
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var (bans, oldEnough, lifted) = counts.Count > 0 ? counts[0] : (0, 0, 0);

        return new BansLifted(bans, oldEnough, lifted, Days, reasons, picked.Count(p => p.Count == 0));
    }

    /// <summary>The reason ids on a lift. A malformed list reads as none rather than failing the page.</summary>
    private static IReadOnlyList<Guid> ReasonIds(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<string>>(json) ?? [])
                .Select(s => Guid.TryParse(s, out var id) ? id : (Guid?)null)
                .Where(id => id is not null)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
