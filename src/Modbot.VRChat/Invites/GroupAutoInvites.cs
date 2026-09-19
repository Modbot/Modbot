using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Giveaways;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;
using Modbot.Core.Live;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.VRChat.Invites;

/// <summary>
/// Invites people to the group once they have spent long enough in one of its instances and pass
/// the group's rules (auto-invites design).
/// </summary>
/// <remarks>
/// <para>
/// One pass sends <strong>at most one invite</strong>. The loop runs every thirty seconds and the
/// sender refuses to go faster than that anyway, so the deployment-wide rate the user asked for is
/// readable straight off this class rather than reconstructed from a token bucket.
/// </para>
/// <para>
/// <strong>Nothing is kept between passes.</strong> There is no queue to lose: each pass works out
/// from the database who is standing in the group's instances right now. A restart loses the pass
/// it was in the middle of and the next one reaches the same answer (auto-invites design §7).
/// </para>
/// <para>
/// <strong>No companion reporting means no invite.</strong> Modbot only knows who is in an instance
/// while somebody's client is telling it, and only then does it know since when. Where nobody is
/// watching there is no list of people, so there is nobody to consider — and a duration is never
/// guessed from a head count or a poll (§8).
/// </para>
/// </remarks>
public sealed class GroupAutoInvites
{
    /// <summary>
    /// How many people one pass will check the rules against before giving up for this pass.
    /// </summary>
    /// <remarks>
    /// The rules are checked one person at a time and only one invite goes out per pass, so a busy
    /// evening would otherwise have every pass evaluating a full instance to send one invite. The
    /// list is ordered longest-here first, so the cap only ever leaves out the people who have
    /// been there least long — and they are still there next pass.
    /// </remarks>
    public const int MostPeoplePerPass = 25;

    private readonly ModbotContext _db;
    private readonly GiveawayRuleChecker _rules;
    private readonly GroupInvites _invites;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    public GroupAutoInvites(
        ModbotContext db,
        GiveawayRuleChecker rules,
        GroupInvites invites,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        IModbotClock clock,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(invites);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _rules = rules;
        _invites = invites;
        _facts = facts;
        _partitions = partitions;
        _clock = clock;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>Somebody who could be invited, and what is known about their stay.</summary>
    private readonly record struct Standing(string UserId, string? InstanceId, int Minutes, bool SeenArriving);

    /// <summary>
    /// One pass. Returns true when an invite went out.
    /// </summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct = default)
    {
        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new
            {
                s.GroupAutoInviteEnabled,
                s.GroupAutoInviteMinutesInInstance,
                s.GroupAutoInviteAgainAfterDays,
                s.GroupAutoInviteRules,
                s.ManagedGroupId,
                s.VRChatSessionUserId,
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        // Off means off: nothing is read, nothing is queried, nothing is sent.
        if (settings is not { GroupAutoInviteEnabled: true })
            return false;

        if (settings.ManagedGroupId is not { Length: > 0 } groupId)
            return false;

        // Asked before any of the work below, so a pass that could not send anything anyway costs
        // one small query.
        if (!await _invites.MaySendAsync(ct).ConfigureAwait(false))
            return false;

        var now = _clock.UtcNow;
        var minutes = Math.Max(Settings.MinimumAutoInviteMinutes, settings.GroupAutoInviteMinutesInInstance);
        var longEnough = TimeSpan.FromMinutes(minutes);

        var instances = await _db.VRChatInstances.AsNoTracking()
            .Where(i => i.ClosedAt == null && i.GroupId == groupId)
            .ToListAsync(ct).ConfigureAwait(false);

        if (instances.Count == 0)
            return false;

        // Built here rather than injected, as every other reader of it is: it holds nothing but
        // the context and reads Modbot's own tables.
        var here = await new InstancePeopleReader(_db).ForInstancesAsync(instances, ct).ConfigureAwait(false);

        var standing = new List<Standing>();

        foreach (var instance in instances)
        {
            if (!here.TryGetValue(instance.Id, out var people) || !people.IsWatched)
                continue;

            foreach (var person in people.Here)
            {
                // `Since` is when they were first seen this stay. Where they were already there
                // when watching began it is a lower bound on the stay, not an estimate of it --
                // which is safe in the only direction that matters (design §8).
                if (now - person.Since < longEnough)
                    continue;

                standing.Add(new Standing(
                    person.UserId,
                    instance.VRChatInstanceId,
                    (int)Math.Floor((now - person.Since).TotalMinutes),
                    person.SeenArriving));
            }
        }

        if (standing.Count == 0)
            return false;

        // Longest here first. The cap below then only ever leaves out the newest arrivals, who
        // will still be there next pass.
        var considered = standing
            .OrderByDescending(p => p.Minutes)
            .DistinctBy(p => p.UserId, StringComparer.Ordinal)
            .Take(MostPeoplePerPass)
            .ToList();

        var allowed = await AllowedAsync(
            groupId, settings.VRChatSessionUserId, settings.GroupAutoInviteAgainAfterDays, considered, now, ct)
            .ConfigureAwait(false);

        if (allowed.Count == 0)
            return false;

        var rule = GiveawayRules.ReadStored(settings.GroupAutoInviteRules);

        foreach (var person in allowed)
        {
            ct.ThrowIfCancellationRequested();

            var answer = await _rules.CheckVRChatUserAsync(rule, person.UserId, ct).ConfigureAwait(false);

            if (!answer.Met)
                continue;

            var result = await _invites.SendAsync(groupId, person.UserId, person.InstanceId, ct).ConfigureAwait(false);

            // Somebody else got in first -- another process, or the gap simply closed while the
            // rules were being checked. Nothing is wrong and nothing is recorded.
            if (result.Outcome == InviteOutcome.TooSoon)
                return false;

            await RecordAsync(person, rule, result, ct).ConfigureAwait(false);

            if (!result.Worked)
            {
                _log.Warning(
                    "Could not invite {UserId} to the group: {Problem}", person.UserId, result.Problem);
            }

            // One invite per pass, whether it worked or not: a refusal counts as an attempt, and a
            // second try thirty seconds later is the whole of the retry policy.
            return result.Worked;
        }

        return false;
    }

    /// <summary>
    /// The people from this pass who may be invited at all — before any rule is asked.
    /// </summary>
    /// <remarks>
    /// None of these are configurable (design §4.2). A group that wants a banned person back has
    /// somebody who can press a button.
    /// </remarks>
    private async Task<List<Standing>> AllowedAsync(
        string groupId,
        string? self,
        int againAfterDays,
        List<Standing> people,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var ids = people.Select(p => p.UserId).ToList();

        var members = await _db.GroupMembers.AsNoTracking()
            .Where(m => m.GroupId == groupId && m.LeftAt == null && ids.Contains(m.UserId))
            .Select(m => m.UserId)
            .ToListAsync(ct).ConfigureAwait(false);

        var banned = await _db.GroupBans.AsNoTracking()
            .Where(b => b.GroupId == groupId && b.LiftedAt == null && ids.Contains(b.UserId))
            .Select(b => b.UserId)
            .ToListAsync(ct).ConfigureAwait(false);

        // Ever thrown out, whatever has happened since. An unban is a decision to let somebody
        // come back, not a decision to go and fetch them.
        var shownTheDoor = await _db.Events.AsNoTracking()
            .Where(e => e.SubjectPlatform == FactPlatform.VRChat
                && ids.Contains(e.SubjectId)
                && ThrownOut.Contains(e.Type))
            .Select(e => e.SubjectId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        var since = now - TimeSpan.FromDays(Math.Max(1, againAfterDays));

        var invited = await _db.GroupAutoInvites.AsNoTracking()
            .Where(i => ids.Contains(i.UserId) && i.InvitedAt >= since)
            .Select(i => i.UserId)
            .ToListAsync(ct).ConfigureAwait(false);

        var out_ = members
            .Concat(banned)
            .Concat(shownTheDoor)
            .Concat(invited)
            .ToHashSet(StringComparer.Ordinal);

        // Ordinal: a VRChat id is opaque text and is compared as text (foundation §3.1.1).
        if (self is { Length: > 0 })
            out_.Add(self);

        return [.. people.Where(p => !out_.Contains(p.UserId))];
    }

    /// <summary>The fact types that mean the group has thrown this person out at some point.</summary>
    private static readonly string[] ThrownOut =
    [
        FactType.MemberBanned,
        FactType.MemberKicked,
        FactType.ActionKick,
        FactType.ActionBan,
        FactType.AutoModGroupBan,
        FactType.AutoModGroupRemove,
        FactType.CopiedBan,
        FactType.CopiedRemove,
    ];

    private async Task RecordAsync(Standing person, GiveawayRule rule, InviteResult result, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

        var data = new JsonObject
        {
            ["minutesInInstance"] = person.Minutes,
            ["seenArriving"] = person.SeenArriving,
            ["rules"] = GiveawayRules.Describe(rule),
        };

        if (result.Problem is { Length: > 0 } problem)
            data["problem"] = problem;

        await _facts.WriteAsync(
            new FactRecord
            {
                Type = result.Worked ? FactType.GroupAutoInvited : FactType.GroupAutoInviteFailed,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = person.UserId,
                InstanceId = person.InstanceId,

                // No actor. Modbot decided this on its own, as AutoMod's actions and the
                // calendar's opener do.
                Source = FactSource.Modbot,
                Data = data,
            },
            ct).ConfigureAwait(false);
    }
}
