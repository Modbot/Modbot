using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Analytics;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.Companion.Context;

/// <summary>
/// Which of a set of people are in the group right now, and which of those are its staff. What the
/// roster's Member and Staff rows, the Live page and the live stream all read.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The member list first, the fact log on top.</strong> This used to be the fact log alone:
/// a person was a member if the last join or leave Modbot had recorded for them was a join. Anybody
/// who joined before Modbot started recording has no such fact, so the founders and the long-time
/// staff -- the people a moderator most needs to recognise -- showed as nobody in particular. The
/// member list the sweep copies from VRChat (<see cref="GroupMember"/>) knows everybody, so it is
/// the starting answer.
/// </para>
/// <para>
/// The list is only as fresh as the last sweep, though, and a sweep of a large group rests between
/// runs. So a join, leave, kick or ban the fact log recorded <em>after</em> the list last read that
/// person wins over the list: somebody kicked ten minutes ago is not still a member because the
/// sweep has not come round, and somebody who joined ten minutes ago is not a stranger. That is
/// also the whole fallback: before the first sweep has finished nothing has been read, so every
/// fact is newer than the list and the answer is the fact log's, exactly as it was before. No
/// "stale after N hours" cut-off is needed, because anything newer than the list already beats it.
/// </para>
/// <para>
/// <strong>Staff is the rule the Team page already uses</strong> (<see cref="ModerationRoles"/>): the
/// group's owner, and anybody holding a role that can kick, remove or ban. Roles come off the
/// member list, which has everybody's; the role facts only start when Modbot did.
/// </para>
/// </remarks>
public sealed class MembersAndStaff
{
    public static MembersAndStaff Nobody { get; } = new(
        new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

    private readonly HashSet<string> _members;

    private readonly HashSet<string> _staff;

    internal MembersAndStaff(HashSet<string> members, HashSet<string> staff)
    {
        _members = members;
        _staff = staff;
    }

    public bool IsMember(string subjectId) => _members.Contains(subjectId);

    public bool IsStaff(string subjectId) => _staff.Contains(subjectId);

    /// <summary>
    /// The facts that start or end a membership. A kick and a ban end one as surely as leaving
    /// does, and the audit log does not promise a leave beside them.
    /// </summary>
    private static readonly string[] MembershipFacts =
    [
        FactType.MemberJoined,
        FactType.MemberLeft,
        FactType.MemberKicked,
        FactType.MemberBanned,
    ];

    /// <summary>Reads both answers for these people. Four small queries, whatever the number of people.</summary>
    public static async Task<MembersAndStaff> ReadAsync(
        ModbotContext database,
        IReadOnlyCollection<string> subjectIds,
        CancellationToken ct)
    {
        if (subjectIds.Count == 0)
            return Nobody;

        var settings = await database.Settings
            .AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new
            {
                s.ManagedGroupId,
                s.GroupInfoSnapshot,
                s.MemberSweepCompletedAt,
                s.MemberSweepPreviousStartedAt,
            })
            .FirstOrDefaultAsync(ct);

        var groupId = settings?.ManagedGroupId ?? string.Empty;

        var listed = await database.GroupMembers
            .AsNoTracking()
            .Where(m => m.GroupId == groupId && subjectIds.Contains(m.UserId))
            .Select(m => new { m.UserId, m.LeftAt, m.LastSeenAt, m.MembershipStatus, m.Roles })
            .ToDictionaryAsync(m => m.UserId, StringComparer.Ordinal, ct);

        var banned = (await database.GroupBans
                .AsNoTracking()
                .Where(b => b.GroupId == groupId && b.LiftedAt == null && subjectIds.Contains(b.UserId))
                .Select(b => b.UserId)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var facts = await database.Events
            .AsNoTracking()
            .Where(e => e.SubjectPlatform == FactPlatform.VRChat
                     && subjectIds.Contains(e.SubjectId)
                     && MembershipFacts.Contains(e.Type))
            .OrderBy(e => e.OccurredAt)
            .Select(e => new { e.SubjectId, e.Type, e.OccurredAt })
            .ToListAsync(ct);

        var lastFact = new Dictionary<string, (bool Joined, DateTimeOffset At)>(StringComparer.Ordinal);
        foreach (var fact in facts)
            lastFact[fact.SubjectId] = (fact.Type == FactType.MemberJoined, fact.OccurredAt);

        // Somebody the list has no row for was last "read" when the last full sweep started: the
        // sweep would have listed them had they been in the group then. Null until one has
        // finished, which makes every fact newer and the fact log the answer.
        var sweepStarted = settings?.MemberSweepPreviousStartedAt ?? settings?.MemberSweepCompletedAt;

        var snapshot = GroupInfoSnapshot.Parse(settings?.GroupInfoSnapshot);
        var owner = snapshot?.OwnerId;
        var moderationRoles = (snapshot?.Roles ?? [])
            .Where(ModerationRoles.IsModerationRole)
            .Select(r => r.Id)
            .ToHashSet(StringComparer.Ordinal);

        var members = new HashSet<string>(StringComparer.Ordinal);
        var staff = new HashSet<string>(StringComparer.Ordinal);

        foreach (var subjectId in subjectIds)
        {
            var row = listed.GetValueOrDefault(subjectId);

            var onList = row is { LeftAt: null } && IsMemberStatus(row.MembershipStatus);

            // When the list last said anything about this person: the sweep that listed them, or
            // the sweep that found them gone.
            var listReadAt = row is null ? sweepStarted : row.LeftAt ?? row.LastSeenAt;

            var isMember = lastFact.TryGetValue(subjectId, out var last) && (listReadAt is null || last.At > listReadAt)
                ? last.Joined
                : onList;

            // A standing ban ends it whatever came before; a banned person cannot be let back in
            // without the ban being lifted first.
            if (banned.Contains(subjectId))
                isMember = false;

            if (isMember)
                members.Add(subjectId);

            if (subjectId == owner
                || (isMember && onList && HoldsAny(row!.Roles, moderationRoles)))
                staff.Add(subjectId);
        }

        return new MembersAndStaff(members, staff);
    }

    /// <summary>
    /// Whether VRChat's word for a listed membership means they are in. <c>member</c> does; a person
    /// VRChat sends with no word at all is in too, because the list only carries members and spec
    /// 1.21 stopped promising the word. <c>invited</c>, <c>requested</c>, <c>banned</c> and
    /// <c>inactive</c> are people the group has not (or no longer) let in.
    /// </summary>
    internal static bool IsMemberStatus(string? status) =>
        status is null || string.Equals(status, "member", StringComparison.OrdinalIgnoreCase);

    private static bool HoldsAny(string? rolesJson, HashSet<string> roles)
    {
        if (roles.Count == 0 || string.IsNullOrWhiteSpace(rolesJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(rolesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var role in document.RootElement.EnumerateArray())
            {
                if (role.ValueKind == JsonValueKind.String && roles.Contains(role.GetString()!))
                    return true;
            }

            return false;
        }
        catch (JsonException)
        {
            // One unreadable row must not take out a roster.
            return false;
        }
    }
}
