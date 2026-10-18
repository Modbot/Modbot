using System.Text.Json.Nodes;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Audit;

/// <summary>
/// The audit log's sentence for a fact, as plain text: "Mira kicked Ada out of The Black Cat #39047."
/// </summary>
/// <remarks>
/// <para>
/// <strong>A second copy, on purpose.</strong> The web audit log writes its sentences in the browser
/// (<c>src/Modbot.Web/src/components/factSentence.tsx</c>), with clickable names, and a companion
/// cannot run that. This writes the same words, for the types the companion's live feed sends
/// (<see cref="Live.Stream.InstanceAudit.PeopleTypes"/>), as text. A change to one of these sentences
/// there is a change here, and the other way round. Where the two differ:
/// </para>
/// <list type="bullet">
/// <item>An instance kick here does not carry the "after N minutes" clause the web adds when a
/// moderator's client had recorded how long the person had been there.</item>
/// <item>The two Discord account link facts have no sentence on the web (it falls back to the raw
/// type); these two are new wording.</item>
/// <item>A fact with no sentence here reads "Actor - Label - Subject in Place" from
/// <see cref="FactLabels"/>, where the web falls back to the raw type.</item>
/// </list>
/// <para>
/// Names are the ones <see cref="AuditNaming"/> found, so call it on resolved entries. A name nobody
/// knows is replaced by the id, as the web shows it.
/// </para>
/// </remarks>
public static class AuditSentence
{
    private static readonly Dictionary<string, Func<Parts, string>> Writers = new(StringComparer.Ordinal)
    {
        // VRChat: membership and moderation.
        [FactType.MemberJoined] = p => $"{p.Subject} joined the group.",
        [FactType.MemberLeft] = p => $"{p.Subject} left the group.",
        [FactType.MemberBanned] = p => p.HasActor ? $"{p.Actor} banned {p.Subject} from the group." : $"{p.Subject} was banned from the group.",
        [FactType.MemberUnbanned] = p => p.HasActor ? $"{p.Actor} lifted the ban on {p.Subject}." : $"The ban on {p.Subject} was lifted.",
        [FactType.MemberKicked] = p => p.HasActor ? $"{p.Actor} removed {p.Subject} from the group." : $"{p.Subject} was removed from the group.",
        [FactType.RoleGranted] = p => $"{p.Actor} gave {p.Subject} {Named(p.Text("roleName"), "role")}.",
        [FactType.RoleRevoked] = p => $"{p.Actor} took {Named(p.Text("roleName"), "role")} away from {p.Subject}.",
        [FactType.JoinRequestCreated] = p => $"{p.Subject} asked to join the group.",
        [FactType.JoinRequestRejected] = p => $"{p.Actor} turned down {p.Subject}'s request to join.",
        [FactType.JoinRequestBlocked] = p => $"{p.Actor} blocked {p.Subject} from asking to join.",
        [FactType.GroupInstanceKick] = p => $"{p.Actor} kicked {p.Subject} out of {p.Place ?? "an instance"}.",
        [FactType.GroupInstanceWarn] = p => $"{p.Actor} warned {p.Subject} in {p.Place ?? "an instance"}.",

        // Discord.
        [FactType.DiscordMemberJoined] = p => $"{p.Subject} joined the Discord server.",
        [FactType.DiscordMemberLeft] = p => $"{p.Subject} left the Discord server.",
        [FactType.DiscordMemberBanned] = p =>
            (p.HasActor ? $"{p.Actor} banned {p.Subject} from the Discord server" : $"{p.Subject} was banned from the Discord server")
            + Because(p.Text("reason")),
        [FactType.DiscordMemberUnbanned] = p =>
            (p.HasActor ? $"{p.Actor} unbanned {p.Subject} on Discord" : $"{p.Subject} was unbanned on Discord") + ".",
        [FactType.DiscordMemberKicked] = p => $"{p.Actor} kicked {p.Subject} from the Discord server" + Because(p.Text("reason")),
        [FactType.DiscordMemberTimedOut] = p =>
            (p.HasActor ? $"{p.Actor} timed out {p.Subject} on Discord" : $"{p.Subject} was timed out on Discord")
            + Because(p.Text("reason")),
        [FactType.DiscordMemberTimeoutRemoved] = p =>
            (p.HasActor ? $"{p.Actor} took {p.Subject}'s Discord timeout off" : $"{p.Subject}'s Discord timeout was taken off") + ".",

        // The link between a Discord account and a VRChat one: the subject is the VRChat person.
        [FactType.DiscordLinkCreated] = p => $"{p.Subject} linked their Discord account.",
        [FactType.DiscordLinkRemoved] = p => $"{p.Subject}'s Discord account was unlinked.",

        // Case files.
        [FactType.ReportCreated] = p => $"{p.Actor} wrote a case file for {p.Subject}" + Because(p.Reasons()),
        [FactType.ReportUpdated] = p => $"{p.Actor} edited the case file for {p.Subject}.",
        [FactType.ReportWithdrawn] = p => $"{p.Actor} withdrew the case file for {p.Subject}" + Because(p.Text("note")),

        // Moderation done from Modbot.
        [FactType.ActionKick] = p => $"{p.Actor} kicked {p.Subject} from the group" + Because(p.Reasons()),
        [FactType.ActionBan] = p => $"{p.Actor} banned {p.Subject} from the group" + Because(p.Reasons()),
        [FactType.ActionUnban] = p => $"{p.Actor} unbanned {p.Subject}" + Because(p.Reasons()),
        [FactType.ActionDiscordBan] = p => $"{p.Actor} banned {p.Subject} from the Discord server" + Because(p.Text("reason")),
        [FactType.ActionDiscordUnban] = p => $"{p.Actor} lifted {p.Subject}'s Discord ban" + Because(p.Text("reason")),
        [FactType.ActionDiscordKick] = p => $"{p.Actor} removed {p.Subject} from the Discord server" + Because(p.Text("reason")),
        [FactType.ActionDiscordTimeOut] = p => $"{p.Actor} timed {p.Subject} out on Discord" + Because(p.Text("reason")),
        [FactType.ActionJoinRequestApproved] = p => $"{p.Actor} let {p.Subject} into the group" + Because(p.Reasons() ?? p.Text("note")),
        [FactType.ActionJoinRequestRejected] = p =>
            $"{p.Actor} turned down {p.Subject}'s request to join the group" + Because(p.Reasons() ?? p.Text("note")),
        [FactType.GroupRoleGiven] = p => $"{p.Actor} gave {p.Subject} the group role{Quoted(p.Text("roleName"))}.",
        [FactType.GroupRoleTaken] = p => $"{p.Actor} took the group role{Quoted(p.Text("roleName"))} away from {p.Subject}.",
    };

    /// <summary>Whether this type has a sentence of its own here.</summary>
    public static bool Writes(string type) => Writers.ContainsKey(type);

    /// <summary>The sentence for a resolved entry.</summary>
    public static string For(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var parts = new Parts(entry);

        return Writers.TryGetValue(entry.Type, out var write) ? write(parts) : Fallback(parts);
    }

    private static string Fallback(Parts p)
    {
        var line = (p.HasActor ? $"{p.Actor} - " : "")
            + FactLabels.For(p.Entry.Type)
            + (p.Entry.SubjectKind == SubjectKind.Person ? $" - {p.Subject}" : "")
            + (p.Place is { } place ? $" in {place}" : "");

        return p.Entry.Description is { Length: > 0 } description ? $"{line}. {description}" : line;
    }

    private static string Because(string? why) => why is null ? "." : $": {why}.";

    private static string Quoted(string? value) => value is null ? "" : $" “{value}”";

    /// <summary>"the role “Moderator”", or "a role" when its name was never learned.</summary>
    private static string Named(string? name, string kind) => name is null ? $"a {kind}" : $"the {kind} “{name}”";

    private sealed class Parts(AuditEntry entry)
    {
        public AuditEntry Entry { get; } = entry;

        public string Subject => Entry.SubjectName ?? Entry.SubjectId;

        public bool HasActor => Entry.ActorId is not null;

        /// <summary>Who did it, or "Somebody" when the source named nobody.</summary>
        public string Actor => Entry.ActorId is null ? "Somebody" : Entry.ActorName ?? Entry.ActorId;

        /// <summary>"The Black Cat #39047" for an instance, the world alone for a world, null for neither.</summary>
        public string? Place
        {
            get
            {
                var world = Entry.WorldName ?? Entry.WorldId;
                var tag = Entry.InstanceName is { } named && named.Trim().Length > 0
                    ? $"“{named.Trim()}”"
                    : Entry.InstanceId is { Length: > 0 } number ? $"#{number}" : null;

                if (world is not null && tag is not null)
                    return $"{world} {tag}";

                if (world is not null)
                    return world;

                return tag is null ? null : $"instance {tag}";
            }
        }

        public string? Text(string key) => AuditJson.Text(Entry.Data, key) is { Length: > 0 } text ? text : null;

        /// <summary>The reason labels on a case file fact or a Modbot action, as one phrase.</summary>
        public string? Reasons()
        {
            if (Entry.Data is not JsonObject data || data["reasonLabels"] is not JsonArray labels)
                return null;

            var words = labels.OfType<JsonValue>()
                .Select(v => v.TryGetValue<string>(out var s) ? s : null)
                .OfType<string>()
                .ToList();

            return words.Count == 0 ? null : string.Join(", ", words);
        }
    }
}
