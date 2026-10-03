using System.ComponentModel.DataAnnotations.Schema;

namespace Modbot.Core.Data.Entities;

/// <summary>
/// Modbot's entire configuration, as a single row.
/// </summary>
/// <remarks>
/// Foundation spec section 2.6: the environment carries only what is needed <em>before</em> the
/// database is reachable. Everything else is entered through the onboarding wizard and lives here,
/// so deploying Modbot is "click the template, open the URL, follow the wizard" rather than a page
/// of environment variables. Secret-bearing columns are encrypted by
/// <see cref="Security.ISecretProtector"/>.
/// </remarks>
public class Settings
{
    /// <summary>Always 1. Enforced by a database check constraint, not by convention.</summary>
    public int Id { get; set; } = 1;

    public bool OnboardingComplete { get; set; }

    /// <summary>
    /// True when everything in this database was made up by the demo seeder.
    /// </summary>
    /// <remarks>
    /// Written only by the demo seeder, and read only by <see cref="Configuration.DemoMode"/>, which
    /// uses it to tell "a demo that has already been seeded once" from "a deployment somebody set up
    /// for real". Without it, a demo would set <see cref="OnboardingComplete"/> during seeding and
    /// then refuse to be a demo on its own next restart. See demo mode design §2.
    /// </remarks>
    public bool DemoData { get; set; }

    // --- VRChat account (spec 2.3) ---
    public string? VRChatUsername { get; set; }
    public string? VRChatPasswordEncrypted { get; set; }
    public string? VRChatTotpSecretEncrypted { get; set; }
    public string? VRChatAuthCookieEncrypted { get; set; }

    /// <summary>
    /// The display name VRChat returned the last time these credentials were accepted.
    /// </summary>
    /// <remarks>
    /// Stored so the wizard and the settings page can show <em>which</em> account Modbot acts as
    /// without a live call — an operator who mistyped a shared account's email finds out by
    /// reading a name, not by watching a sync produce the wrong group's data.
    /// </remarks>
    public string? VRChatDisplayName { get; set; }

    /// <summary>When VRChat last accepted these credentials (spec 7.1, step 2).</summary>
    public DateTimeOffset? VRChatVerifiedAt { get; set; }

    /// <summary>
    /// The username the stored session cookies were issued to (foundation spec 4.1.2).
    /// </summary>
    /// <remarks>
    /// A session belongs to an account, not to the settings row. When the username beside it no
    /// longer matches, the cookies are ignored rather than sent: presenting one account's session
    /// while holding another account's password would report the wrong account as signed in.
    /// </remarks>
    public string? VRChatSessionAccount { get; set; }

    /// <summary>The VRChat user id the stored session belongs to, from the sign-in that made it.</summary>
    public string? VRChatSessionUserId { get; set; }

    /// <summary>
    /// The profile the session check reads to tell a bad session from a lost group (spec 4.1.2).
    /// Null means VRChat staff member Nayir's, <c>usr_fbdf2c30-fcea-4220-88f4-c3f83e11215a</c>.
    /// </summary>
    /// <remarks>
    /// Configuration rather than a constant, because it is someone else's account: if VRChat ever
    /// removes it, an operator changes this and nothing else. Never validated (spec 3.1.1).
    /// </remarks>
    public string? VRChatSessionCheckUserId { get; set; }

    /// <summary>When Modbot last signed in to VRChat with the account's password (spec 4.1.2).</summary>
    /// <remarks>
    /// Not every successful request: only a sign-in that sent the password. It is what tells an
    /// operator whether a restart or a deploy cost a sign-in, which it should not.
    /// </remarks>
    public DateTimeOffset? VRChatLastSignedInAt { get; set; }

    /// <summary>
    /// When Modbot may next try to sign in, while it is waiting (spec 4.1.2). Null when not waiting.
    /// </summary>
    /// <remarks>
    /// Kept here rather than in memory so a restart or a crash loop cannot cut the wait short.
    /// VRChat allows only a handful of sign-ins an hour and answers the next with an hour-long
    /// block, so a wait that a redeploy forgot would be spent straight back into that block.
    /// </remarks>
    public DateTimeOffset? VRChatSignInWaitUntil { get; set; }

    /// <summary>
    /// Why Modbot is waiting to sign in: <c>RateLimitedByVRChat</c> or <c>SignInLimitReached</c>.
    /// </summary>
    public string? VRChatSignInWaitReason { get; set; }

    // --- Managed group (spec 2.4) ---
    public string? ManagedGroupId { get; set; }
    public string? ManagedGroupName { get; set; }

    /// <summary>
    /// The group's icon and banner, as VRChat last gave them.
    /// </summary>
    /// <remarks>
    /// Kept out of <c>GroupInfoSnapshot</c> on purpose — a picture address
    /// changes on its own schedule and would make every poll look like a change — but recorded
    /// here, because the public instances report is how the landing page knows what a group looks
    /// like, and a group with no picture is a grey box on that page.
    /// </remarks>
    public string? ManagedGroupIconUrl { get; set; }

    /// <inheritdoc cref="ManagedGroupIconUrl"/>
    public string? ManagedGroupBannerUrl { get; set; }

    /// <summary>
    /// The languages the group lists on its VRChat page, as VRChat's codes (<c>eng</c>, <c>jpn</c>).
    /// Null until the group-info sync has read them.
    /// </summary>
    /// <remarks>
    /// Kept here with the pictures rather than in <c>GroupInfoSnapshot</c>, for the same reason: the
    /// VRChat analytics page shows them as the group has them now, and none of the group-info facts
    /// ever carried them, so adding them to the snapshot would write one "changed" fact on every
    /// deployment the first time it polled.
    /// </remarks>
    public List<string>? ManagedGroupLanguages { get; set; }

    /// <summary>
    /// The links the group lists on its VRChat page. Only <c>http</c> and <c>https</c> addresses are
    /// kept, so every one is safe to put in an <c>href</c>. Null until the sync has read them.
    /// </summary>
    /// <remarks><inheritdoc cref="ManagedGroupLanguages" path="/remarks"/></remarks>
    public List<string>? ManagedGroupLinks { get; set; }

    /// <summary>
    /// The group's galleries (id, name, description, members only), as JSON, from the group-info
    /// poll. Null until the poll has read them.
    /// </summary>
    /// <remarks>
    /// Kept so the VRChat page's Gallery tab can list the galleries with no request of its own: the
    /// poll's answer carries them already. Kept out of <c>GroupInfoSnapshot</c> for the same reason
    /// as the languages and links. Their images are read when the tab asks for them, never stored.
    /// </remarks>
    public string? ManagedGroupGalleries { get; set; }

    /// <summary>
    /// The group roles Modbot's own VRChat account holds, by id, from <c>myMember</c> in the
    /// group-info poll. Null until the poll has read them.
    /// </summary>
    /// <remarks>
    /// Kept so a refusal for a missing group permission can say which roles the account has, with
    /// no request of its own. The member list never includes the account asking for it, so this
    /// is the only place Modbot learns its own roles.
    /// </remarks>
    public List<string>? VRChatAccountRoleIds { get; set; }

    /// <summary>
    /// The group permissions Modbot's own VRChat account holds, as VRChat's ids
    /// (<c>group-bans-manage</c>, or <c>*</c> for the owner), from the same poll. Null until read.
    /// </summary>
    public List<string>? VRChatAccountPermissions { get; set; }

    /// <summary>
    /// Modbot's own VRChat user id, from <c>myMember</c> in the group-info poll. Null until read.
    /// </summary>
    /// <remarks>
    /// The member list never includes the account asking for it, so the group-info poll writes the
    /// account's own <c>group_member</c> row, and the member sweep needs this id to leave that row
    /// alone rather than mark it as left. Never validated (spec 3.1.1).
    /// </remarks>
    public string? VRChatAccountUserId { get; set; }

    // --- Public instances on modbot.co (central services design 4.6) ---

    /// <summary>
    /// Whether this server tells Modbot Cloud which of the group's instances are open to everyone, so
    /// they are listed on modbot.co. On unless somebody turns it off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only instances anyone can join are ever sent. An instance limited to group members, or to members
    /// and their friends, is not a public instance and never leaves this server — putting a link to
    /// one on a public web page would hand out an address the group deliberately kept inside.
    /// </para>
    /// <para>
    /// Nobody is counted. No head count, no member count, no list of who is in the instance: the page
    /// says an instance is open, not how many people are in it.
    /// </para>
    /// <para>
    /// <c>MODBOT_CLOUD_DISABLED</c> beats this setting. A server told not to talk to Cloud sends
    /// nothing, whatever is saved here.
    /// </para>
    /// </remarks>
    public bool SharePublicInstances { get; set; } = true;

    /// <summary>
    /// Whether the calendar's event form may upload a picture to VRChat, on the VRChat account Modbot
    /// signs in as (calendar design §2). Off until an operator turns it on: VRChat refused the
    /// gallery upload on the account the maintainer tried (it very likely needs VRChat+ on the account
    /// Modbot signs in as), and a scripted upload is the part of Modbot's VRChat use a terms-of-service
    /// review would look at hardest. Off, the upload endpoint answers "Picture uploads are off." and
    /// sends nothing to VRChat, and events keep the picture ids they have.
    /// </summary>
    public bool VRChatPictureUploads { get; set; }

    /// <summary>
    /// Whether the calendar's public feed answers at <c>/api/calendar/public.ics</c> (calendar design
    /// §6.1, added 2026-10-03): the events visible to everyone, with no secret in the address, for
    /// event directories and websites. Off until someone with Manage calendar turns it on, because it
    /// puts the group's public events on an address anyone can read; off, the address answers 404.
    /// </summary>
    public bool CalendarPublicFeed { get; set; }

    // --- Google Calendar (Google Calendar design §3.1) ---

    /// <summary>
    /// The service account's address from its key file (<c>client_email</c>): Modbot's Google
    /// address, which the owner shares the calendar with. Shown, not secret. Null with no key.
    /// </summary>
    public string? GoogleClientEmail { get; set; }

    /// <summary>Which of the account's keys is stored (<c>private_key_id</c>). Not secret.</summary>
    public string? GoogleKeyId { get; set; }

    /// <summary>The Google Cloud project the account belongs to (<c>project_id</c>). Not secret.</summary>
    public string? GoogleProjectId { get; set; }

    /// <summary>
    /// The key file's private key, PEM, encrypted like every other secret (see
    /// <c>ISecretProtector</c>). The rest of the file is not kept. Never returned by the API.
    /// </summary>
    public string? GooglePrivateKeyEncrypted { get; set; }

    /// <summary>The calendar Modbot uses, as Google names it (<c>…@group.calendar.google.com</c>).</summary>
    public string? GoogleCalendarId { get; set; }

    /// <summary>
    /// When Check last ran. Null when it never has, or since the key or the calendar changed: the
    /// Check fields below describe the key and calendar they were found with, and are cleared with
    /// them.
    /// </summary>
    public DateTimeOffset? GoogleCheckedAt { get; set; }

    /// <summary>The calendar's name, as Check found it.</summary>
    public string? GoogleCalendarName { get; set; }

    /// <summary>The calendar's IANA time zone, as Check found it.</summary>
    public string? GoogleCalendarTimeZone { get; set; }

    /// <summary>Whether Check found Modbot may change events on the calendar (Google's <c>writer</c> or <c>owner</c>).</summary>
    public bool GoogleCanChange { get; set; }

    /// <summary>
    /// Whether Check found the calendar public: <c>all</c>, <c>freeBusy</c> or <c>no</c>. Null after
    /// a Check when Google would not say.
    /// </summary>
    public string? GooglePublic { get; set; }

    /// <summary>What went wrong at the last Check, as the sentence the operator reads. Null when nothing did.</summary>
    public string? GoogleProblem { get; set; }

    /// <summary>
    /// No call of any kind goes to Google before this (design §3.7): set when Google answers with a
    /// rate limit, and kept here so a restart cannot cut the wait short. Never retried early.
    /// </summary>
    public DateTimeOffset? GoogleStoppedUntil { get; set; }

    /// <summary>
    /// Pause all posting (posts design §4.6): while on, no post goes to any site, Marketing posts and
    /// event posts alike. Posts wait, and one more than an hour late turns Failed with Post now.
    /// The calendar's own copies (its VRChat entry, the Discord event and the channel card) are not
    /// posts and carry on (decision 12). Off by default.
    /// </summary>
    public bool PostsPaused { get; set; }

    /// <summary>
    /// Whether posts go to Discord at all (posts design §4.6). On by default, new installs and old:
    /// nothing goes out unless a person ticks Discord on a post, and the calendar's one-off Discord
    /// posts were already on. Off, Discord destinations wait and show "Off".
    /// </summary>
    public bool DiscordPostsOn { get; set; } = true;

    /// <summary>
    /// This server's id on Modbot Cloud for the public instances report, made up here on the first
    /// report and kept afterwards, with the secret that proves it is the same server.
    /// </summary>
    /// <remarks>
    /// Cloud keeps only a hash of the secret, and takes the first report under an id as the one
    /// that claims it. So a server that keeps its row keeps its instances, and nobody else can
    /// overwrite them.
    /// </remarks>
    public Guid? PublicInstancesServerId { get; set; }

    /// <inheritdoc cref="PublicInstancesServerId"/>
    public string? PublicInstancesSecretEncrypted { get; set; }

    /// <summary>When the public instances report last reached Cloud.</summary>
    public DateTimeOffset? PublicInstancesReportedAt { get; set; }

    // --- Modbot Cloud (central services spec 1.1, 5) ---

    /// <summary>
    /// Whether this server sends Modbot Cloud its usage report every six hours. On unless somebody
    /// turns it off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The report is the fields of <c>ServerReport</c>: the server's public address (when one is
    /// set), the Modbot version, the operating system, the group's id, name, description, icon and
    /// banner, whether Discord is connected, which shared term lists are imported, the number of
    /// rate-limit cold stops and blocked requests since the last report, and whether AI moderation
    /// is on. Turning this off changes none of that; it only stops the report going.
    /// </para>
    /// <para>
    /// Off means no report and no registration: a server that has not registered yet stays
    /// unregistered, and asking for a link code registers it (address, version and operating
    /// system only). <c>MODBOT_CLOUD_DISABLED</c> beats this setting.
    /// </para>
    /// <para>
    /// It does not reach the other Cloud features. Sending Modbot's log
    /// (<see cref="ShipLogsToCloud"/>) and listing public instances
    /// (<see cref="SharePublicInstances"/>) have their own switches, and the log is sent with the
    /// login this report's first pass made.
    /// </para>
    /// </remarks>
    public bool SendUsageReport { get; set; } = true;

    /// <summary>
    /// The id Modbot Cloud gave this server when it registered, or null before it has. Cloud assigns
    /// it; this server never picks one.
    /// </summary>
    public string? CloudServerId { get; set; }

    /// <summary>The secret Cloud handed back once, encrypted at rest (spec 8.3).</summary>
    public string? CloudServerSecretEncrypted { get; set; }

    /// <summary>When the last report was sent, whether or not Cloud took it.</summary>
    public DateTimeOffset? CloudLastReportAt { get; set; }

    /// <summary>Whether Cloud took the last report. Null before the first one was tried.</summary>
    public bool? CloudLastReportOk { get; set; }

    /// <summary>One short sentence about the last failure, for the Health page. Null when it worked.</summary>
    public string? CloudLastReportProblem { get; set; }

    // --- Update checking (update checking design) ---

    /// <summary>
    /// Whether this server asks what the newest Modbot release is. On unless somebody turns it off.
    /// </summary>
    /// <remarks>
    /// <c>MODBOT_CLOUD_DISABLED</c> does <strong>not</strong> beat this one, and that is the point
    /// of it existing: the question sends nothing about the deployment and is not a Cloud feature,
    /// so an operator who wants no outbound calls at all has to say so here. Modbot never updates
    /// itself either way — it says what exists and the operator decides.
    /// </remarks>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>The newest server release Modbot has heard of, or null before it has heard of any.</summary>
    public string? NewestRelease { get; set; }

    /// <summary>When that release was published.</summary>
    public DateTimeOffset? NewestReleaseAt { get; set; }

    /// <summary>The page with that release's notes on it.</summary>
    public string? NewestReleaseNotesUrl { get; set; }

    /// <summary>The image to pull for it, such as <c>modbot/modbot</c>.</summary>
    public string? NewestReleaseImage { get; set; }

    /// <summary>The tag to pull it with.</summary>
    public string? NewestReleaseTag { get; set; }

    /// <summary>When the last check was made, whether or not it worked.</summary>
    public DateTimeOffset? UpdateCheckedAt { get; set; }

    /// <summary>One short sentence about the last failed check. Null when it worked.</summary>
    public string? UpdateCheckProblem { get; set; }

    // --- Optional egress proxy (spec 2.3.1) ---
    public string? ProxyUrl { get; set; }
    public string? ProxyUsername { get; set; }
    public string? ProxyPasswordEncrypted { get; set; }

    /// <summary>When the egress check last passed (spec 7.1.1).</summary>
    /// <remarks>
    /// Recorded so the wizard knows step 3 is genuinely done rather than merely skipped past, and
    /// so the settings page can say when the answer it is showing was last true. A host that was
    /// reachable in March and is WAF-blocked today is the exact case §7.1.1's re-runnable check
    /// exists for.
    /// </remarks>
    public DateTimeOffset? ConnectionCheckedAt { get; set; }

    // --- Optional integrations (spec 7.1 step 5, 9) ---

    /// <summary>
    /// The Discord bot token. Encrypted (spec 8.3); absent means the bot does not start and
    /// nothing else about Modbot is affected (spec 9).
    /// </summary>
    public string? DiscordBotTokenEncrypted { get; set; }

    /// <summary>The guild the bot serves. An opaque snowflake; never parsed or validated.</summary>
    public string? DiscordGuildId { get; set; }

    /// <summary>
    /// The Discord channel that open instances are announced in. Null means no announcements,
    /// which is the default and is not a fault.
    /// </summary>
    /// <remarks>
    /// Separate from the event channels (<see cref="DiscordEventRoute"/>) on purpose. Those are a
    /// record for the team and read like a ledger; this is a notice board for members, saying "we are
    /// in here right now", and the two want different channels and usually different audiences.
    /// </remarks>
    public string? DiscordInstanceChannelId { get; set; }

    /// <summary>
    /// The operator's own line, posted above the card -- "Come hang out!", a set of rules, a
    /// ping-free reminder. Null or empty posts the card on its own.
    /// </summary>
    /// <remarks>
    /// Sent with mentions disabled, always. A line written once and posted automatically every
    /// time an instance opens must not be able to ping a server at four in the morning.
    /// </remarks>
    public string? DiscordInstanceMessage { get; set; }

    /// <summary>
    /// Whether an instance card lists the display names of the people in the instance while a moderator
    /// is watching it. On by default.
    /// </summary>
    /// <remarks>
    /// With nobody watching, a card shows the head count only whatever this says, because Modbot does
    /// not know who is inside. Off keeps every card to the head count, for a community that would
    /// rather not have names posted in a channel.
    /// </remarks>
    public bool DiscordInstanceShowNames { get; set; } = true;

    // --- Discord account linking (Discord account linking design §4) ---

    /// <summary>
    /// The Discord application's OAuth2 client id, which is also its application id. Used for
    /// "Sign in with Discord" on the link page and to build the bot invite link.
    /// </summary>
    public string? DiscordOAuthClientId { get; set; }

    /// <summary>
    /// The OAuth2 client secret. Encrypted like the bot token and never returned. Forgotten when
    /// the client id changes without a new secret, so it is only ever sent with the id it was
    /// saved for.
    /// </summary>
    public string? DiscordOAuthClientSecretEncrypted { get; set; }

    /// <summary>
    /// "Prompt new joiners to link their VRChat account". Off by default; while off, the bot does
    /// not ask Discord for the privileged Server Members intent.
    /// </summary>
    public bool DiscordLinkPromptNewMembers { get; set; }

    /// <summary>
    /// Where a new member is mentioned when Discord refuses the direct message. Null means the
    /// prompt stops at the failed DM.
    /// </summary>
    public string? DiscordLinkBackupChannelId { get; set; }

    /// <summary>The role every linked member is given. Null means none.</summary>
    public string? DiscordLinkedRoleId { get; set; }

    /// <summary>The role a linked member whose VRChat record is 18+ verified is given. Null means none.</summary>
    public string? DiscordEighteenPlusRoleId { get; set; }

    /// <summary>
    /// "Members can use /me": the private command that shows a member what Modbot holds about them
    /// and lets them ask the staff to delete it (Discord /me design, 2026-09-30). Off by default;
    /// while off the command is not registered on the server at all.
    /// </summary>
    public bool DiscordMeCommand { get; set; }

    // --- The join gate (join gate design §3) ---

    /// <summary>
    /// <see cref="DiscordGateModes.Off"/>, <see cref="DiscordGateModes.Watch"/> (record what would
    /// happen, do nothing in Discord) or <see cref="DiscordGateModes.On"/>. Off by default.
    /// </summary>
    public string DiscordGateMode { get; set; } = DiscordGateModes.Off;

    /// <summary>When the gate last went from off to watching or on. Only people who join after it are gated by themselves.</summary>
    public DateTimeOffset? DiscordGateStartedAt { get; set; }

    /// <summary>The role that opens the server. Modbot gives it when the steps are done.</summary>
    public string? DiscordGateMemberRoleId { get; set; }

    /// <summary>The one channel everybody can see, where the gate's message with its button sits.</summary>
    public string? DiscordGateChannelId { get; set; }

    /// <summary>The operator's own words above the button in the gate channel. Sent with mentions off.</summary>
    public string? DiscordGateMessage { get; set; }

    /// <summary>The gate message Modbot posted and keeps up to date, and the channel it is in.</summary>
    public string? DiscordGateMessageId { get; set; }

    public string? DiscordGateMessageChannelId { get; set; }

    /// <summary>What the gate message last said, so it is rewritten only when that changes.</summary>
    public string? DiscordGateMessagePosted { get; set; }

    /// <summary>The step "Link VRChat account". Off by default.</summary>
    public bool DiscordGateNeedsLink { get; set; }

    /// <summary>The step "18+ on VRChat". Needs <see cref="DiscordGateNeedsLink"/>.</summary>
    public bool DiscordGateNeedsEighteenPlus { get; set; }

    /// <summary>Minutes before somebody who has not finished is removed. Null is never.</summary>
    public int? DiscordGateRemoveAfterMinutes { get; set; }

    /// <summary>Hold new joiners by itself when the "People joining Discord" alert fires. Off by default.</summary>
    public bool DiscordGateHoldOnSpike { get; set; }

    /// <summary>
    /// Pausing the server's invites is allowed: the button on the alert and the At the gate card, and,
    /// with <see cref="DiscordGateHoldOnSpike"/>, on a join spike by itself. Needs Manage Server.
    /// </summary>
    public bool DiscordGatePauseInvites { get; set; }

    /// <summary>
    /// Since when new joiners are held: nobody gets the member role by themselves until staff lift
    /// the hold. Null while not held.
    /// </summary>
    public DateTimeOffset? DiscordGateHeldAt { get; set; }

    /// <summary>The newest "People joining Discord" alert the gate has looked at, so each is acted on once.</summary>
    public DateTimeOffset? DiscordGateSpikeSeenAt { get; set; }

    // --- Role and ban sync (M5 §3 and §4; Discord sync design) ---
    //
    // Three switches, all off, because each one is a different decision. Turning any of them on
    // lets Modbot change somebody's standing on a platform because of something that happened on
    // the other one, and a group that wants roles kept in step does not necessarily want a chat
    // ban to take away their VRChat membership (M5 §4.1).

    /// <summary>
    /// Whether the role pairs are kept in step at all. Off by default; each pair also has its own
    /// switch and decides for itself which side wins.
    /// </summary>
    public bool DiscordRoleSyncOn { get; set; }

    /// <summary>
    /// Whether Discord roles give Modbot roles through the staff role mappings (staff roles from
    /// Discord design §9). Off by default: off gives and takes nothing, while the preview still
    /// answers, because seeing what would happen is how somebody decides to turn it on.
    /// </summary>
    public bool DiscordStaffRolesOn { get; set; }

    /// <summary>Whether a ban or unban in the VRChat group is copied into Discord.</summary>
    public bool DiscordBanSyncToDiscord { get; set; }

    /// <summary>Whether a ban or unban in Discord is copied into the VRChat group.</summary>
    public bool DiscordBanSyncToVRChat { get; set; }

    /// <summary>
    /// Whether a Discord ban made by another bot (Dyno, Carl-bot and the like) is copied into the
    /// group as well. Off by default, and only asked about while <see cref="DiscordBanSyncToVRChat"/>
    /// is on.
    /// </summary>
    /// <remarks>
    /// A bot bans on rules its owner wrote, often for chat behaviour, and often in bulk. A group
    /// that copies what its moderators do in Discord does not necessarily want to copy what
    /// somebody else's bot does. Modbot's own bot is never copied whatever this says: that is
    /// Modbot's own work coming back round.
    /// </remarks>
    public bool DiscordBanSyncFromBots { get; set; }

    /// <summary>
    /// What a copied VRChat ban does in Discord: one of <see cref="DiscordBanCopyActions"/>.
    /// </summary>
    /// <remarks>
    /// A group that does not want a VRChat offence to earn a permanent Discord ban can have Modbot
    /// remove the person from the server instead. A removal cannot be undone, so a later VRChat
    /// unban copies nothing: the person simply rejoins.
    /// </remarks>
    public string DiscordBanCopyAction { get; set; } = DiscordBanCopyActions.Ban;

    /// <summary>
    /// Whether saved lists give their paired Discord roles (roles from lists design §5). Off by
    /// default: off gives and takes nothing, while the preview still answers, because seeing what
    /// would happen is how somebody decides to turn it on.
    /// </summary>
    public bool DiscordListRolesOn { get; set; }

    // --- Webhooks (API keys design §6.7) ---

    /// <summary>
    /// Whether webhooks may be sent to private, loopback and link-local addresses, and over plain
    /// http. Off by default, so the webhook form cannot be used to reach Modbot's own network.
    /// </summary>
    public bool WebhooksAllowPrivateAddresses { get; set; }

    // --- AI (M8 section 4) ---

    /// <summary>
    /// Whether anything in Modbot may call the AI endpoint. Off by default: sending members'
    /// profile text anywhere is a choice a deployment makes, not one it inherits (M8 4.2).
    /// </summary>
    public bool AiEnabled { get; set; }

    /// <summary>
    /// Which preset the endpoint was filled from: <c>openrouter</c>, <c>xai</c>, <c>anthropic</c>,
    /// <c>openai</c> or <c>custom</c>. Text rather than a number so a new preset is not a
    /// migration. See <c>AiProviders</c> in <c>Modbot.AI</c>.
    /// </summary>
    public string? AiProvider { get; set; }

    /// <summary>The OpenAI-compatible base address, e.g. <c>https://openrouter.ai/api/v1</c>.</summary>
    public string? AiEndpoint { get; set; }

    /// <summary>
    /// The API key. Encrypted like every other secret, and never returned by the API.
    /// </summary>
    /// <remarks>
    /// Cleared whenever the endpoint changes without a new key being typed, so a stored key is
    /// only ever sent to the address it was entered for.
    /// </remarks>
    public string? AiApiKeyEncrypted { get; set; }

    /// <summary>The model features use unless they ask for another.</summary>
    public string? AiModel { get; set; }

    /// <summary>
    /// When an operator confirmed what member text Modbot sends to the provider (M8 §4.5). Null
    /// until somebody has; AI cannot be switched on before then.
    /// </summary>
    /// <remarks>
    /// One confirmation for the deployment, not one per person and not one per visit: it is a
    /// decision about where members' text goes, and it is recorded as a fact naming who made it.
    /// </remarks>
    public DateTimeOffset? AiAcknowledgedAt { get; set; }

    public Guid? AiAcknowledgedByUserId { get; set; }

    public string? AiAcknowledgedByUsername { get; set; }

    /// <summary>
    /// A second model, tried once when the first one errors, times out or is refused. Null means
    /// there is none and a failed call stays failed.
    /// </summary>
    /// <remarks>
    /// Never tried when a spend limit stopped the call, when the key is wrong, or when the person
    /// asking went away: none of those are the model's fault, and a second call would only spend
    /// again or fail the same way.
    /// </remarks>
    public string? AiFallbackModel { get; set; }

    /// <summary>
    /// How long a row in the call log is kept, in days. 0 keeps them forever.
    /// </summary>
    /// <remarks>
    /// A month by default: long enough to see what a model has been doing and to read the prompt
    /// behind a flag somebody is still looking at, short enough that the text of every flagged
    /// message does not sit in the database for a year.
    /// </remarks>
    public int AiCallLogKeepDays { get; set; } = 30;

    // --- AI chat (AI chat design §5) ---

    /// <summary>Whether the Chat page answers. Off by default, and needs <see cref="AiEnabled"/> too.</summary>
    public bool AiChatEnabled { get; set; }

    /// <summary>The model Chat uses. Null means <see cref="AiModel"/>.</summary>
    public string? AiChatModel { get; set; }

    /// <summary>Added to the end of Chat's system prompt, in the operator's own words.</summary>
    public string? AiChatInstructions { get; set; }

    /// <summary>How many tools one reply may call before it has to answer with what it has.</summary>
    public int AiChatMaxToolCalls { get; set; } = 8;

    /// <summary>The most tokens the model may write in one round of a reply.</summary>
    public int AiChatMaxReplyTokens { get; set; } = 2000;

    /// <summary>How long one reply may take, tool calls included, before it is stopped.</summary>
    public int AiChatTimeLimitSeconds { get; set; } = 120;

    /// <summary>
    /// Whether the instance and person popups offer an AI brief: a summary of the audit log
    /// entries their Activity tab shows (AI chat design §14). Off by default, and needs
    /// <see cref="AiEnabled"/> and <see cref="AiChatEnabled"/> too, because a brief is a Chat call
    /// with the Chat model, limits and permission.
    /// </summary>
    public bool AiBriefsEnabled { get; set; }

    /// <summary>
    /// How many tokens (input plus output) each team member may use in a UTC month, whatever the
    /// model, unless they have an allowance of their own (<see cref="AiMemberAllowance"/>). Null means
    /// no such limit. Tokens are counted for a model with no price too, which is why this is the
    /// measure that always works.
    /// </summary>
    public long? AiMemberMonthlyTokens { get; set; }

    /// <summary>
    /// How much each team member may spend in US dollars in a UTC month, unless they have an
    /// allowance of their own. Null means no such limit. Spend of a model with no price is unknown,
    /// so it cannot reach this; the token allowance is what stops that.
    /// </summary>
    public decimal? AiMemberMonthlyMoney { get; set; }

    /// <summary>
    /// Per-tool on/off switches, as a JSON object of tool name to true or false.
    /// </summary>
    /// <remarks>
    /// Only switches somebody changed are stored. A tool with no entry is on when it only reads
    /// and off when it acts, so a tool added in a later version starts the way its kind should.
    /// </remarks>
    public string AiChatToolSwitches { get; set; } = "{}";

    /// <summary>
    /// Whether the MCP server at <c>/mcp</c> answers (MCP server design). Off by default; off
    /// answers 404. It serves the same tools as Chat, under the same switches, to a person's own
    /// AI app -- so it needs no AI provider of its own and does not need <see cref="AiEnabled"/>.
    /// </summary>
    public bool McpServerEnabled { get; set; }

    /// <summary>
    /// Whether <c>/api/proxy/vrchat/…</c> forwards requests to VRChat (VRChat proxy design). Off
    /// by default; off answers 404. A proxied request goes out as the service account, so the
    /// switch is the operator's, whatever permissions a caller holds.
    /// </summary>
    public bool VRChatProxyEnabled { get; set; }

    /// <summary>
    /// Whether Modbot fetches VRChat pictures itself at <c>/api/files/vrchat</c>, rather than
    /// sending the browser to VRChat for them (VRChat files design).
    /// </summary>
    /// <remarks>
    /// Off by default, since 2026-09-18. Fetching somebody else's pictures for every face on
    /// every screen is a thing a server should be asked to do rather than told, and it costs the
    /// deployment the bandwidth and the disk the cache sits on. Off does not mean no pictures: the
    /// route answers with a redirect to VRChat, and whether VRChat serves a browser that asks
    /// directly is VRChat's business rather than Modbot's to assume.
    /// <para>
    /// A deployment that already had this on keeps it on -- the migration changes what a new row
    /// starts as, not what an existing one says.
    /// </para>
    /// </remarks>
    public bool VRChatImagesProxied { get; set; }

    // --- AutoMod (AutoMod design; the AI parts, AI moderation design) ---

    /// <summary>The one switch for term lists and AI topics. Off by default, like everything in M8.</summary>
    public bool AutoModEnabled { get; set; }

    /// <summary>
    /// Which AI tools AutoMod may use, as JSON switches by tool name (AutoMod design §6). Only
    /// switches somebody changed are stored; a tool with no entry stands where its kind starts.
    /// </summary>
    public string AutoModAiTools { get; set; } = "{}";

    /// <summary>How many AI calls moderation may make in one UTC day (design §4.2). Term lists are not counted.</summary>
    public int AiModerationDailyCallLimit { get; set; } = 200;

    /// <summary>
    /// How many profiles the profile check may put in one AI call. 1 sends one profile per call.
    /// </summary>
    /// <remarks>
    /// Five by default. The topics and the instructions are the same for every profile in the
    /// batch, so sending five together sends them once instead of five times; much past that and
    /// one unreadable answer costs five profiles a retry.
    /// </remarks>
    public int AiModerationProfileBatchSize { get; set; } = 5;

    /// <summary>The UTC day <see cref="AiModerationCallsUsed"/> counts.</summary>
    public DateOnly? AiModerationCallsDay { get; set; }

    public int AiModerationCallsUsed { get; set; }

    /// <summary>
    /// The last profile fact the profile check has read (design §8). It does not move while
    /// AutoMod is off, so switching it on checks the profiles seen in between.
    /// </summary>
    public long AutoModProfileFactsReadThrough { get; set; }

    // --- Operator-supplied SMTP (spec 7.4) ---
    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    public string? SmtpUsername { get; set; }
    public string? SmtpPasswordEncrypted { get; set; }
    public string? SmtpFromAddress { get; set; }

    /// <summary>Defaults on: an SMTP relay that needs it turned off is the unusual one.</summary>
    public bool SmtpUseTls { get; set; } = true;

    /// <summary>
    /// The most emails sent in any 24 hours. At least <see cref="Email.EmailLimit.Minimum"/>, and
    /// that many are always kept for account email (accounts and access design §4.4).
    /// </summary>
    public int EmailLimitPer24Hours { get; set; } = Email.EmailLimit.Default;

    /// <summary>
    /// The address people use to reach this Modbot, e.g. <c>https://modbot.example.com</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <strong>only</strong> thing a link sent by email or Discord is ever built from
    /// (accounts and access design §4.2). Building it from the request's host or a forwarded
    /// header would let anyone who can send a forgot-password request for a victim's username
    /// choose where the victim's genuine reset link points -- and collect the token when it is
    /// clicked. A human types this once and confirms it; the server never infers it silently.
    /// </para>
    /// <para>
    /// Null until set. While null, nothing is sent: the login page says so, and the copyable
    /// links an administrator makes still work, because the browser that shows them knows its
    /// own address.
    /// </para>
    /// </remarks>
    public string? PublicAddress { get; set; }

    /// <summary>
    /// Whether <c>GET /api/server</c> tells anyone who asks the owner's email address.
    /// </summary>
    /// <remarks>
    /// On by default, because the address is what lets somebody adding this server on
    /// my.modbot.co see whose server it is, and because a group's moderation contact is usually
    /// public already. An operator who does not want it published turns this off and the field
    /// comes back null; nothing else about the server page changes (server info and account email
    /// design §2.3).
    /// </remarks>
    public bool ServerShowOwnerEmail { get; set; } = true;

    // --- Retention, tiered per fact class (spec 5.5) ---
    public int ModerationFactRetentionDays { get; set; }         // 0 = keep forever
    public int PresenceFactRetentionDays { get; set; }            // 0 = keep forever

    /// <summary>
    /// How long stored Discord messages are kept, in days. 0 keeps them forever.
    /// </summary>
    /// <remarks>
    /// Its own setting rather than one of the fact classes (M5 spec §5.1): messages are neither
    /// moderation history nor presence, and a group may well want chat gone long before the bans
    /// it led to. Enforced by dropping whole months of <c>discord_message</c>.
    /// </remarks>
    public int DiscordMessageRetentionDays { get; set; }

    /// <summary>
    /// How long Modbot's own log lines are kept in the database, in days. 0 keeps them forever.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 180 days, unlike the fact classes above, which keep everything by default. A log line is not
    /// a record of what happened to a member; it is Modbot talking about itself, and six months is
    /// already longer than any question anybody asks of it. Keeping them forever would grow a table
    /// nobody reads past the tables that matter.
    /// </para>
    /// <para>
    /// Only the database copy. The files have their own limit on how many are kept, Seq has its own
    /// retention, and Modbot Cloud has its own setting for the copy it holds.
    /// </para>
    /// </remarks>
    public int LogRetentionDays { get; set; } = Logging.Store.LogStore.DefaultRetentionDays;

    /// <summary>
    /// Send Modbot's own log lines to Modbot Cloud. On unless somebody turns it off, and ignored
    /// entirely when <c>MODBOT_CLOUD_DISABLED</c> is set.
    /// </summary>
    public bool ShipLogsToCloud { get; set; } = true;

    /// <summary>The last <c>modbot_log</c> row id Cloud has been sent.</summary>
    public long CloudLogSentThroughId { get; set; }

    /// <summary>When the last batch reached Cloud.</summary>
    public DateTimeOffset? CloudLogSentAt { get; set; }

    /// <summary>
    /// Lines the shipper skipped because Cloud was unreachable for long enough that the queue ran
    /// past <c>CloudLogShipper.MostRowsBehind</c>. Counted, never silently forgotten.
    /// </summary>
    public long CloudLogDropped { get; set; }

    /// <summary>Why the last attempt to send failed. Null once one succeeds.</summary>
    public string? CloudLogError { get; set; }

    public DateTimeOffset? CloudLogErrorAt { get; set; }

    /// <summary>
    /// Deduplication half-window for client-reported facts (spec 5.7.1). Bounded above by the
    /// 15-second genuine leave-and-rejoin, below by residual clock skew after IModbotClock sync.
    /// </summary>
    public int DedupWindowSeconds { get; set; } = 5;

    /// <summary>Spec 5.8.1 -- optional by default; groups may opt into requiring it.</summary>
    public bool RequireModerationClassification { get; set; }

    /// <summary>
    /// The numbers repeat-offender status and the moderator pattern checks are decided on, as a
    /// sparse JSON document (spec 5.8.5: thresholds are configurable, with conservative defaults).
    /// Null means every default.
    /// </summary>
    /// <remarks>
    /// One <c>jsonb</c> column rather than a column per number, for the reason
    /// <see cref="SyncPacing"/> is: the set of checks is open, and each new one would otherwise be
    /// a migration. Absent fields take the current default, so a deployment that never touched a
    /// number picks up a revised default on upgrade. See <c>ReviewThresholds</c> in
    /// <c>Modbot.Analytics</c> for the fields and their bounds.
    /// </remarks>
    [Column(TypeName = "jsonb")]
    public string? ReviewThresholds { get; set; }

    /// <summary>
    /// Which rules make a person Flagged on the companion and the Live page, as a sparse JSON
    /// document (flagged rules design §3). Null means every default. See <c>FlagRuleSettings</c> in
    /// <c>Modbot.Api</c> for the fields.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? FlagRules { get; set; }

    // --- Sync pacing (spec 4.2.1) ---

    /// <summary>
    /// Every rate, ceiling and interval the operator has moved off spec 4.2's defaults, as a
    /// sparse JSON document. Null means nothing has been configured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One <c>jsonb</c> column rather than a column per rate</strong>, because the set of
    /// endpoint classes is open by design: spec 4.3.4 makes adding one a decision taken during
    /// implementation, and two of them (<c>users.groups</c>, <c>groups.auditlog.types</c>)
    /// arrived after the table in spec 4.2 was written. A typed column per class would make every
    /// new endpoint class a migration, and would leave a dead column behind whenever one was
    /// withdrawn.
    /// </para>
    /// <para>
    /// The usual objection — that <c>jsonb</c> is harder to validate — does not buy much here,
    /// because the validation that matters is not one a column constraint can express. The
    /// dangerous value is not a negative rate (a <c>CHECK</c> would catch that) but a rate above
    /// the per-class cap in spec 4.2, and that cap is a C# constant that moves with the spec. So
    /// the enforcement lives in code and runs twice: once on write, so the stored number is the
    /// one that runs, and again on read, so a hand-edited row or a restored backup cannot raise a
    /// rate either. See <c>SyncPacingJson.Clamp</c>.
    /// </para>
    /// <para>
    /// The document is sparse on purpose. An absent field means "use spec 4.2's default", so a
    /// deployment that never touched a slider picks up a revised default on upgrade, while one
    /// that deliberately lowered a rate keeps its choice.
    /// </para>
    /// </remarks>
    [Column(TypeName = "jsonb")]
    public string? SyncPacing { get; set; }

    // --- Sync cursors (spec 4.2.4) ---
    //
    // These are position, not configuration, and they live on the settings row for the reason
    // the row exists at all: a single-group appliance has exactly one of each. Losing them costs
    // a re-read, never a fact -- every producer that reads them also deduplicates, so the worst
    // a reset cursor can do is spend budget rediscovering what is already recorded.

    /// <summary>
    /// The newest audit-log entry Modbot has fully consumed. The next poll starts a configurable
    /// overlap <em>behind</em> this rather than at it.
    /// </summary>
    /// <remarks>
    /// A precise high-water mark would be wrong here: VRChat's audit-log ids are opaque and its
    /// timestamps are not guaranteed monotonic across a paged read, so an entry written while a
    /// page was in flight can surface with a <c>created_at</c> below the mark. Re-reading a
    /// window and discarding what is already recorded fails toward duplicate work; advancing
    /// exactly fails toward silently losing a ban.
    /// </remarks>
    public DateTimeOffset? AuditLogSyncedThrough { get; set; }

    /// <summary>
    /// How far the one-off walk back through the group's existing audit log has got, as an
    /// offset into it. Meaningless once <see cref="AuditLogCatchUpComplete"/> is true.
    /// </summary>
    /// <remarks>
    /// Offsets are safe for this walk in a way they are not in general: the audit log only ever
    /// grows at the head, so entries arriving mid-walk shift the page window toward entries
    /// already seen. That produces duplicates, which are discarded, and never a gap.
    /// </remarks>
    public int AuditLogCatchUpOffset { get; set; }

    /// <summary>True once VRChat has no older audit-log entries left to hand over.</summary>
    public bool AuditLogCatchUpComplete { get; set; }

    /// <summary>
    /// Which version of the one-off walk the stored cursor belongs to. Below the version the
    /// running build expects, the walk starts again from offset 0.
    /// </summary>
    /// <remarks>
    /// Exists so a change in what the walk would record -- a new event type mapped, an entry
    /// shape kept that used to be dropped -- can re-read what VRChat still holds without anyone
    /// touching the database by hand. Zero on every deployment from before it existed, which is
    /// what makes the first bump reach them.
    /// </remarks>
    public int AuditLogCatchUpVersion { get; set; }

    /// <summary>
    /// Which version of <c>NameNormalizer</c> made the stored searchable names. Below the version
    /// the running build carries, the name catch-up clears them all and makes them again.
    /// </summary>
    /// <remarks>
    /// Zero on every deployment from before searchable names existed, so the first build with
    /// them fills every row. A later change to the folding rules bumps the version and the same
    /// pass re-runs, without anyone touching the database by hand.
    /// </remarks>
    public int NameCatchUpVersion { get; set; }

    /// <summary>
    /// How far into the current backlog the poll has read, when there is more waiting than one
    /// pass may read. Zero whenever the window was last drained completely.
    /// </summary>
    /// <remarks>
    /// Without this a backlog larger than one pass's page budget never clears: the cursor cannot
    /// advance until the window is drained, so every pass would re-read the same first pages and
    /// the entries behind them would stay unread forever. The failure would look like a producer
    /// working perfectly on a group that had been offline for a day.
    /// </remarks>
    public int AuditLogBacklogOffset { get; set; }

    /// <summary>
    /// When the audit-log poll last completed, successfully or not.
    /// </summary>
    /// <remarks>
    /// Spec 4.2.3: the UI shows real last-sync times per data type and never an implied freshness
    /// guarantee, so the time has to be recorded rather than inferred from the newest fact -- a
    /// quiet group and a broken sync produce the same newest fact and must not look alike.
    /// </remarks>
    public DateTimeOffset? AuditLogPolledAt { get; set; }

    /// <summary>
    /// When the last audit-log pass that read all the way to the newest entry began.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not the same as <see cref="AuditLogPolledAt"/>, which is stamped by every pass, including
    /// one that was rate-limited, failed, or ran out of pages with a backlog still behind it. The
    /// member and ban sweeps wait on this one instead: before they record a join or a ban
    /// themselves, the audit log has to have read everything up to a moment after they noticed it
    /// (member and ban sync design §4). A poll that stopped partway through a long backlog, after
    /// a restart say, has not read that far, and a sweep that took it as the audit log's turn would
    /// write the same join the audit log was about to.
    /// </para>
    /// <para>
    /// The pass's start rather than its end, because an entry made while the pass was paging
    /// may not be on any page it read.
    /// </para>
    /// </remarks>
    public DateTimeOffset? AuditLogReadToEndAt { get; set; }

    /// <summary>
    /// When Modbot first began writing "ended on its own" entries for the group's instances. Null
    /// until the first pass after the update that added them.
    /// </summary>
    /// <remarks>
    /// An instance that ended before this moment is caught up: its entry is written for the record
    /// and never posted to Discord, however soon after the end it is written. One that ended after it
    /// is news, even if a stalled audit log delays the entry. Set once, by that first pass, and never moved.
    /// </remarks>
    public DateTimeOffset? InstanceEndEntriesStartedAt { get; set; }

    /// <summary>
    /// The group metadata as it was when the last change was recorded, as JSON.
    /// </summary>
    /// <remarks>
    /// Kept so the producer can tell a change from a re-assertion. Writing a fact on every poll
    /// regardless would bury the real changes and corrupt any "how often does this change"
    /// question -- the same failure as the avatar-line noise in the log research (§4.0), where
    /// 82% of the lines restated what was already true.
    /// </remarks>
    public string? GroupInfoSnapshot { get; set; }

    /// <summary>When the group-info poll last completed. Same reasoning as the audit log's.</summary>
    public DateTimeOffset? GroupInfoPolledAt { get; set; }

    /// <summary>
    /// The largest fact id the profile sync has read while looking for people it has not seen
    /// before. Each pass reads the facts written since, minus a small overlap, rather than
    /// rescanning the whole log.
    /// </summary>
    /// <remarks>
    /// An id rather than a timestamp because the fact log's primary key leads with it, so "every
    /// row after this one" is an index range and needs no new index on a table that is already
    /// the largest in the database. Ids are handed out at insert and committed slightly later,
    /// so a fact can appear below a cursor that has already moved past it; the overlap the
    /// producer re-reads covers that, and the cost of missing one anyway is only that the person
    /// is discovered by their next fact rather than this one.
    /// </remarks>
    public long UserProfileEventsReadThrough { get; set; }

    /// <summary>When the profile sync last completed a pass, refresh or not. Same reasoning as the audit log's.</summary>
    public DateTimeOffset? UserProfilePolledAt { get; set; }

    // --- Member and ban sweeps (member and ban sync design §3) ---
    //
    // A sweep is a walk through VRChat's list a page at a time, and a restart mid-way resumes
    // from the page it was on rather than from the front. The offset is the resume point; the
    // start time is what tells a row seen this sweep from a row not seen; the completion time is
    // what the UI shows as "last synced".

    /// <summary>How far into the current member sweep the walk has got. Zero between sweeps.</summary>
    public int MemberSweepOffset { get; set; }

    /// <summary>When the sweep now in progress started. Null between sweeps.</summary>
    public DateTimeOffset? MemberSweepStartedAt { get; set; }

    /// <summary>When the last full member sweep finished. Null until one has.</summary>
    public DateTimeOffset? MemberSweepCompletedAt { get; set; }

    /// <summary>When the last full sweep started, so a join VRChat dates before it is known to have been missed rather than new.</summary>
    public DateTimeOffset? MemberSweepPreviousStartedAt { get; set; }

    /// <summary>How many members the last full sweep listed.</summary>
    public int MemberSweepCount { get; set; }

    /// <summary>How many members the sweep now in progress has listed so far.</summary>
    public int MemberSweepSeenSoFar { get; set; }

    /// <summary>When the member sweep last completed a pass, successfully or not.</summary>
    public DateTimeOffset? MemberSweepPolledAt { get; set; }

    public int BanSweepOffset { get; set; }

    public DateTimeOffset? BanSweepStartedAt { get; set; }

    public DateTimeOffset? BanSweepCompletedAt { get; set; }

    public DateTimeOffset? BanSweepPreviousStartedAt { get; set; }

    public int BanSweepCount { get; set; }

    public int BanSweepSeenSoFar { get; set; }

    public DateTimeOffset? BanSweepPolledAt { get; set; }

    // ── Places: which instances the group has open, and which worlds still need a name ──────────

    /// <summary>
    /// When the group's live instance list was last read. This is the only view Modbot has of a
    /// instance nobody running the client is standing in, so how fresh it is decides how quickly an
    /// unattended event shows up at all.
    /// </summary>
    public DateTimeOffset? GroupInstancesPolledAt { get; set; }

    /// <summary>When the sweep that puts names to worlds last ran.</summary>
    public DateTimeOffset? WorldSweepPolledAt { get; set; }

    // ── Evidence storage (evidence design §6, §8) ───────────────────────────────────────────

    /// <summary>
    /// Which backend holds evidence: 0 none, 1 S3, 2 filesystem, 3 in-database.
    /// </summary>
    /// <remarks>
    /// Stored as the raw value rather than mapped through an enum here, because
    /// <c>Modbot.Evidence</c> owns that enum and <c>Modbot.Core</c> does not reference it — the
    /// store is deliberately a leaf the rest of the system does not depend on.
    /// </remarks>
    public short EvidenceBackend { get; set; }

    /// <summary>
    /// The store marker written into the store when it was set up.
    /// </summary>
    /// <remarks>
    /// This is the memory outside the store that makes §8's detection conclusive.
    /// <c>PersistenceProbe</c> cannot say whether a missing marker means "first run" or "wiped",
    /// because it has nowhere to remember having written one. This column is that memory: if it
    /// is set and the store has no matching store marker, the store is <em>wrong</em> — an unmounted
    /// volume, an emptied bucket, or a different bucket entirely — and that is a finding rather
    /// than an ambiguity.
    /// </remarks>
    public Guid? EvidenceStoreId { get; set; }

    /// <summary>Filesystem backend: where objects go. Requires a mounted volume to be useful.</summary>
    public string? EvidenceRoot { get; set; }

    public string? EvidenceS3Bucket { get; set; }
    public string? EvidenceS3Endpoint { get; set; }
    public string? EvidenceS3AccessKeyId { get; set; }
    public string? EvidenceS3Region { get; set; }

    /// <summary>Key prefix, so one bucket can hold more than one deployment's evidence.</summary>
    public string? EvidenceS3Prefix { get; set; }

    /// <summary>
    /// Older S3-compatible buckets need path-style URLs; newer Railway buckets are
    /// virtual-hosted. The bucket's own credentials page says which, so this is asked rather than
    /// guessed.
    /// </summary>
    public bool EvidenceS3UsePathStyle { get; set; }

    /// <summary>Encrypted like every other secret (see <c>ISecretProtector</c>).</summary>
    public string? EvidenceS3SecretAccessKeyEncrypted { get; set; }

    /// <summary>Per-file cap, enforced while streaming rather than after buffering.</summary>
    public long EvidenceMaxFileBytes { get; set; } = 100L * 1024 * 1024;

    public long EvidenceMaxReportBytes { get; set; }
    public long EvidenceMaxDeploymentBytes { get; set; }

    /// <summary>
    /// Hand the browser a presigned URL rather than proxying bytes through Modbot.
    /// </summary>
    /// <remarks>
    /// Only S3 can do it. On Railway it is also the cheaper path — bucket egress is free and
    /// service egress is not — so proxying is billed in both directions for no benefit.
    /// </remarks>
    public bool EvidenceDirectDeliveryEnabled { get; set; } = true;

    // ── The operator's acknowledgement that a disk may not persist (§8.2) ───────────────────

    /// <summary>
    /// Set when the operator chose the filesystem backend despite Modbot being unable to prove
    /// the directory survives a restart.
    /// </summary>
    /// <remarks>
    /// Modbot warns and recommends object storage; it does not refuse. Platform detection is a
    /// suspicion — Railway, Fly.io and Render all support mountable volumes, and the operator is
    /// the only party who knows whether they mounted one.
    /// </remarks>
    public bool EvidenceDiskAcknowledged { get; set; }

    public string? EvidenceDiskAcknowledgedBy { get; set; }

    public DateTimeOffset? EvidenceDiskAcknowledgedAt { get; set; }

    /// <summary>
    /// The exact warning text the operator was shown, stored verbatim.
    /// </summary>
    /// <remarks>
    /// Not a reference to the current wording. A reworded warning must not retroactively change
    /// what somebody agreed to — if this ever has to be pointed at in a dispute, it has to be the
    /// sentence that was actually on their screen.
    /// </remarks>
    public string? EvidenceDiskWarningShown { get; set; }

    // ── Auto-invites (auto-invites design) ──────────────────────────────────────────────────

    /// <summary>
    /// Whether Modbot invites people to the group once they have spent long enough in one of its
    /// instances and pass <see cref="GroupAutoInviteRules"/>. Off by default.
    /// </summary>
    /// <remarks>
    /// Off is the only safe default. Invites go out in the group's name, to named people, and
    /// cannot be taken back; a deployment that upgraded into this switched on would start
    /// inviting strangers without anybody deciding to.
    /// </remarks>
    public bool GroupAutoInviteEnabled { get; set; }

    /// <summary>
    /// How many minutes somebody must have been in the instance before they can be invited.
    /// <strong>Never fewer than <see cref="MinimumAutoInviteMinutes"/>.</strong>
    /// </summary>
    /// <remarks>
    /// A column rather than a rule in <see cref="GroupAutoInviteRules"/>, because the rule tree
    /// has "none of" in it: anything it can say it can also invert, and a floor a rule builder can
    /// turn upside down is not a floor (auto-invites design §4.1). Every path that reads or writes
    /// it clamps.
    /// </remarks>
    public int GroupAutoInviteMinutesInInstance { get; set; } = MinimumAutoInviteMinutes;

    /// <summary>The smallest <see cref="GroupAutoInviteMinutesInInstance"/> anybody may set.</summary>
    public const int MinimumAutoInviteMinutes = 5;

    /// <summary>
    /// How many days before the same person may be invited again. Somebody who declined, or who
    /// left the instance and came back, is not asked again inside it.
    /// </summary>
    public int GroupAutoInviteAgainAfterDays { get; set; } = 30;

    /// <summary>
    /// Who qualifies, as a rule tree in the shape <c>GiveawayRules</c> reads and writes
    /// (auto-invites design §3). Empty "all of" means everybody who gets past the checks that are
    /// not rules.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string GroupAutoInviteRules { get; set; } = "{\"kind\":\"allOf\",\"rules\":[]}";

    // ── VRChat picture cache (VRChat files design §5) ───────────────────────────────────────

    /// <summary>
    /// How much disk the cache of VRChat pictures and videos may use before the oldest are
    /// deleted. 2 GB by default; 0 means no cache at all, so every picture is fetched again.
    /// </summary>
    /// <remarks>
    /// A cap rather than an age, because these files never change -- a VRChat file address names
    /// a version -- so there is no moment at which a cached one has gone stale. The only reason
    /// to delete any of them is that the disk is not endless, and a cap is that reason written
    /// down. It sits beside the evidence settings because it is the same disk.
    /// </remarks>
    public long VRChatFileCacheBytes { get; set; } = 2L * 1024 * 1024 * 1024;
}
