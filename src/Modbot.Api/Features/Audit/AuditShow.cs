using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Audit;

/// <summary>
/// The broad kinds of fact a person's timeline can be narrowed to: <c>moderation</c>,
/// <c>presence</c> and <c>discord</c>. Everything is the timeline with none of them.
/// </summary>
/// <remarks>
/// <para>
/// "Is this person repeatedly causing problems?" is answered by a handful of entries, and on a
/// person a moderator's companion has seen for a month those few sit under hundreds of arrivals and
/// leaves. The log's own split is no help here: presence is moderation history in
/// <see cref="AuditVisibility"/> (who may read it), and that is right for who may read it and wrong
/// for what a moderator wants to look at. So this is a second, narrower question about a type, asked
/// only to narrow what is shown. It never widens anything: the endpoint applies it to the type list
/// after the permission filter has already run.
/// </para>
/// <para>
/// A type can be in more than one: a Discord ban is moderation and it is Discord. Membership comings
/// and goings, role changes, profile changes and invites are in none of the three on purpose; they
/// are what "everything" adds.
/// </para>
/// </remarks>
public static class AuditShow
{
    public const string Moderation = "moderation";
    public const string Presence = "presence";
    public const string Discord = "discord";

    /// <summary>Every value <c>show</c> takes, in the order a filter offers them.</summary>
    public static IReadOnlyList<string> All { get; } = [Moderation, Presence, Discord];

    /// <summary>
    /// What was done to somebody, or what they did to somebody, as a moderation decision: bans,
    /// kicks, warnings, turned-down requests, notes, case files, AutoMod and the bans Modbot copied.
    /// </summary>
    private static readonly HashSet<string> ModerationTypes = new(StringComparer.Ordinal)
    {
        FactType.MemberBanned,
        FactType.MemberUnbanned,
        FactType.MemberKicked,
        FactType.GroupInstanceKick,
        FactType.GroupInstanceWarn,
        FactType.JoinRequestRejected,
        FactType.JoinRequestBlocked,
        FactType.ActionKick,
        FactType.ActionBan,
        FactType.ActionUnban,
        FactType.ActionFailed,
        FactType.ActionJoinRequestRejected,
        FactType.NoteAdded,
        FactType.NoteTakenBack,
        FactType.ReportCreated,
        FactType.ReportUpdated,
        FactType.ReportWithdrawn,
        FactType.ReportSnapshotRecaptured,
        FactType.UserAgeFlagSet,
        FactType.UserAgeFlagCleared,
        FactType.AutoModFlag,
        FactType.AutoModFlagDismissed,
        FactType.AutoModFlagConfirmed,
        FactType.AutoModMessageDeleted,
        FactType.AutoModTimeout,
        FactType.AutoModGroupBan,
        FactType.AutoModGroupRemove,
        FactType.DiscordMemberBanned,
        FactType.DiscordMemberUnbanned,
        FactType.DiscordMemberKicked,
        FactType.DiscordMemberTimedOut,
        FactType.DiscordMemberTimeoutRemoved,
        FactType.DiscordMessagesRemoved,
        FactType.DiscordMessagesBulkRemoved,
        FactType.CopiedBan,
        FactType.CopiedUnban,
        FactType.CopiedRemove,
        FactType.ReviewOpened,
        FactType.ReviewClosed,
    };

    /// <summary>Where they were and what they looked like there, as a moderator's companion saw it.</summary>
    private static readonly HashSet<string> PresenceTypes = new(StringComparer.Ordinal)
    {
        FactType.InstanceJoined,
        FactType.InstanceLeft,
        FactType.InstancePresenceObserved,
        FactType.AvatarChanged,
        FactType.InstanceLogStopped,
    };

    /// <summary>
    /// Whether <paramref name="value"/> is one of <see cref="All"/>. Anything else, "everything"
    /// included, narrows nothing.
    /// </summary>
    public static bool TryParse(string? value, out string show)
    {
        show = All.FirstOrDefault(s => string.Equals(s, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return show.Length > 0;
    }

    /// <summary>Whether a fact of this type is shown under <paramref name="show"/>.</summary>
    public static bool Includes(string show, string type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return show switch
        {
            Moderation => ModerationTypes.Contains(type),
            Presence => PresenceTypes.Contains(type),
            // Everything Discord's own log and the bot recorded, and every copy Modbot made between
            // the two platforms, whichever way it went: the copies are how a Discord ban follows a
            // VRChat one, and a Discord history without them reads as bans from nowhere.
            Discord => type.StartsWith("discord.", StringComparison.Ordinal)
                || type.StartsWith("modbot.copy.", StringComparison.Ordinal)
                || type == FactType.DiscordCommandRun,
            _ => true,
        };
    }

    /// <summary>Which of <see cref="All"/> a type is shown under, for the filter list.</summary>
    public static IReadOnlyList<string> Of(string type) => All.Where(show => Includes(show, type)).ToList();
}
