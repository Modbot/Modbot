namespace Modbot.Core.Data.Entities;

/// <summary>
/// A short human label for each fact type.
/// </summary>
/// <remarks>
/// <para>
/// Server-side rather than in the SPA because the type <em>list</em> is server-driven — it is
/// narrowed by permission before it is sent (spec 5.9.4) — and a browser-side label table would
/// have to be kept in step with an enum it cannot see. One list, in the same place as the
/// permission decision.
/// </para>
/// <para>
/// In Core rather than the API since Discord event routes: the bot titles each post with the same
/// words the settings page offers and the audit log shows.
/// </para>
/// <para>
/// The fallback is the enum name, not a placeholder. A type added without a label reads as
/// <c>InstanceLeft</c>, which is ugly and correct; anything friendlier would be inventing a
/// description of something nobody has described.
/// </para>
/// </remarks>
public static class FactLabels
{
    private static readonly Dictionary<string, string> Labels = new()
    {
        [FactType.MemberJoined] = "Joined the group",
        [FactType.MemberLeft] = "Left the group",
        [FactType.MemberBanned] = "Banned",
        [FactType.MemberUnbanned] = "Unbanned",
        [FactType.MemberKicked] = "Kicked",
        [FactType.RoleGranted] = "Role granted",
        [FactType.RoleRevoked] = "Role revoked",
        [FactType.InviteCreated] = "Invite created",
        [FactType.GroupInfoChanged] = "Group details changed",
        [FactType.MembersSnapshot] = "Member list first read",
        [FactType.BansSnapshot] = "Ban list first read",

        // VRChat's own wording in the audit log, made plain: "has issued an instance kick for",
        // "requested to join the group", "Calendar Entry created by".
        [FactType.RoleUpdated] = "Role changed",
        [FactType.JoinRequestCreated] = "Asked to join the group",
        [FactType.JoinRequestRejected] = "Join request rejected",
        [FactType.JoinRequestBlocked] = "Join request blocked",
        [FactType.GroupPostCreated] = "Group post created",
        [FactType.GroupPostDeleted] = "Group post deleted",
        [FactType.GroupInstanceCreated] = "Group instance created",
        [FactType.GroupInstanceClosed] = "Group instance closed",
        [FactType.GroupInstanceUpdated] = "Group instance changed",
        [FactType.GroupInstanceAnnouncement] = "Group instance announcement",
        [FactType.GroupInstanceKick] = "Kicked from an instance",
        [FactType.GroupInstanceWarn] = "Warned in an instance",
        [FactType.CalendarEventCreated] = "Calendar entry created",
        [FactType.CalendarEventDeleted] = "Calendar entry deleted",
        [FactType.CalendarEventSeriesUpdated] = "Recurring calendar entry changed",
        [FactType.CalendarEventSeriesDeleted] = "Recurring calendar entry deleted",

        // Profile sync. "18+ verified" is the wording VRChat's own UI uses.
        [FactType.UserProfileFirstSeen] = "Profile recorded for the first time",
        [FactType.UserProfileChanged] = "Profile changed",
        [FactType.UserProfileNotFound] = "Account no longer found on VRChat",
        [FactType.UserAgeVerified] = "Seen as 18+ verified",
        [FactType.UserAgeFlagSet] = "Marked 18+ verified by a moderator",
        [FactType.UserAgeFlagCleared] = "18+ verified flag cleared by a moderator",

        [FactType.InstanceJoined] = "Joined an instance",
        [FactType.InstanceLeft] = "Left an instance",
        [FactType.AvatarChanged] = "Changed avatar",
        [FactType.InstancePresenceObserved] = "Seen in an instance",
        [FactType.InstanceLogStopped] = "Stopped watching an instance",

        [FactType.DiscordMemberJoined] = "Joined Discord",
        [FactType.DiscordMemberLeft] = "Left Discord",
        [FactType.DiscordVoiceJoined] = "Joined a voice channel",
        [FactType.DiscordVoiceLeft] = "Left a voice channel",
        [FactType.DiscordRoleGranted] = "Discord role granted",
        [FactType.DiscordRoleRevoked] = "Discord role revoked",
        [FactType.DiscordMembersSnapshot] = "Discord member list first read",
        [FactType.DiscordMemberBanned] = "Banned from Discord",
        [FactType.DiscordMemberUnbanned] = "Unbanned from Discord",
        [FactType.DiscordMemberKicked] = "Kicked from Discord",
        [FactType.DiscordMemberTimedOut] = "Timed out on Discord",
        [FactType.DiscordMemberTimeoutRemoved] = "Discord timeout removed",
        [FactType.DiscordMemberNicknameChanged] = "Discord nickname changed",
        [FactType.DiscordVoiceMoved] = "Moved to another voice channel",
        [FactType.DiscordMessagesRemoved] = "Discord messages removed by a moderator",
        [FactType.DiscordMessagesBulkRemoved] = "Discord messages removed in bulk",
        [FactType.DiscordChannelCreated] = "Discord channel created",
        [FactType.DiscordChannelChanged] = "Discord channel changed",
        [FactType.DiscordChannelDeleted] = "Discord channel deleted",
        [FactType.DiscordRoleCreated] = "Discord role created",
        [FactType.DiscordRoleChanged] = "Discord role changed",
        [FactType.DiscordRoleDeleted] = "Discord role deleted",
        [FactType.DiscordLinkCreated] = "Discord account linked",
        [FactType.DiscordLinkRemoved] = "Discord account unlinked",
        [FactType.DiscordLinkRoleGranted] = "Discord role given for a link",
        [FactType.DiscordLinkRoleRemoved] = "Discord role taken away for a link",
        [FactType.DiscordLinkPrompted] = "Asked to link on Discord",

        [FactType.Login] = "Signed in",
        [FactType.LoginFailed] = "Failed sign-in",
        [FactType.PasswordChanged] = "Password changed",
        [FactType.UsernameChanged] = "Username changed",
        [FactType.ContactChanged] = "Contact details changed",
        [FactType.VRChatLinked] = "VRChat account linked",
        [FactType.UserCreated] = "Account created",
        [FactType.UserInvited] = "Invite link created",
        [FactType.UserInviteUsed] = "Invite link used",
        [FactType.UserInviteRevoked] = "Invite link taken back",
        [FactType.UserDisabled] = "Account disabled",
        [FactType.UserEnabled] = "Account enabled",
        [FactType.UserRolesChanged] = "Roles changed",
        [FactType.ResetLinkCreated] = "Reset link created",
        [FactType.ResetLinkUsed] = "Reset link used",
        [FactType.SignedOutEverywhere] = "Signed out everywhere",
        [FactType.RoleCreated] = "Role created",
        [FactType.RoleChanged] = "Role changed",
        [FactType.RoleDeleted] = "Role deleted",
        [FactType.ApiKeyCreated] = "API key created",
        [FactType.ApiKeyRevoked] = "API key revoked",
        [FactType.WebhookCreated] = "Webhook created",
        [FactType.WebhookChanged] = "Webhook changed",
        [FactType.WebhookSecretChanged] = "Webhook secret changed",
        [FactType.WebhookDeleted] = "Webhook deleted",
        [FactType.WebhookDisabled] = "Webhook turned off",
        [FactType.SettingsChanged] = "Settings changed",
        [FactType.PlannedEventCreated] = "Event planned",
        [FactType.PlannedEventChanged] = "Planned event changed",
        [FactType.PlannedEventCancelled] = "Planned event cancelled",
        [FactType.PlannedEventDeleted] = "Planned event deleted",
        [FactType.PlannedEventOpened] = "Planned event opened",
        [FactType.PlannedEventFinished] = "Planned event finished",
        [FactType.PlannedEventInstanceOpened] = "Instance opened for an event",
        [FactType.PlannedEventInstanceFailed] = "Instance for an event failed to open",
        [FactType.PlannedEventPublishFailed] = "Event failed to publish",
        [FactType.CalendarFeedRegenerated] = "Calendar feed link replaced",

        // Reviews of a moderator's pattern (spec 5.8.5). Both are about the moderator.
        [FactType.ReviewOpened] = "Review opened",
        [FactType.ReviewClosed] = "Review closed",

        // Case files (spec 5.8.3). About the person who was banned.
        [FactType.ReportCreated] = "Case file written",
        [FactType.ReportUpdated] = "Case file edited",
        [FactType.ReportWithdrawn] = "Case file withdrawn",
        [FactType.ReportSnapshotRecaptured] = "Case file profile snapshot taken again",
        [FactType.BanReasonsChanged] = "Ban reason list changed",

        [FactType.EvidenceAttached] = "Evidence attached",
        [FactType.EvidenceAccessed] = "Evidence opened",
        [FactType.EvidenceDestroyed] = "Evidence destroyed",

        [FactType.SyncFailed] = "Sync failed",
        [FactType.RateLimitColdStop] = "Rate-limit cold stop",
        [FactType.WafBlocked] = "Blocked by Cloudflare",
        [FactType.MigrationApplied] = "Migration applied",
        [FactType.RetentionPruned] = "Retention pruned",
        [FactType.PartitionCreated] = "Partition created",
        [FactType.AiLimitReached] = "AI spend limit reached",
        [FactType.InsightAlert] = "Unusual activity",
        [FactType.ChatLookup] = "Asked about in chat",
        [FactType.UserPurged] = "User data purged",
        [FactType.Unrecognised] = "Event Modbot has no name for yet",

        [FactType.DiscordCommandRun] = "Discord command used",
        [FactType.DiscordLogPosted] = "Posted to the Discord log channel",

        // AI moderation (AI moderation design §7).
        [FactType.AiModerationFlag] = "Flagged by a moderation rule",
        [FactType.AiModerationFlagDismissed] = "Moderation flag dismissed",
        [FactType.AiModerationMessageDeleted] = "Discord message deleted by a moderation rule",
        [FactType.AiModerationTimeout] = "Timed out on Discord by a moderation rule",
        [FactType.AiModerationRuleChanged] = "Moderation rule changed",
        [FactType.AiModerationRulePaused] = "Moderation rule paused itself",
        [FactType.AiAcknowledged] = "What is sent to the AI provider was confirmed",
    };

    public static string For(string type)
        => Labels.TryGetValue(type, out var label) ? label : type.ToString();
}
