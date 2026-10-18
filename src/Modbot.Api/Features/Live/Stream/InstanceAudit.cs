using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Live;

namespace Modbot.Api.Features.Live.Stream;

/// <summary>
/// What a paired companion that asked for moderation is sent besides presence: the audit log's
/// facts about its own instance, and about the people seen in it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The rule (user, 2026-10-18).</strong> The Audit Log tab shows events that occurred in the
/// instance the staff member is in, along with any actions by or about people from that instance:
/// somebody joins the instance, then the group, then the Discord, and all three rows show, though the
/// later two are not instance events. The first half needs nothing here (a fact placed in the
/// instance is matched by <see cref="LiveScope.IsHere"/>); this is the second half.
/// </para>
/// <para>
/// <strong>Who is "from the instance".</strong> Somebody with a join or an already-here fact in the
/// device's instance since its moderator's watch of it began (<see cref="InstanceWatching"/>, the
/// rule the Live page uses), leavers included: they are who was seen there, not who is there now.
/// A Discord account counts through its account link. An instance nobody is watching has no such
/// people, because without a watch nobody can say who was there.
/// </para>
/// <para>
/// <strong>Only these types.</strong> Moderation, membership, account links and case files
/// (<see cref="PeopleTypes"/>). What a person did in another instance, their avatar changes, their
/// voice and their Discord messages and nicknames are left out: the log keeps them, this feed is
/// for what a moderator acts on.
/// </para>
/// <para>
/// <strong>Lookups per page, not per event, and only when needed.</strong> A page with no fact of a
/// listed type costs nothing; one with a listed type for somebody never seen in this instance costs
/// two small queries and stops there.
/// </para>
/// </remarks>
public static class InstanceAudit
{
    /// <summary>How far back a stay in the instance is looked for. An instance lasting longer is read from here.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>
    /// The facts about a person that follow them out of the instance. Spelled out rather than taken
    /// from a category, so a type added later is out until somebody decides it belongs.
    /// </summary>
    public static readonly IReadOnlySet<string> PeopleTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        // The group.
        FactType.MemberJoined,
        FactType.MemberLeft,
        FactType.MemberBanned,
        FactType.MemberUnbanned,
        FactType.MemberKicked,
        FactType.RoleGranted,
        FactType.RoleRevoked,
        FactType.GroupRoleGiven,
        FactType.GroupRoleTaken,
        FactType.JoinRequestCreated,
        FactType.JoinRequestRejected,
        FactType.JoinRequestBlocked,
        FactType.GroupInstanceKick,
        FactType.GroupInstanceWarn,

        // Discord, and the link between the two accounts.
        FactType.DiscordMemberJoined,
        FactType.DiscordMemberLeft,
        FactType.DiscordMemberBanned,
        FactType.DiscordMemberUnbanned,
        FactType.DiscordMemberKicked,
        FactType.DiscordMemberTimedOut,
        FactType.DiscordMemberTimeoutRemoved,
        FactType.DiscordLinkCreated,
        FactType.DiscordLinkRemoved,

        // Modbot's own actions.
        FactType.ActionKick,
        FactType.ActionBan,
        FactType.ActionUnban,
        FactType.ActionDiscordBan,
        FactType.ActionDiscordUnban,
        FactType.ActionDiscordKick,
        FactType.ActionDiscordTimeOut,
        FactType.ActionJoinRequestApproved,
        FactType.ActionJoinRequestRejected,

        // Case files.
        FactType.ReportCreated,
        FactType.ReportUpdated,
        FactType.ReportWithdrawn,
    };

    /// <summary>Where a fact comes from, for the row's mark: <c>VRChat</c>, <c>Discord</c> or <c>Modbot</c>.</summary>
    public static string OriginOf(string type)
        => type.StartsWith("discord.", StringComparison.Ordinal) ? "Discord"
            : type.StartsWith("modbot.", StringComparison.Ordinal) ? "Modbot"
            : "VRChat";

    /// <summary>
    /// The ids of the facts on this page that are about somebody seen in the device's instance.
    /// Empty for a device that is not sent moderation, or has not said where it stands.
    /// </summary>
    public static async Task<HashSet<long>> AboutPeopleHereAsync(
        ModbotContext db,
        IReadOnlyList<ModbotEvent> facts,
        LiveScope scope,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(scope);

        var found = new HashSet<long>();

        if (!scope.CarriesModeration || scope.InstanceId is not { Length: > 0 } instance)
            return found;

        // A fact placed in this instance is already wanted; only the rest need a person.
        var candidates = facts
            .Where(f => PeopleTypes.Contains(f.Type) && !scope.IsHere(f.InstanceId, f.WorldId))
            .ToList();

        if (candidates.Count == 0)
            return found;

        var discordIds = candidates
            .SelectMany(PeopleOf)
            .Where(p => p.Platform == FactPlatform.Discord)
            .Select(p => p.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var linked = discordIds.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : await db.DiscordAccountLinks.AsNoTracking()
                .Where(l => discordIds.Contains(l.DiscordUserId) && l.UnlinkedAt == null)
                .ToDictionaryAsync(l => l.DiscordUserId, l => l.VRChatUserId, StringComparer.Ordinal, ct);

        var named = candidates
            .Select(f => (Fact: f, People: PeopleOf(f)
                .Select(p => p.Platform == FactPlatform.Discord ? linked.GetValueOrDefault(p.Id) : p.Id)
                .OfType<string>()
                .ToList()))
            .Where(x => x.People.Count > 0)
            .ToList();

        if (named.Count == 0)
            return found;

        var ids = named.SelectMany(x => x.People).Distinct(StringComparer.Ordinal).ToList();
        var world = scope.WorldId;
        var from = now - Window;

        var seen = await db.Events.AsNoTracking()
            .Where(e => e.OccurredAt >= from
                && e.SubjectPlatform == FactPlatform.VRChat
                && ids.Contains(e.SubjectId)
                && e.InstanceId == instance
                && (world == null || e.WorldId == world)
                && (e.Type == FactType.InstanceJoined || e.Type == FactType.InstancePresenceObserved))
            .GroupBy(e => e.SubjectId)
            .Select(g => new { Id = g.Key, Last = g.Max(e => e.OccurredAt) })
            .ToListAsync(ct);

        if (seen.Count == 0)
            return found;

        var since = await WatchStartAsync(db, scope, instance, world, from, ct);
        if (since is null)
            return found;

        var here = seen
            .Where(s => s.Last >= since.Value - InstanceWatching.ArrivalBurstAllowance)
            .Select(s => s.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (fact, people) in named)
        {
            if (people.Any(here.Contains))
                found.Add(fact.Id);
        }

        return found;
    }

    /// <summary>
    /// When the device's owner started watching the instance, or failing that when the first
    /// moderator did. Null when nobody is watching it.
    /// </summary>
    private static async Task<DateTimeOffset?> WatchStartAsync(
        ModbotContext db, LiveScope scope, string instance, string? world, DateTimeOffset from, CancellationToken ct)
    {
        var people = await new InstancePeopleReader(db).ForNumberAsync(instance, world, from, closedAt: null, ct);
        if (people.Watching.Count == 0)
            return null;

        var owner = scope.OwnerId is { } id
            ? await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.VRChatUserId).FirstOrDefaultAsync(ct)
            : null;

        var theirs = owner is null ? null : people.Watching.FirstOrDefault(w => w.UserId == owner);

        return theirs?.Since ?? people.Watching.Min(w => w.Since);
    }

    /// <summary>The people a fact is about or by: its subject, when it is a person, and its actor.</summary>
    private static IEnumerable<(FactPlatform Platform, string Id)> PeopleOf(ModbotEvent fact)
    {
        if (fact.SubjectPlatform is FactPlatform.VRChat or FactPlatform.Discord)
            yield return (fact.SubjectPlatform, fact.SubjectId);

        if (fact.ActorPlatform is { } platform
            && platform is FactPlatform.VRChat or FactPlatform.Discord
            && fact.ActorId is { } actor)
        {
            yield return (platform, actor);
        }
    }

    /// <summary>
    /// Gives each of these events the audit log's sentence and its source mark. The names come
    /// from the same lookups as the web audit log's, once for the whole page.
    /// </summary>
    public static async Task WriteSentencesAsync(
        ModbotContext db,
        IReadOnlyList<ModbotEvent> facts,
        Dictionary<long, LiveEvent> built,
        Func<LiveEvent, bool> wanted,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var chosen = facts
            .Where(f => built.TryGetValue(f.Id, out var e) && !LiveKinds.IsPresence(e.Kind) && wanted(e))
            .ToList();

        if (chosen.Count == 0)
            return;

        var entries = await AuditNaming.ResolveAsync(db, [.. chosen.Select(AuditQuery.Project)], ct);

        foreach (var entry in entries)
        {
            built[entry.Id] = built[entry.Id] with
            {
                Sentence = AuditSentence.For(entry),
                Origin = OriginOf(entry.Type),
            };
        }
    }
}
