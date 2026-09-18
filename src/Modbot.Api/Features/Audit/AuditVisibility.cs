using Modbot.Core.Data.Entities;
using System.Text.Json.Serialization;

namespace Modbot.Api.Features.Audit;

/// <summary>Which of the two logs a fact type belongs to (spec 5.9.2).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AuditCategory>))]
public enum AuditCategory
{
    /// <summary>Moderation history: who did what to whom. Gated on <c>ViewAuditLog</c>.</summary>
    Moderation = 1,

    /// <summary>
    /// Modbot's own operational record: logins, settings changes, sync failures, API keys.
    /// Gated on <c>ViewOperationalLog</c>.
    /// </summary>
    Operational = 2,
}

/// <summary>
/// Decides which facts a caller is allowed to see, from the permissions they hold.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.9.4: <c>ViewAuditLog</c> and <c>ViewOperationalLog</c> are distinct permissions, and
/// the split is not cosmetic — a moderator who should see bans and kicks does not automatically
/// need to see that the owner reconfigured SMTP or which API keys exist.
/// </para>
/// <para>
/// <strong>The filter is applied on every query, not only when the caller asks for a type.</strong>
/// The requested types are intersected with the visible set rather than validated against it, so
/// a request naming an operational type is answered with the moderation facts it was also
/// entitled to instead of a 403 — and a request naming nothing at all cannot fall through to
/// "everything". The SPA decides what to offer; this decides what is allowed.
/// </para>
/// <para>
/// Membership in a category is by explicit list rather than by numeric block. The blocks in
/// <see cref="FactType"/> are a retention grouping (spec 5.5), and retention class and
/// visibility class are different questions that happen to agree today — <c>UserPurged</c> takes
/// moderation retention because the record of a deletion must survive, and is nonetheless an
/// administrative action that belongs in the operational log.
/// </para>
/// </remarks>
public static class AuditVisibility
{
    private static readonly Dictionary<string, AuditCategory> Categories = new()
    {
        // Membership and moderation, from VRChat's audit log or inferred from a sync.
        [FactType.MemberJoined] = AuditCategory.Moderation,
        [FactType.MemberLeft] = AuditCategory.Moderation,
        [FactType.MemberBanned] = AuditCategory.Moderation,
        [FactType.MemberUnbanned] = AuditCategory.Moderation,
        [FactType.MemberKicked] = AuditCategory.Moderation,
        [FactType.RoleGranted] = AuditCategory.Moderation,
        [FactType.RoleRevoked] = AuditCategory.Moderation,
        [FactType.InviteCreated] = AuditCategory.Moderation,
        [FactType.GroupInfoChanged] = AuditCategory.Moderation,

        // The first read of each list: a headcount with a date on it, which is membership
        // history in the same sense the group-info baseline is.
        [FactType.MembersSnapshot] = AuditCategory.Moderation,
        [FactType.BansSnapshot] = AuditCategory.Moderation,

        // The rest of the group's audit log. Every one is something a person did in the group --
        // an instance kick is as much moderation as a group kick -- so every one is in the log a
        // moderator may read.
        [FactType.RoleUpdated] = AuditCategory.Moderation,
        [FactType.JoinRequestCreated] = AuditCategory.Moderation,
        [FactType.JoinRequestRejected] = AuditCategory.Moderation,
        [FactType.JoinRequestBlocked] = AuditCategory.Moderation,
        [FactType.GroupPostCreated] = AuditCategory.Moderation,
        [FactType.GroupPostDeleted] = AuditCategory.Moderation,
        [FactType.GroupInstanceCreated] = AuditCategory.Moderation,
        [FactType.GroupInstanceClosed] = AuditCategory.Moderation,
        [FactType.GroupInstanceUpdated] = AuditCategory.Moderation,
        [FactType.GroupInstanceAnnouncement] = AuditCategory.Moderation,
        [FactType.GroupInstanceKick] = AuditCategory.Moderation,
        [FactType.GroupInstanceWarn] = AuditCategory.Moderation,
        [FactType.CalendarEventCreated] = AuditCategory.Moderation,
        [FactType.CalendarEventDeleted] = AuditCategory.Moderation,
        [FactType.CalendarEventSeriesUpdated] = AuditCategory.Moderation,
        [FactType.CalendarEventSeriesDeleted] = AuditCategory.Moderation,

        // A person's profile over time, and what a moderator decided about their 18+ flag. Member
        // history, read by the same person asking what they were banned for. The manual flag
        // changes are Modbot's own actions and still belong here: they are moderation decisions
        // about a member, not plumbing.
        [FactType.UserProfileFirstSeen] = AuditCategory.Moderation,
        [FactType.UserProfileChanged] = AuditCategory.Moderation,
        [FactType.UserProfileNotFound] = AuditCategory.Moderation,
        [FactType.UserAgeVerified] = AuditCategory.Moderation,
        [FactType.UserAgeFlagSet] = AuditCategory.Moderation,
        [FactType.UserAgeFlagCleared] = AuditCategory.Moderation,

        // Presence, reported by a moderator's client. It is member history, not operations:
        // "where was this person" is asked by the same person asking "what were they banned for",
        // in the same timeline (spec 5.9.5).
        [FactType.InstanceJoined] = AuditCategory.Moderation,
        [FactType.InstanceLeft] = AuditCategory.Moderation,
        [FactType.AvatarChanged] = AuditCategory.Moderation,
        [FactType.InstancePresenceObserved] = AuditCategory.Moderation,
        [FactType.InstanceLogStopped] = AuditCategory.Moderation,

        // Discord, once the bot is a second fact source (spec 9.1).
        [FactType.DiscordMemberJoined] = AuditCategory.Moderation,
        [FactType.DiscordMemberLeft] = AuditCategory.Moderation,
        [FactType.DiscordVoiceJoined] = AuditCategory.Moderation,
        [FactType.DiscordVoiceLeft] = AuditCategory.Moderation,
        [FactType.DiscordRoleGranted] = AuditCategory.Moderation,
        [FactType.DiscordRoleRevoked] = AuditCategory.Moderation,
        [FactType.DiscordMembersSnapshot] = AuditCategory.Moderation,
        [FactType.DiscordMemberBanned] = AuditCategory.Moderation,
        [FactType.DiscordMemberUnbanned] = AuditCategory.Moderation,
        [FactType.DiscordMemberKicked] = AuditCategory.Moderation,
        [FactType.DiscordMemberTimedOut] = AuditCategory.Moderation,
        [FactType.DiscordMemberTimeoutRemoved] = AuditCategory.Moderation,
        [FactType.DiscordMemberNicknameChanged] = AuditCategory.Moderation,
        [FactType.DiscordVoiceMoved] = AuditCategory.Moderation,
        [FactType.DiscordMessagesRemoved] = AuditCategory.Moderation,
        [FactType.DiscordMessagesBulkRemoved] = AuditCategory.Moderation,
        [FactType.DiscordChannelCreated] = AuditCategory.Moderation,
        [FactType.DiscordChannelChanged] = AuditCategory.Moderation,
        [FactType.DiscordChannelDeleted] = AuditCategory.Moderation,
        [FactType.DiscordRoleCreated] = AuditCategory.Moderation,
        [FactType.DiscordRoleChanged] = AuditCategory.Moderation,
        [FactType.DiscordRoleDeleted] = AuditCategory.Moderation,

        // Linking a member's accounts, and the roles that follow, are member history. The prompt
        // sent to a new member is plumbing.
        [FactType.DiscordLinkCreated] = AuditCategory.Moderation,
        [FactType.DiscordLinkRemoved] = AuditCategory.Moderation,
        [FactType.DiscordLinkRoleGranted] = AuditCategory.Moderation,
        [FactType.DiscordLinkRoleRemoved] = AuditCategory.Moderation,
        [FactType.DiscordLinkPrompted] = AuditCategory.Operational,

        // Auth and config: spec 5.9.2's first two rows, and the reason the split exists.
        [FactType.Login] = AuditCategory.Operational,
        [FactType.LoginFailed] = AuditCategory.Operational,
        [FactType.PasswordChanged] = AuditCategory.Operational,

        // Staff accounts and roles (accounts and access design §6): the "Auth" row throughout.
        // Who was given which role and who was disabled is history worth keeping, and it is
        // administration rather than moderation, so it sits in the log gated on the operational
        // permission.
        [FactType.UsernameChanged] = AuditCategory.Operational,
        [FactType.ContactChanged] = AuditCategory.Operational,
        [FactType.VRChatLinked] = AuditCategory.Operational,
        [FactType.UserCreated] = AuditCategory.Operational,
        [FactType.UserInvited] = AuditCategory.Operational,
        [FactType.UserInviteUsed] = AuditCategory.Operational,
        [FactType.UserInviteRevoked] = AuditCategory.Operational,
        [FactType.UserDisabled] = AuditCategory.Operational,
        [FactType.UserEnabled] = AuditCategory.Operational,
        [FactType.UserRolesChanged] = AuditCategory.Operational,
        [FactType.ResetLinkCreated] = AuditCategory.Operational,
        [FactType.ResetLinkUsed] = AuditCategory.Operational,
        [FactType.SignedOutEverywhere] = AuditCategory.Operational,
        [FactType.UpdatesSubscribed] = AuditCategory.Operational,
        [FactType.RoleCreated] = AuditCategory.Operational,
        [FactType.RoleChanged] = AuditCategory.Operational,
        [FactType.RoleDeleted] = AuditCategory.Operational,
        [FactType.ApiKeyCreated] = AuditCategory.Operational,
        [FactType.ApiKeyRevoked] = AuditCategory.Operational,
        [FactType.McpConnected] = AuditCategory.Operational,
        [FactType.McpDisconnected] = AuditCategory.Operational,
        [FactType.WebhookCreated] = AuditCategory.Operational,
        [FactType.WebhookChanged] = AuditCategory.Operational,
        [FactType.WebhookSecretChanged] = AuditCategory.Operational,
        [FactType.WebhookDeleted] = AuditCategory.Operational,
        [FactType.WebhookDisabled] = AuditCategory.Operational,
        [FactType.SettingsChanged] = AuditCategory.Operational,
        [FactType.PlannedEventCreated] = AuditCategory.Operational,
        [FactType.PlannedEventChanged] = AuditCategory.Operational,
        [FactType.PlannedEventCancelled] = AuditCategory.Operational,
        [FactType.PlannedEventDeleted] = AuditCategory.Operational,
        [FactType.PlannedEventOpened] = AuditCategory.Operational,
        [FactType.PlannedEventFinished] = AuditCategory.Operational,
        [FactType.PlannedEventInstanceOpened] = AuditCategory.Operational,
        [FactType.PlannedEventInstanceFailed] = AuditCategory.Operational,
        [FactType.PlannedEventPublishFailed] = AuditCategory.Operational,
        [FactType.CalendarFeedRegenerated] = AuditCategory.Operational,

        // System: operational noise, and the row spec 5.9.2 gives the short retention class.
        [FactType.SyncFailed] = AuditCategory.Operational,
        [FactType.RateLimitColdStop] = AuditCategory.Operational,
        [FactType.WafBlocked] = AuditCategory.Operational,
        [FactType.MigrationApplied] = AuditCategory.Operational,
        [FactType.RetentionPruned] = AuditCategory.Operational,
        [FactType.PartitionCreated] = AuditCategory.Operational,
        [FactType.AiLimitReached] = AuditCategory.Operational,
        [FactType.UserPurged] = AuditCategory.Operational,
        [FactType.InsightAlert] = AuditCategory.Operational,

        // A review of a moderator's pattern being opened and closed is moderation history about
        // that moderator (spec 5.8.5: resolutions are recorded as facts, so "this was reviewed and
        // found legitimate" is itself history). It belongs in the log the team reads.
        [FactType.ReviewOpened] = AuditCategory.Moderation,
        [FactType.ReviewClosed] = AuditCategory.Moderation,

        // The Discord bot. Who used a command is an access record, and what it posted is
        // plumbing; both belong to the operator's log rather than the member timeline.
        [FactType.DiscordCommandRun] = AuditCategory.Operational,
        [FactType.DiscordLogPosted] = AuditCategory.Operational,

        // A case file is the group's written record of why somebody was banned (spec 5.8.3). Its
        // writing, editing and withdrawal are moderation history about the banned person and sit
        // beside the ban in the same timeline. The reason list is a setting, and goes with the
        // other settings changes.
        [FactType.ReportCreated] = AuditCategory.Moderation,
        [FactType.ReportUpdated] = AuditCategory.Moderation,
        [FactType.ReportWithdrawn] = AuditCategory.Moderation,
        [FactType.ReportSnapshotRecaptured] = AuditCategory.Moderation,
        [FactType.BanReasonsChanged] = AuditCategory.Operational,

        // A kick, ban or unban pressed in Modbot is moderation history about the person it was
        // done to, and belongs in their timeline beside VRChat's own record of it. A refused one
        // is too: "this was attempted and did not happen" is exactly what a moderator who thinks
        // they banned somebody needs to be able to find.
        [FactType.ActionKick] = AuditCategory.Moderation,
        [FactType.ActionBan] = AuditCategory.Moderation,
        [FactType.ActionUnban] = AuditCategory.Moderation,
        [FactType.ActionFailed] = AuditCategory.Moderation,

        // Looking somebody up through Chat is the same kind of access record as opening a piece of
        // evidence, and it belongs in the timeline of the person who was looked at rather than in
        // the operator's log: a moderator reading their own history should see who has been asking
        // about them.
        [FactType.ChatLookup] = AuditCategory.Moderation,

        // Evidence is moderation history, not plumbing: who attached what to a case, who opened
        // it, and who destroyed it are all part of the accountability record spec 5.8 exists for.
        [FactType.EvidenceAttached] = AuditCategory.Moderation,
        [FactType.EvidenceAccessed] = AuditCategory.Moderation,
        [FactType.EvidenceDestroyed] = AuditCategory.Moderation,

        // AutoMod. What a rule flagged, dismissed or did to somebody is moderation history about
        // that person; a rule being changed is a setting, and goes with the other settings.
        [FactType.AutoModFlag] = AuditCategory.Moderation,
        [FactType.AutoModFlagDismissed] = AuditCategory.Moderation,
        [FactType.AutoModFlagConfirmed] = AuditCategory.Moderation,
        [FactType.AutoModMessageDeleted] = AuditCategory.Moderation,
        [FactType.AutoModTimeout] = AuditCategory.Moderation,
        [FactType.AutoModGroupBan] = AuditCategory.Moderation,
        [FactType.AutoModGroupRemove] = AuditCategory.Moderation,
        [FactType.AutoModRuleChanged] = AuditCategory.Operational,
        [FactType.AutoModRulePaused] = AuditCategory.Operational,
        [FactType.AiAcknowledged] = AuditCategory.Operational,

        // An upstream event Modbot has no name for yet. Its TypeRaw comes from the group's own
        // audit log, which is moderation history by definition -- an instance kick Modbot does
        // not map is still something a moderator did to somebody. The Operational default exists
        // for Modbot's internal events, and this is not one of those.
        [FactType.Unrecognised] = AuditCategory.Moderation,

        // A note is moderation history about the person it was written about. An import
        // finishing is the operator's business, like a settings change (import design §3.3).
        [FactType.NoteAdded] = AuditCategory.Moderation,
        [FactType.ImportDone] = AuditCategory.Operational,
    };

    /// <summary>
    /// Which log a type belongs to.
    /// </summary>
    /// <remarks>
    /// A type absent from the table is <see cref="AuditCategory.Operational"/> — the more
    /// restricted of the two. A fact type added without a line here is then invisible to a plain
    /// moderator rather than silently readable by one, which is the direction a mistake here
    /// should fail in. <c>AuditVisibilityTests</c> pins the table against the enum so the
    /// omission is caught by a test rather than by a person.
    /// </remarks>
    public static AuditCategory CategoryOf(string type)
        => Categories.TryGetValue(type, out var category) ? category : AuditCategory.Operational;

    public static bool CanSee(ModbotPermissions held, AuditCategory category)
    {
        if (held.HasFlag(ModbotPermissions.Administrator))
            return true;

        return category is AuditCategory.Moderation
            ? held.HasFlag(ModbotPermissions.ViewAuditLog)
            : held.HasFlag(ModbotPermissions.ViewOperationalLog);
    }

    /// <summary>Every fact type this caller may read. Empty means they may read none.</summary>
    public static IReadOnlyList<string> VisibleTypes(ModbotPermissions held)
        => Categories
            .Where(pair => CanSee(held, pair.Value))
            .Select(pair => pair.Key)
            .Order()
            .ToList();

    /// <summary>
    /// The types to actually query: what was asked for, narrowed to what is permitted.
    /// </summary>
    /// <param name="requested">
    /// Null or empty means "whatever I am allowed to see", which is the merged default
    /// timeline of spec 5.9.5.
    /// </param>
    public static IReadOnlyList<string> Resolve(
        ModbotPermissions held,
        IReadOnlyCollection<string>? requested)
    {
        var visible = VisibleTypes(held);

        if (requested is null || requested.Count == 0)
            return visible;

        var allowed = visible.ToHashSet();

        return requested.Where(allowed.Contains).Distinct().Order().ToList();
    }
}
