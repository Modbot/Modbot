namespace Modbot.Core.Data.Entities;

/// <summary>
/// What happened, as a hierarchical <c>platform.domain.action</c> string.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.3 and 5.9.2. <strong>These were a <c>smallint</c> enum until 2026-09-13.</strong> The enum
/// was smaller on disk and gave exhaustive <c>switch</c> checking, and it cost more than it bought.
/// </para>
/// <para>
/// The deciding failure was not inconvenience. VRChat types its own audit-log <c>eventType</c> as a
/// free-form string — <c>group.member.user.ban</c> — and Modbot translated those into numbers. An
/// event type with no number was <em>counted and not recorded</em>, and the cursor advanced past it.
/// VRChat's audit log has its own retention, so those entries were gone for good. §5.1's entire
/// argument is that history cannot be filled in later; the enum turned "Modbot does not understand this
/// yet" into "this never happened", which is the one outcome the fact log exists to prevent.
/// </para>
/// <para>
/// Around that sat the ordinary friction: <c>GroupInfoChanged</c> and
/// <c>InstancePresenceObserved</c> both had to be appended by hand before a producer could record
/// anything, one of them shipping for a while as a <c>(FactType)203</c> cast; evidence access had no
/// member at all and so was not recorded; and a whole vocabulary-check machinery (§4.3.4.2, since
/// withdrawn) existed to detect a mapping whose spelling is wrong. Strings remove the translation
/// step that created all of it.
/// </para>
/// <para>
/// <strong>Storage was never the real argument.</strong> Measured: a fact costs 326 bytes including
/// indexes, and the longest name here adds about 22 bytes to the column and again to each of the two
/// composite indexes it appears in — roughly 20%. For a typical group that is 240 MB a year becoming
/// 290 MB. At the largest scale Modbot targets it is about fifteen cents a month.
/// </para>
/// <para>
/// <strong>These are <c>const string</c>, deliberately.</strong> Every existing
/// <c>FactType.MemberBanned</c> expression still compiles, and constants still work in
/// <c>switch</c> patterns — so the move cost one rewritten method rather than three hundred edits.
/// Validation belongs at the write boundary (<c>FactWriter</c>), not in a wrapper type, because the
/// entire point is that a value Modbot has never seen is legitimate and must be storable.
/// </para>
/// <para>
/// <strong>Naming rules.</strong> Lowercase, dot-separated, most general segment first. The first
/// segment is the system the fact is about — <c>vrchat</c>, <c>discord</c>, <c>modbot</c> — which
/// makes retention classification a prefix test rather than a table somebody must remember to
/// update. Prefer the upstream system's own word for the action (<c>remove</c>, not <c>kick</c>,
/// because that is what VRChat calls it) so the mapping stays near-identity and a reader comparing
/// the two is not translating in their head.
/// </para>
/// <para>
/// <strong>Renaming one of these is a data migration</strong>, exactly as renumbering the enum was.
/// Adding one is not: an unrecognised value stores and reads back fine, which is the property this
/// change was made for.
/// </para>
/// </remarks>
public static class FactType
{
    // ── VRChat: membership and moderation ──────────────────────────────────────────────────
    public const string MemberJoined = "vrchat.group.member.join";
    public const string MemberLeft = "vrchat.group.member.leave";
    public const string MemberBanned = "vrchat.group.member.ban";
    public const string MemberUnbanned = "vrchat.group.member.unban";

    /// <summary>Removed from the group by a moderator — VRChat's own word for a kick.</summary>
    public const string MemberKicked = "vrchat.group.member.remove";

    public const string RoleGranted = "vrchat.group.role.assign";
    public const string RoleRevoked = "vrchat.group.role.unassign";
    public const string InviteCreated = "vrchat.group.invite.create";

    /// <summary>
    /// The managed group's own metadata changed. The subject is the <em>group</em>, one of the
    /// three fact types for which that is true.
    /// </summary>
    public const string GroupInfoChanged = "vrchat.group.update";

    /// <summary>
    /// The first full sweep of the member list: how many members there were, and when. The
    /// subject is the group.
    /// </summary>
    /// <remarks>
    /// One fact, not one join per member. Recording 4,700 "joined" facts on the first sweep would
    /// say 4,700 people joined on the day Modbot was installed, which is false and would put a
    /// spike in every chart forever (member and ban sync design §4).
    /// </remarks>
    public const string MembersSnapshot = "vrchat.group.members.snapshot";

    /// <summary>The first full sweep of the ban list. Same reasoning as <see cref="MembersSnapshot"/>.</summary>
    public const string BansSnapshot = "vrchat.group.bans.snapshot";

    // ── VRChat: everything else the group's audit log records ──────────────────────────────
    //
    // All of these are things somebody did in the group, so all are moderation history: kept
    // forever (retention is a prefix test, and vrchat.group.* is not a presence prefix) and shown
    // to anyone who may read the audit log. The subject is whatever VRChat put in targetId --
    // documented only as "typically a UserID, GroupID, GroupRoleID, or Location" -- carried
    // through untouched and never parsed (spec 3.1.1).

    /// <summary>A role's name, permissions or settings changed. The subject is probably the role.</summary>
    public const string RoleUpdated = "vrchat.group.role.update";

    public const string JoinRequestCreated = "vrchat.group.request.create";
    public const string JoinRequestRejected = "vrchat.group.request.reject";
    public const string JoinRequestBlocked = "vrchat.group.request.block";

    public const string GroupPostCreated = "vrchat.group.post.create";
    public const string GroupPostDeleted = "vrchat.group.post.delete";

    /// <summary>
    /// A group instance was opened, closed, changed or announced into. The subject is probably a
    /// location. <strong>Not</strong> <c>vrchat.instance.*</c>: that prefix is a client's presence
    /// report and ages out; these are group moderation and do not.
    /// </summary>
    public const string GroupInstanceCreated = "vrchat.group.instance.create";
    public const string GroupInstanceClosed = "vrchat.group.instance.close";
    public const string GroupInstanceUpdated = "vrchat.group.instance.update";
    public const string GroupInstanceAnnouncement = "vrchat.group.instance.announcement";

    /// <summary>
    /// Ejected from a group instance -- separate from <see cref="MemberKicked"/>, which is removal
    /// from the group. Folding them together would blend two different actions into one count.
    /// </summary>
    public const string GroupInstanceKick = "vrchat.group.instance.kick";
    public const string GroupInstanceWarn = "vrchat.group.instance.warn";

    public const string CalendarEventCreated = "vrchat.group.calendar-event.create";
    public const string CalendarEventDeleted = "vrchat.group.calendar-event.delete";
    public const string CalendarEventSeriesUpdated = "vrchat.group.calendar-event.series.update";
    public const string CalendarEventSeriesDeleted = "vrchat.group.calendar-event.series.delete";

    // ── VRChat: user profiles (user profile sync design) ───────────────────────────────────
    //
    // Written by the profile sync, which fetches one user at a time from users.read. The subject
    // is always the VRChat user; there is never an actor, because a profile says what a person
    // looks like and nothing about who changed it. Kept forever: a bio as it stood when a ban was
    // issued is exactly the kind of history that cannot be filled in later.

    /// <summary>
    /// The first time this user's profile was fetched. Carries a small baseline -- display name,
    /// age verification status, join date -- so the timeline has a starting point to diff from.
    /// </summary>
    public const string UserProfileFirstSeen = "vrchat.user.profile.first-seen";

    /// <summary>
    /// A refresh found something different from last time. The payload is
    /// <c>{changed: {field: {old, new}}}</c>, the same shape the audit-log mapper lifts under
    /// <c>changed</c>, so one reader of the timeline meets one diff shape.
    /// </summary>
    public const string UserProfileChanged = "vrchat.user.profile.changed";

    /// <summary>VRChat answered 404 for a user it used to know -- usually a deleted account.</summary>
    public const string UserProfileNotFound = "vrchat.user.profile.not-found";

    /// <summary>
    /// A refresh saw VRChat report the user as 18+ verified for the first time. Written once per
    /// user; the flag it sets is sticky and a later "hidden" writes nothing.
    /// </summary>
    public const string UserAgeVerified = "vrchat.user.age-verified";

    // ── Modbot: moderator overrides on a VRChat user's record ──────────────────────────────

    /// <summary>A moderator marked the user as 18+ verified by hand. The actor is the moderator.</summary>
    public const string UserAgeFlagSet = "modbot.user-profile.age-flag.set";

    /// <summary>
    /// A moderator cleared the 18+ flag. The only way it is ever cleared -- a sync cannot -- so
    /// this fact is the whole audit trail of that decision, and it always names who made it.
    /// </summary>
    public const string UserAgeFlagCleared = "modbot.user-profile.age-flag.cleared";

    // ── VRChat: presence ───────────────────────────────────────────────────────────────────
    public const string InstanceJoined = "vrchat.instance.join";
    public const string InstanceLeft = "vrchat.instance.leave";
    public const string AvatarChanged = "vrchat.avatar.change";

    /// <summary>
    /// This person was already here when the reporting client arrived — arrival time unknown and
    /// earlier.
    /// </summary>
    /// <remarks>
    /// Not a weaker <see cref="InstanceJoined"/> but a different claim. VRChat emits
    /// <c>OnPlayerJoined</c> for everyone already present whenever anybody enters, so recording
    /// those as arrivals invents a join for every occupant every time a moderator walks into a
    /// room — silently, since nothing errors and the numbers are simply wrong. Carries
    /// <c>occurred_before</c> rather than a point in time.
    /// </remarks>
    public const string InstancePresenceObserved = "vrchat.instance.presence";

    /// <summary>
    /// A moderator's client reported that VRChat's log stopped while they were in this instance.
    /// The subject is the moderator; the time is the last line the log wrote.
    /// </summary>
    /// <remarks>
    /// Not a leave. Whether the moderator is still standing there is unknown -- VRChat may have
    /// crashed, or the machine slept -- only that their client can no longer see the room. It ends
    /// that moderator's watch (<c>RoomWatching</c>), so nobody they last saw stays listed as present.
    /// Sent once per stop, never as a repeating "still here". Presence class, by prefix.
    /// </remarks>
    public const string InstanceLogStopped = "vrchat.instance.log-stopped";

    // ── Discord (M5) ───────────────────────────────────────────────────────────────────────
    //
    // Everything here has subject_platform = Discord and, where somebody did it, an actor on
    // Discord too. Moderation actions -- bans, kicks, timeouts, role changes, messages removed by a
    // moderator -- come from the server's audit log when the bot may read it, because only the audit
    // log says who did it, and carry its entry id as `auditEntryId`. Without that permission they
    // come from the gateway's own events, with no actor. Voice is presence and ages out with it;
    // the rest is membership and moderation history and is kept.

    public const string DiscordMemberJoined = "discord.member.join";
    public const string DiscordMemberLeft = "discord.member.leave";

    /// <summary>
    /// The first full read of the server's member list: how many members there were. The subject is
    /// the server. One fact rather than a join per member, for the reason <see cref="MembersSnapshot"/> gives.
    /// </summary>
    public const string DiscordMembersSnapshot = "discord.members.snapshot";

    public const string DiscordMemberBanned = "discord.member.ban";
    public const string DiscordMemberUnbanned = "discord.member.unban";
    public const string DiscordMemberKicked = "discord.member.kick";

    /// <summary>Timed out, or a timeout changed. Payload: <c>until</c>.</summary>
    public const string DiscordMemberTimedOut = "discord.member.timeout";

    /// <summary>A timeout taken off before it ran out.</summary>
    public const string DiscordMemberTimeoutRemoved = "discord.member.timeout.remove";

    /// <summary>Their server nickname changed. Payload: <c>old</c> and <c>new</c>.</summary>
    public const string DiscordMemberNicknameChanged = "discord.member.nickname";

    public const string DiscordVoiceJoined = "discord.voice.join";
    public const string DiscordVoiceLeft = "discord.voice.leave";

    /// <summary>Moved from one voice channel to another without leaving. Payload: <c>from</c> and <c>channelId</c>.</summary>
    public const string DiscordVoiceMoved = "discord.voice.move";

    public const string DiscordRoleGranted = "discord.role.assign";
    public const string DiscordRoleRevoked = "discord.role.unassign";

    // ── Discord account linking (Discord account linking design §9) ────────────────────────

    /// <summary>
    /// A Discord account and a VRChat account were proved to be the same person. Subject is the
    /// VRChat user, so it sits in their history; actor is the Discord account. Payload: Discord id
    /// and name, VRChat display name, which side they started from, the link it replaced.
    /// </summary>
    public const string DiscordLinkCreated = "discord.link.create";

    /// <summary>A link ended. Subject is the VRChat user. Payload: Discord id, and who ended it.</summary>
    public const string DiscordLinkRemoved = "discord.link.remove";

    /// <summary>Modbot gave a linked member a role. Subject is the Discord account; no actor.</summary>
    public const string DiscordLinkRoleGranted = "discord.link.role.grant";

    /// <summary>Modbot took away a role it had given. Subject is the Discord account; no actor.</summary>
    public const string DiscordLinkRoleRemoved = "discord.link.role.remove";

    /// <summary>
    /// A new member was sent the link prompt. Subject is the Discord account. Payload: <c>dm</c>,
    /// <c>channel</c> or <c>none</c>, and the error when there was one. Short retention: plumbing.
    /// </summary>
    public const string DiscordLinkPrompted = "discord.link.prompt";

    /// <summary>
    /// A moderator deleted somebody's messages. The subject is the author, the actor the moderator.
    /// Payload: <c>channelId</c> and <c>count</c>. The messages themselves stay stored, marked deleted.
    /// </summary>
    public const string DiscordMessagesRemoved = "discord.message.remove";

    /// <summary>A moderator deleted many messages at once. The subject is the channel. Payload: <c>count</c>.</summary>
    public const string DiscordMessagesBulkRemoved = "discord.message.bulk-remove";

    // Channels and roles changed in Discord, from the audit log. The subject is the channel or role;
    // the channel and role lists themselves are kept by the server index, not by these.
    public const string DiscordChannelCreated = "discord.channel.create";
    public const string DiscordChannelChanged = "discord.channel.update";
    public const string DiscordChannelDeleted = "discord.channel.delete";
    public const string DiscordRoleCreated = "discord.role.create";
    public const string DiscordRoleChanged = "discord.role.update";
    public const string DiscordRoleDeleted = "discord.role.delete";

    // ── Modbot's Discord bot (foundation §9) ───────────────────────────────────────────────

    /// <summary>
    /// Somebody ran one of the bot's slash commands. Subject is the Discord user; actor is the
    /// Modbot account it is linked to, when there is one. Payload: the command, whether it was
    /// answered or refused, and the VRChat user it looked up if any. Kept forever, like evidence
    /// access: "who looked at whom" is an access record, not noise.
    /// </summary>
    public const string DiscordCommandRun = "modbot.discord.command";

    /// <summary>
    /// The bot posted a batch of moderation events to the log channel. Subject is the channel.
    /// Payload: how many, and the first and last fact id. Presence class -- it is plumbing.
    /// </summary>
    public const string DiscordLogPosted = "modbot.discord.posted";

    // ── Modbot's own audit entries (spec 5.9.2) ────────────────────────────────────────────

    // Staff accounts (accounts and access design §6). Every one is the "Auth" row of spec
    // 5.9.2: operational log, moderation retention. The subject is the account; the actor is
    // whoever did it. These three were modbot.auth.* until 2026-09-13; nothing had written them,
    // so the rename was not a data migration.
    public const string Login = "modbot.user.login";

    /// <summary>Records the username attempted and the caller's address. Never the password.</summary>
    public const string LoginFailed = "modbot.user.login.failed";

    public const string PasswordChanged = "modbot.user.password.change";

    /// <summary>Payload carries the old and new names.</summary>
    public const string UsernameChanged = "modbot.user.username.change";

    /// <summary>Email or Discord user id set. Payload says which fields changed, not the values.</summary>
    public const string ContactChanged = "modbot.user.contact.change";

    /// <summary>The person proved which VRChat account is theirs (design §4.3).</summary>
    public const string VRChatLinked = "modbot.user.vrchat.link";

    public const string UserCreated = "modbot.user.create";
    public const string UserInvited = "modbot.user.invite.create";
    public const string UserInviteUsed = "modbot.user.invite.use";
    public const string UserInviteRevoked = "modbot.user.invite.revoke";
    public const string UserDisabled = "modbot.user.disable";
    public const string UserEnabled = "modbot.user.enable";
    public const string UserRolesChanged = "modbot.user.roles.change";
    public const string ResetLinkCreated = "modbot.user.password.reset.create";
    public const string ResetLinkUsed = "modbot.user.password.reset.use";
    public const string SignedOutEverywhere = "modbot.user.sign-out-everywhere";

    public const string RoleCreated = "modbot.role.create";
    public const string RoleChanged = "modbot.role.change";
    public const string RoleDeleted = "modbot.role.delete";

    public const string ApiKeyCreated = "modbot.apikey.create";
    public const string ApiKeyRevoked = "modbot.apikey.revoke";

    // Webhooks (API keys design §6.8). The subject is the webhook's id on the Modbot platform. The
    // payload names the webhook and its address, never the secret.
    public const string WebhookCreated = "modbot.webhook.create";
    public const string WebhookChanged = "modbot.webhook.change";
    public const string WebhookSecretChanged = "modbot.webhook.secret.change";
    public const string WebhookDeleted = "modbot.webhook.delete";

    /// <summary>Modbot turned a webhook off by itself: failing for a day, or its owner disabled. No actor.</summary>
    public const string WebhookDisabled = "modbot.webhook.disable";
    public const string SettingsChanged = "modbot.settings.change";

    // ── Modbot's own calendar (calendar design §8). The subject is the event's id. ──────────
    //
    // "Planned event" in the names, because the CalendarEvent* names above are VRChat's own
    // audit-log entries about its calendar -- a different record from a different source.
    public const string PlannedEventCreated = "modbot.calendar.event.create";
    public const string PlannedEventChanged = "modbot.calendar.event.change";
    public const string PlannedEventCancelled = "modbot.calendar.event.cancel";
    public const string PlannedEventDeleted = "modbot.calendar.event.delete";

    /// <summary>An occurrence opened. Written by the scheduler; no actor.</summary>
    public const string PlannedEventOpened = "modbot.calendar.event.open";

    /// <summary>The last occurrence ended. Written by the scheduler; no actor.</summary>
    public const string PlannedEventFinished = "modbot.calendar.event.finish";

    public const string PlannedEventInstanceOpened = "modbot.calendar.instance.open";
    public const string PlannedEventInstanceFailed = "modbot.calendar.instance.fail";
    public const string PlannedEventPublishFailed = "modbot.calendar.publish.fail";
    public const string CalendarFeedRegenerated = "modbot.calendar.feed.regenerate";

    // ── Reviews of a moderator's pattern (spec 5.8.5, accountability signals design) ───────
    //
    // The subject is the moderator being reviewed, on the VRChat platform, because the review is
    // about their VRChat actions and shows in their history. Moderation retention: "this was
    // reviewed and found fine" is itself history (spec 5.8.5).

    /// <summary>
    /// Detection found a pattern worth a human look and opened a review. No actor: Modbot
    /// opened it. The payload carries the signal, the summary sentence and the evidence.
    /// </summary>
    public const string ReviewOpened = "modbot.review.opened";

    /// <summary>A person closed a review, with a note. The actor is the Modbot account.</summary>
    public const string ReviewClosed = "modbot.review.closed";

    // ── Case files (spec 5.8.3, ban case files design) ─────────────────────────────────────
    //
    // The subject is the person who was banned, on the VRChat platform, so the case file shows in
    // their history next to the ban itself. The actor is always the Modbot account that did it
    // (spec 5.9.1: attribution lives here and nowhere else). Every one carries the case file id
    // and enough of the content that the edit history can be read back from the log alone.

    /// <summary>A moderator wrote up a ban. Payload: reasons, the written reason, and when the snapshot was taken.</summary>
    public const string ReportCreated = "modbot.report.created";

    /// <summary>The reasons or the written reason changed. Payload: before and after.</summary>
    public const string ReportUpdated = "modbot.report.updated";

    /// <summary>The case file was marked withdrawn, with a note. The row stays.</summary>
    public const string ReportWithdrawn = "modbot.report.withdrawn";

    /// <summary>
    /// The profile snapshot was taken again after a fresher profile arrived. Payload carries the
    /// snapshot it replaced in full, so the first capture is never lost.
    /// </summary>
    public const string ReportSnapshotRecaptured = "modbot.report.snapshot.recaptured";

    /// <summary>The ban reason list changed: a reason added, reworded, switched off or reordered.</summary>
    public const string BanReasonsChanged = "modbot.ban-reasons.change";

    // ── Evidence (evidence design §6, §14.1) ───────────────────────────────────────────────

    /// <summary>Evidence was attached to a case file.</summary>
    public const string EvidenceAttached = "modbot.evidence.attach";

    /// <summary>
    /// Somebody opened a piece of evidence.
    /// </summary>
    /// <remarks>
    /// Evidence may be video of the person a ban was applied to, so who looked at it is part of the
    /// accountability record rather than incidental. Moderation retention: the point of recording
    /// an access is that it is still answerable long afterwards.
    /// </remarks>
    public const string EvidenceAccessed = "modbot.evidence.access";

    /// <summary>
    /// The bytes were destroyed; the record that they existed was not.
    /// </summary>
    /// <remarks>
    /// "This case had a video and an administrator destroyed it on this date" has to stay
    /// answerable forever — a case file that looks like it never had evidence is indistinguishable
    /// from one nobody documented.
    /// </remarks>
    public const string EvidenceDestroyed = "modbot.evidence.destroy";

    // ── AI moderation (AI moderation design §7) ────────────────────────────────────────────
    //
    // A flag and a dismissal are about the person who wrote the text, on the platform they wrote
    // it on. The two actions have no actor, because nobody pressed a button at that moment; the
    // operator who set the rule to act is named in the data (M8 §2). A rule change is about the
    // Modbot account that made it. All are kept: "who switched this rule to delete messages" is
    // history.

    public const string AiModerationFlag = "modbot.ai-moderation.flag";

    public const string AiModerationFlagDismissed = "modbot.ai-moderation.flag.dismiss";

    public const string AiModerationMessageDeleted = "modbot.ai-moderation.message-delete";

    public const string AiModerationTimeout = "modbot.ai-moderation.timeout";

    public const string AiModerationRuleChanged = "modbot.ai-moderation.rule.change";

    // ── Modbot operational events ──────────────────────────────────────────────────────────
    public const string SyncFailed = "modbot.sync.failed";
    public const string RateLimitColdStop = "modbot.ratelimit.coldstop";
    public const string WafBlocked = "modbot.waf.blocked";
    public const string MigrationApplied = "modbot.migration.applied";
    public const string RetentionPruned = "modbot.retention.pruned";
    public const string PartitionCreated = "modbot.partition.created";

    /// <summary>
    /// An AI spend limit was reached (AI chat design §10.7). Recorded once per limit per UTC day or
    /// month, the first time a call is stopped by it. Subject is the limit. Payload: which limit, the
    /// period, the amount and what had been spent.
    /// </summary>
    public const string AiLimitReached = "modbot.ai.limit.reached";

    /// <summary>
    /// Something Modbot watches ran far outside this deployment's own normal (AI insights design
    /// §8). Subject is the watcher. Payload: the figure, the window, what normal looks like, and
    /// where in Modbot to look. Never a person: an alert is counts, not a report about anyone.
    /// </summary>
    public const string InsightAlert = "modbot.insight.alert";

    /// <summary>
    /// Every fact about one subject was erased on request (spec 5.5). It deliberately names no
    /// user.
    /// </summary>
    public const string UserPurged = "modbot.user.purged";

    /// <summary>
    /// Something happened that Modbot does not yet have a name for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reason this whole type is strings. An upstream event with no mapping is recorded under
    /// this type with the source's own wording preserved in <c>ModbotEvent.TypeRaw</c>, instead of
    /// being dropped. Modbot's understanding of it can be added later; the event itself cannot be
    /// fetched again, because VRChat's audit log ages out.
    /// </para>
    /// <para>
    /// Moderation retention, like every unprefixed default — an event nobody has classified is
    /// exactly the one not to delete on a guess.
    /// </para>
    /// </remarks>
    public const string Unrecognised = "modbot.unrecognised";

    /// <summary>
    /// Every type declared here, read from the constants so it cannot drift from them.
    /// </summary>
    /// <remarks>
    /// Deliberately <em>not</em> the set of types that can exist in the log — an unrecognised
    /// upstream event is stored under <see cref="Unrecognised"/> and is as real as any of these.
    /// This is the list Modbot has names and labels for.
    /// </remarks>
    public static IReadOnlyList<string> All { get; } = typeof(FactType)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();

    /// <summary>
    /// Whether a value is a well-formed fact type: lowercase, dot-separated, no empty segments.
    /// </summary>
    /// <remarks>
    /// Deliberately does <strong>not</strong> check the value against a list. An unknown type is
    /// legitimate and storable; a malformed one is a bug in whatever produced it, and catching it
    /// at the write boundary keeps <c>"Banned"</c>, <c>"vrchat..ban"</c> and <c>"VRChat.Ban"</c>
    /// out of a log that is meant to be queryable by prefix for years.
    /// </remarks>
    public static bool IsWellFormed(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;

        var segments = value.Split('.');
        if (segments.Length < 2) return false;

        foreach (var segment in segments)
        {
            if (segment.Length == 0) return false;

            foreach (var c in segment)
            {
                if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
                    return false;
            }
        }

        return true;
    }
}
