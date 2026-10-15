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
    /// When VRChat last refused Modbot's account for asking for a featured event ("You do not have
    /// permission to make a featured event"), or null if it never has. For a while after it the
    /// calendar leaves Featured out of what it sends, so an event is not refused again and again for
    /// the one setting; it asks once more when the time has passed, in case the account may now
    /// (calendar repeats and VRChat settings design §4).
    /// </summary>
    public DateTimeOffset? VRChatFeaturedRefusedAt { get; set; }

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
    /// sends nothing to VRChat, and events keep the picture ids they have. The Marketing tab's VRChat
    /// posts follow it too (posts design §3.6, decision 14): off, a VRChat post goes as text only.
    /// </summary>
    public bool VRChatPictureUploads { get; set; }

    /// <summary>
    /// Whether the VRChat account Modbot signs in as has VRChat+, read off its own tags
    /// (<c>system_supporter</c>) when Modbot signs in, and again when someone presses Check again
    /// in Settings. Null until it has been read, and then Modbot behaves as it did before it was
    /// known. False turns the VRChat picture fields off: VRChat takes a picture upload only from
    /// an account with VRChat+.
    /// </summary>
    public bool? VRChatHasPlus { get; set; }

    /// <summary>
    /// Whether the calendar's public feed answers at <c>/api/calendar/public.ics</c> (calendar design
    /// §6.1, added 2026-10-03): the events visible to everyone, with no secret in the address, for
    /// event directories and websites. Off until someone with Manage calendar turns it on, because it
    /// puts the group's public events on an address anyone can read; off, the address answers 404.
    /// </summary>
    public bool CalendarPublicFeed { get; set; }

    // --- Event alerts (event alerts design §3) ---

    /// <summary>
    /// Whether members are told before an event whose category they signed up for. Off until someone
    /// with Manage calendar turns it on: off, nothing is queued or sent, the Notify me button comes
    /// off the channel posts and <c>/notify</c> is not registered.
    /// </summary>
    public bool EventAlertsOn { get; set; }

    /// <summary>Whether an alert may go as a direct message. On by default.</summary>
    public bool EventAlertsDirectMessage { get; set; } = true;

    /// <summary>Whether an alert may go as a ping in <see cref="EventAlertsChannelId"/>. Off by default.</summary>
    public bool EventAlertsPing { get; set; }

    /// <summary>The Discord channel the ping is posted in. An opaque snowflake; null for none.</summary>
    public string? EventAlertsChannelId { get; set; }

    /// <summary>How long before an event starts the alert goes: 5 to 1,440. 60 by default.</summary>
    public int EventAlertsMinutesBefore { get; set; } = EventAlertSettings.DefaultMinutesBefore;

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
    /// Whether posts go to the VRChat group at all (posts design §3.6, §4.6). On by default, new
    /// installs and old, for the same reason as <see cref="DiscordPostsOn"/>: nothing goes out unless
    /// a person ticks VRChat on a post. Off, VRChat destinations wait and show "Off".
    /// </summary>
    public bool VRChatPostsOn { get; set; } = true;

    /// <summary>
    /// Whether events are sent to the Google calendar (Google Calendar design §3.1, step 2). The one
    /// place that stops every write to Google. Off until turned on after a good Check; off leaves
    /// what is on Google as it is (decision 7).
    /// </summary>
    public bool GoogleSendingOn { get; set; }

    /// <summary>
    /// Every event Modbot put on the Google calendar is being deleted, one call at a time, by the
    /// sending loop (Remove Modbot's events, decision 7). Turned off by the loop once none is left.
    /// Sending is off while this is on.
    /// </summary>
    public bool GoogleRemovingEvents { get; set; }

    // --- Twitch (Twitch design, 2026-10-07) ---

    /// <summary>
    /// The Twitch app's client id (dev.twitch.tv). Shown, not secret: Twitch prints it on the app's page.
    /// </summary>
    public string? TwitchClientId { get; set; }

    /// <summary>
    /// The Twitch app's client secret, encrypted like every other secret (see <c>ISecretProtector</c>).
    /// Never returned by the API.
    /// </summary>
    public string? TwitchClientSecretEncrypted { get; set; }

    /// <summary>The channel's login name as the operator typed it, lower case: <c>ourgroup</c>.</summary>
    public string? TwitchChannelLogin { get; set; }

    /// <summary>The channel's Twitch user id, as Check found it. Cleared with the credentials and the login.</summary>
    public string? TwitchChannelId { get; set; }

    /// <summary>The channel's display name, as Check found it.</summary>
    public string? TwitchChannelName { get; set; }

    /// <summary>
    /// When Check last ran. Null when it never has, or since the client id, the secret or the login
    /// changed: the Check fields describe the ones they were found with.
    /// </summary>
    public DateTimeOffset? TwitchCheckedAt { get; set; }

    /// <summary>What went wrong at the last Check, as the sentence the operator reads. Null when nothing did.</summary>
    public string? TwitchProblem { get; set; }

    /// <summary>
    /// No call of any kind goes to Twitch before this: set when Twitch answers with a rate limit, from
    /// its <c>Ratelimit-Reset</c>, and kept here so a restart cannot cut the wait short. Never retried early.
    /// </summary>
    public DateTimeOffset? TwitchStoppedUntil { get; set; }

    /// <summary>
    /// Whether Modbot asks Twitch once a minute if the channel is live. The one place that stops every
    /// poll. Off until turned on after a Check that passed.
    /// </summary>
    public bool TwitchLiveOn { get; set; }

    /// <summary>When the poll last got an answer from Twitch.</summary>
    public DateTimeOffset? TwitchPolledAt { get; set; }

    /// <summary>What went wrong at the last poll, as a sentence. Null when it worked.</summary>
    public string? TwitchPollProblem { get; set; }

    /// <summary>How many minutes the channel must have been live before a "live" post is made.</summary>
    public int TwitchPostAfterMinutes { get; set; } = 3;

    /// <summary>The most often a "live" post is made: no second one inside this many hours of the last.</summary>
    public int TwitchPostEveryHours { get; set; } = 6;

    /// <summary>The "live" post's title, with <c>{title}</c>, <c>{category}</c> and <c>{link}</c>. Null for the built-in one.</summary>
    public string? TwitchPostTitle { get; set; }

    /// <summary>The "live" post's text, with <c>{title}</c>, <c>{category}</c> and <c>{link}</c>. Null for the built-in one.</summary>
    public string? TwitchPostText { get; set; }

    /// <summary>
    /// Where a "live" post goes, as JSON (<c>TwitchPostPlaces</c>): the sites ticked and their own
    /// choices. Nothing is ticked to start, so nothing is posted until an operator ticks a site.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string TwitchPostPlaces { get; set; } = "{}";

    // --- Weekly spotlight (spotlight design, 2026-10-13) ---

    /// <summary>Whether Modbot makes a spotlight draft each week. Off to start.</summary>
    public bool SpotlightOn { get; set; }

    /// <summary>The day of the week the draft is made, 0 (Sunday) to 6, as <see cref="DayOfWeek"/> counts.</summary>
    public int SpotlightDay { get; set; } = 1;

    /// <summary>The hour of that day, 0 to 23, in <see cref="SpotlightTimeZone"/>.</summary>
    public int SpotlightHour { get; set; } = 9;

    /// <summary>The IANA zone the day and hour are in. Null is UTC.</summary>
    public string? SpotlightTimeZone { get; set; }

    /// <summary>One of <see cref="SpotlightModes"/>.</summary>
    public string SpotlightMode { get; set; } = SpotlightModes.Random;

    /// <summary>
    /// Where the spotlight post goes, as JSON (<c>TwitchPostPlaces</c>, the same shape the Twitch post
    /// uses): the sites ticked and their own choices. Nothing is ticked to start.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string SpotlightPlaces { get; set; } = "{}";

    /// <summary>The latest moment already dealt with. A moment at or before it never makes a draft.</summary>
    public DateTimeOffset? SpotlightHandledThrough { get; set; }

    // --- "We're live" group posts (2026-10-13) ---

    /// <summary>One of <see cref="LivePostScopes"/>: which instances get a "we're live" group post. Off to start.</summary>
    public string LivePostScope { get; set; } = LivePostScopes.Off;

    /// <summary>
    /// Post straight away instead of making a draft for a person to approve. With it on there is
    /// no draft and no message to the host.
    /// </summary>
    public bool LivePostAuto { get; set; }

    /// <summary>
    /// When the scope was last turned on from off. An instance first seen before this never gets a
    /// post, so turning the setting on does not post about the instances already running.
    /// </summary>
    public DateTimeOffset? LivePostFrom { get; set; }

    // --- Bluesky (Bluesky design §3.1, posts design §4.2c) ---

    /// <summary>
    /// The account's handle as the operator typed it, without the <c>@</c>: <c>ourgroup.bsky.social</c>
    /// or <c>ourgroup.com</c>. Shown, not secret. Check reads it again from the account each time.
    /// </summary>
    public string? BlueskyHandle { get; set; }

    /// <summary>
    /// The account's lasting id (its DID), found from the handle by Check. What Modbot keys on, so a
    /// handle changed on Bluesky does not stop posting. Never shown in the app.
    /// </summary>
    public string? BlueskyDid { get; set; }

    /// <summary>
    /// The account's own server (its PDS), an https address with no path, found by Check from the
    /// account's DID document. Every sign-in and post goes there.
    /// </summary>
    public string? BlueskyServer { get; set; }

    /// <summary>
    /// The app password, encrypted like every other secret (see <c>ISecretProtector</c>). Only a value
    /// shaped like an app password is taken, so the account's main password is never kept. Never
    /// returned by the API.
    /// </summary>
    public string? BlueskyAppPasswordEncrypted { get; set; }

    /// <summary>
    /// The signed-in session's access and refresh tokens, as one encrypted JSON. A refresh replaces
    /// both, and the new ones are written here before they are used, because the old refresh token
    /// stops working. Never returned by the API.
    /// </summary>
    public string? BlueskySessionEncrypted { get; set; }

    /// <summary>When Check last ran. Null when it never has, or since the handle or app password changed.</summary>
    public DateTimeOffset? BlueskyCheckedAt { get; set; }

    /// <summary>The account's display name as Check found it.</summary>
    public string? BlueskyDisplayName { get; set; }

    /// <summary>Whether Check found the account marked as automated (decision 10).</summary>
    public bool BlueskyAutomated { get; set; }

    /// <summary>What went wrong at the last Check or sign-in, as the sentence the operator reads. Null when nothing did.</summary>
    public string? BlueskyProblem { get; set; }

    /// <summary>
    /// Whether posts go to Bluesky (Bluesky design §3.1). The one place that stops all Bluesky
    /// posting, mirrored in Settings → Posts. Off until turned on after a good Check.
    /// </summary>
    public bool BlueskyPostingOn { get; set; }

    /// <summary>
    /// The tags every calendar post ends with after its categories' tags, as typed and cleaned
    /// (<see cref="Modbot.Core.Bluesky.BlueskyTags.Write"/>): <c>#VRChat #Karaoke</c>. Null or empty for none.
    /// </summary>
    public string? BlueskyPostTags { get; set; }

    /// <summary>
    /// Keep the next calendar date's Bluesky post pinned on the account's profile. A post a person
    /// pinned by hand is left alone.
    /// </summary>
    public bool BlueskyPinNextEvent { get; set; }

    /// <summary>The address of the post Modbot last pinned or unpinned for, so a pass with nothing to change asks Bluesky nothing. Null when Modbot has none pinned.</summary>
    public string? BlueskyPinnedUri { get; set; }

    /// <summary>When Modbot last looked at the profile's pin and left it as it was or could not change it: the next look waits.</summary>
    public DateTimeOffset? BlueskyPinCheckedAt { get; set; }

    /// <summary>Why the last try to pin or unpin failed, in plain words; shown on Health. Null once a pin goes through, and when none has failed.</summary>
    public string? BlueskyPinError { get; set; }

    /// <summary>When <see cref="BlueskyPinError"/> was last set.</summary>
    public DateTimeOffset? BlueskyPinErrorAt { get; set; }

    /// <summary>
    /// While Bluesky's public API limits the reading of post numbers, when it may be asked again. Apart
    /// from <see cref="BlueskyStoppedUntil"/>: that one is the account's own server, this is another.
    /// </summary>
    public DateTimeOffset? BlueskyCountsStoppedUntil { get; set; }

    /// <summary>Why the last try to read post numbers failed, in plain words; shown on Health. Null once a read goes through.</summary>
    public string? BlueskyCountsError { get; set; }

    /// <summary>When <see cref="BlueskyCountsError"/> was last set.</summary>
    public DateTimeOffset? BlueskyCountsErrorAt { get; set; }

    /// <summary>Post the week's line-up (the next seven days' public dates) on Bluesky once a week. Off to start.</summary>
    public bool BlueskyLineupOn { get; set; }

    /// <summary>The day of the week the line-up is posted, 0 (Sunday) to 6, as <see cref="DayOfWeek"/> counts.</summary>
    public int BlueskyLineupDay { get; set; } = 1;

    /// <summary>The hour of that day, 0 to 23, in <see cref="BlueskyLineupTimeZone"/>.</summary>
    public int BlueskyLineupHour { get; set; } = 12;

    /// <summary>The IANA zone the day and hour are in, and the times in the post. Null is UTC.</summary>
    public string? BlueskyLineupTimeZone { get; set; }

    /// <summary>The latest line-up time already looked at, so a time is handled once: turning the line-up on or moving its time sets it to the latest time gone by.</summary>
    public DateTimeOffset? BlueskyLineupHandledThrough { get; set; }

    /// <summary>
    /// No call goes to the account's server before this: set when Bluesky answers with a rate limit,
    /// to the time it says the limit resets, and kept here so a restart cannot cut the wait short.
    /// Never retried early (CLAUDE.md).
    /// </summary>
    public DateTimeOffset? BlueskyStoppedUntil { get; set; }

    /// <summary>
    /// Bluesky refused the app password at sign-in. Nothing signs in again until a new app password
    /// is saved, so a revoked password cannot become a loop of refused sign-ins.
    /// </summary>
    public bool BlueskySignInRefused { get; set; }

    /// <summary>When Modbot last signed in with the app password: at most one sign-in every 10 minutes.</summary>
    public DateTimeOffset? BlueskySignedInAt { get; set; }

    /// <summary>The UTC day <see cref="BlueskySignInsUsed"/> counts.</summary>
    public DateOnly? BlueskySignInsDay { get; set; }

    /// <summary>Sign-ins with the app password on <see cref="BlueskySignInsDay"/>: at most 20 a day.</summary>
    public int BlueskySignInsUsed { get; set; }

    /// <summary>
    /// The account was signed in with Bluesky's own sign-in page (OAuth, posts design §4.2c, step 3b)
    /// rather than an app password. Its tokens are in <see cref="BlueskySessionEncrypted"/>; no app
    /// password is kept beside them, so when Bluesky ends the sign-in, someone signs in again.
    /// </summary>
    public bool BlueskyOAuthSignedIn { get; set; }

    /// <summary>
    /// Modbot's own signing key for Bluesky's sign-in (a private ES256 key, as JSON), encrypted like
    /// every other secret. Its public half is the one in the client document Bluesky reads; the
    /// private half is never returned. Made on first use and kept, so a sign-in stays good.
    /// </summary>
    public string? BlueskyOAuthKeyEncrypted { get; set; }

    /// <summary>
    /// A sign-in with Bluesky that was started and not finished yet, as one encrypted JSON: its state,
    /// its PKCE verifier, its DPoP key, the account it is for and who started it. Good for ten
    /// minutes, and taken once by the callback that carries its state for the person who started it.
    /// </summary>
    public string? BlueskyOAuthPendingEncrypted { get; set; }

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

    // --- Permission watch ---

    /// <summary>
    /// The Discord channel staff are told in when a role or a member gains a dangerous permission
    /// (see <c>DiscordDangerousPermissions</c>). Null means the fact log alone, which a channel route
    /// can still send.
    /// </summary>
    public string? DiscordPermissionWatchChannelId { get; set; }

    /// <summary>
    /// Also take the permission away: from the role, or the role from the member. Off unless somebody
    /// turns it on. Never takes anything from the server's owner.
    /// </summary>
    public bool DiscordPermissionWatchRemoves { get; set; }

    // --- 18+ check ---

    /// <summary>The Discord roles a member is given when they pass the 18+ check. Null or empty means none.</summary>
    public List<string>? AgeCheckGiveRoleIds { get; set; }

    /// <summary>The Discord roles taken away from a member when they pass the 18+ check. Null or empty means none.</summary>
    public List<string>? AgeCheckRemoveRoleIds { get; set; }

    /// <summary>The managed VRChat group's role the person is given when they pass the 18+ check. Null means none.</summary>
    public string? AgeCheckGroupRoleId { get; set; }

    /// <summary>Delete the ticket channel once the 18+ check is done. Off to start.</summary>
    public bool AgeCheckDeleteTicket { get; set; }

    /// <summary>
    /// The category whose channels an 18+ check may delete. A Modbot ticket thread may be deleted
    /// whatever this says; no other channel may be, so null means only those.
    /// </summary>
    public string? AgeCheckTicketCategoryId { get; set; }

    // --- Gallery pictures from Discord ---

    /// <summary>
    /// The Discord channel where members post pictures for the group's VRChat gallery. Every PNG or
    /// JPEG posted there waits for staff on the Gallery page. Null means nothing is collected.
    /// </summary>
    public string? DiscordGalleryChannelId { get; set; }

    // --- Birthdays in Discord ---

    /// <summary>Whether Modbot posts the day's birthdays. Off to start. <c>/birthday</c> follows its own switch on the Commands card.</summary>
    public bool BirthdaysOn { get; set; }

    /// <summary>The Discord channel the day's birthdays are posted in. Null means none.</summary>
    public string? BirthdayChannelId { get; set; }

    /// <summary>The hour of the day, 0 to 23, in <see cref="BirthdayTimeZone"/>, that the birthdays are posted.</summary>
    public int BirthdayHour { get; set; } = 9;

    /// <summary>The IANA zone the hour is in. Null is UTC.</summary>
    public string? BirthdayTimeZone { get; set; }

    /// <summary>The post's text, with <c>{members}</c>. Null for <see cref="BirthdayRules.DefaultMessage"/>.</summary>
    public string? BirthdayMessage { get; set; }

    /// <summary>The role members hold for their birthday and lose on a later day. Null for none.</summary>
    public string? BirthdayRoleId { get; set; }

    /// <summary>The latest day's moment already dealt with. A moment at or before it posts nothing again.</summary>
    public DateTimeOffset? BirthdayHandledThrough { get; set; }

    /// <summary>What went wrong the last time the pass tried, in a sentence. Null when it went well.</summary>
    public string? BirthdayProblem { get; set; }

    // --- Invite tracking in Discord ---

    /// <summary>
    /// Whether Modbot records which invite each new member used. Off to start, since it needs Manage
    /// Server and Create Invite, which the bot's link asks for only while this is on.
    /// </summary>
    public bool DiscordInviteTrackingOn { get; set; }

    // --- Server snapshots in Discord ---

    /// <summary>
    /// How often Modbot saves a snapshot of the server's roles and channels by itself, one of
    /// <see cref="DiscordSnapshotSchedules"/>. Off by default; a snapshot can always be taken by hand.
    /// </summary>
    public string DiscordSnapshotSchedule { get; set; } = DiscordSnapshotSchedules.Off;

    /// <summary>
    /// Whether staff can put a role's or a channel's permissions back from a snapshot. Off to start,
    /// since it needs Manage Roles, which the bot's link asks for only while this is on.
    /// </summary>
    public bool DiscordSnapshotRestoreOn { get; set; }

    // --- Supporters (Ko-fi and Patreon) ---

    /// <summary>
    /// Whether Modbot takes in Ko-fi's payment messages. Off to start, and it cannot go on without a
    /// verification token. Turning it on makes the bot's invite link ask for Manage Roles.
    /// </summary>
    public bool KofiOn { get; set; }

    /// <summary>
    /// The verification token from Ko-fi's webhook page, encrypted like every other secret (see
    /// <c>ISecretProtector</c>). Every message carries it; one that does not match is refused.
    /// Never returned by the API.
    /// </summary>
    public string? KofiVerificationTokenEncrypted { get; set; }

    /// <summary>
    /// Whether Modbot takes in Patreon's membership messages. Off to start, and it cannot go on without a
    /// webhook secret. Turning it on makes the bot's invite link ask for Manage Roles.
    /// </summary>
    public bool PatreonOn { get; set; }

    /// <summary>
    /// The secret from Patreon's webhook page, encrypted like every other secret (see
    /// <c>ISecretProtector</c>). Every message is signed with it; one whose signature does not match is
    /// refused. Never returned by the API.
    /// </summary>
    public string? PatreonWebhookSecretEncrypted { get; set; }

    /// <summary>
    /// The client id of the Patreon client the owner registered, encrypted like every other secret.
    /// Needed to ask Patreon for a new access token. Never returned by the API.
    /// </summary>
    public string? PatreonClientIdEncrypted { get; set; }

    /// <summary>The client secret of that Patreon client, encrypted. Never returned by the API.</summary>
    public string? PatreonClientSecretEncrypted { get; set; }

    /// <summary>
    /// The creator's access token, encrypted: what Modbot asks Patreon for each patron's Discord account and
    /// the daily list of patrons with. Replaced when Modbot asks Patreon for a new one. Never returned by the API.
    /// </summary>
    public string? PatreonAccessTokenEncrypted { get; set; }

    /// <summary>The creator's refresh token, encrypted. Patreon gives a new one with each new access token. Never returned by the API.</summary>
    public string? PatreonRefreshTokenEncrypted { get; set; }

    /// <summary>When the saved access token runs out, as Patreon said when it gave it. Null for one pasted in, whose age is not known.</summary>
    public DateTimeOffset? PatreonTokenExpiresAt { get; set; }

    /// <summary>The id of the creator's first Patreon campaign, found with the access token and kept. Null until found.</summary>
    public string? PatreonCampaignId { get; set; }

    /// <summary>Nothing is sent to Patreon before this moment: Patreon limited Modbot (never retried).</summary>
    public DateTimeOffset? PatreonStoppedUntil { get; set; }

    /// <summary>When the daily catch-up of every current patron last got to the end. Null until it has.</summary>
    public DateTimeOffset? PatreonCatchUpAt { get; set; }

    /// <summary>When the next catch-up is due: a day after the last one, or sooner after a problem. Null is now, which a newly saved key makes it.</summary>
    public DateTimeOffset? PatreonCatchUpDueAt { get; set; }

    /// <summary>
    /// Why the last catch-up or Discord lookup did not work, as a sentence. Null when it did. While it is set
    /// Patreon is not asked for anything but the catch-up, when it is due.
    /// </summary>
    public string? PatreonCatchUpProblem { get; set; }

    /// <summary>
    /// How many days after a Ko-fi supporter's paid month is up the roles come off, or
    /// <see cref="SupporterLimits.RolesNeverOff"/> for never. 3 to start.
    /// </summary>
    public int KofiRolesOffDays { get; set; } = SupporterLimits.DefaultRolesOffDays;

    /// <summary>
    /// How many days after a Patreon supporter's paid month is up (or after a declined charge) the roles come off,
    /// or <see cref="SupporterLimits.RolesNeverOff"/> for never. 3 to start.
    /// </summary>
    public int PatreonRolesOffDays { get; set; } = SupporterLimits.DefaultRolesOffDays;

    /// <summary>
    /// The direct message a new supporter gets, with {name} and {tier} filled in. Null sends none.
    /// The same message for every site.
    /// </summary>
    public string? SupporterThanksText { get; set; }

    /// <summary>
    /// When a Modbot began saving a scheduled snapshot, held only while it does and cleared after. A
    /// second Modbot on the same database that finds it set (and recent) leaves the snapshot to the first,
    /// so one due moment gives one snapshot.
    /// </summary>
    public DateTimeOffset? DiscordSnapshotClaimedAt { get; set; }

    // --- New staff checklist ---

    /// <summary>
    /// The tasks a new staff member does and fills in themselves, one written answer each, first first. Copied
    /// onto a trial when it begins. Null or empty means none.
    /// </summary>
    public List<string>? NewStaffTasks { get; set; }

    /// <summary>The lines a new staff member's buddy signs off, first first. Copied onto a trial when it begins. Null or empty means none.</summary>
    public List<string>? NewStaffSignOffs { get; set; }

    /// <summary>
    /// The Discord channel the day-30 Council reminders and the 30-day inactive check-ins are posted in: one channel
    /// for both. Null means neither is posted.
    /// </summary>
    public string? StaffRemindersChannelId { get; set; }

    // --- Event recaps in Discord ---

    /// <summary>Whether Modbot posts a recap in Discord once an event's instance has closed. Off to start.</summary>
    public bool EventRecapsOn { get; set; }

    /// <summary>The Discord channel the recaps are posted in. Null means none.</summary>
    public string? EventRecapChannelId { get; set; }

    /// <summary>
    /// When the recaps were last turned on. An instance that closed before it gets no recap, so
    /// turning the switch on never posts for events that ended long ago.
    /// </summary>
    public DateTimeOffset? EventRecapsFrom { get; set; }

    /// <summary>What went wrong the last time a recap was posted, in a sentence. Null when it went well.</summary>
    public string? EventRecapProblem { get; set; }

    // --- Monthly event summary in Discord ---

    /// <summary>Whether Modbot posts, once a month, how each event did the month before. Off to start.</summary>
    public bool EventSummaryOn { get; set; }

    /// <summary>The Discord channel the summary is posted in. Null means none.</summary>
    public string? EventSummaryChannelId { get; set; }

    /// <summary>
    /// The latest month (its first day, UTC) already dealt with: posted, or left out because no event
    /// ran in it. Written before the post, so a month is posted once however many restarts. Turning
    /// the summary on sets it to the month before, so it never posts for months already over.
    /// </summary>
    public DateOnly? EventSummaryLastMonth { get; set; }

    /// <summary>What went wrong the last time the summary was posted, in a sentence. Null when it went well.</summary>
    public string? EventSummaryProblem { get; set; }

    // --- Member milestones ---

    /// <summary>The number of events somebody has come to that Modbot calls their first moment. 1 to start.</summary>
    public int MilestoneFirstAt { get; set; } = 1;

    /// <summary>The number of events at which somebody is a Regular. 5 to start.</summary>
    public int MilestoneRegularAt { get; set; } = 5;

    /// <summary>How many days away make a return a welcome back. 60 to start.</summary>
    public int MilestoneBackDays { get; set; } = 60;

    /// <summary>The Discord channel the milestone posts go in. Null means none.</summary>
    public string? MilestoneChannelId { get; set; }

    /// <summary>Whether a post goes up for somebody's first moment. Off to start.</summary>
    public bool MilestonePostFirst { get; set; }

    /// <summary>Whether a post goes up for somebody becoming a Regular. Off to start.</summary>
    public bool MilestonePostRegular { get; set; }

    /// <summary>Whether a post goes up for somebody coming back. Off to start.</summary>
    public bool MilestonePostBack { get; set; }

    /// <summary>The Discord role given to a Regular who has a linked account. Null means none.</summary>
    public string? MilestoneRegularRoleId { get; set; }

    /// <summary>
    /// When posts or the role were last turned on from nothing. A moment reached before it is left
    /// alone, so turning them on never posts for people who got there long ago.
    /// </summary>
    public DateTimeOffset? MilestonesFrom { get; set; }

    /// <summary>
    /// How far the scan for returning visitors has read the fact log. It starts again a day before,
    /// so a report that came in late is still read.
    /// </summary>
    public DateTimeOffset? MilestoneVisitsReadTo { get; set; }

    /// <summary>What went wrong the last time a milestone post or role was tried, in a sentence. Null when it went well.</summary>
    public string? MilestoneProblem { get; set; }

    /// <summary>
    /// The Discord channel a closed ticket's transcript is posted to, as a file. Null means no
    /// transcript is kept: the ticket still closes and the fact log still records it.
    /// </summary>
    public string? DiscordTicketTranscriptChannelId { get; set; }

    /// <summary>
    /// Which of the bot's commands are on, as a JSON object mapping a command's name to true or
    /// false (Discord commands design §3.8). A name that is not in it takes its default from
    /// <see cref="Modbot.Core.Discord.DiscordCommandSwitches"/>, and a command that is off is not
    /// registered on the server at all. This holds what the "Members can use /me" switch used to
    /// (Discord /me design, 2026-09-30): the migration copied it under the name <c>me</c>.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string DiscordCommands { get; set; } = "{}";

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

    // --- Smart welcome (smart welcome design §7) ---

    /// <summary>Whether new members are sent next steps. Off by default.</summary>
    public bool DiscordWelcomeEnabled { get; set; }

    /// <summary>One of <see cref="DiscordWelcomeWhere"/>.</summary>
    public string DiscordWelcomeVia { get; set; } = DiscordWelcomeWhere.DirectMessage;

    /// <summary>Where the channel post goes. Needed when <see cref="DiscordWelcomeVia"/> includes a channel.</summary>
    public string? DiscordWelcomeChannelId { get; set; }

    /// <summary>
    /// When the switch was last turned on, so only members who joined after it are welcomed. Null
    /// while the switch is off.
    /// </summary>
    public DateTimeOffset? DiscordWelcomeStartedAt { get; set; }

    /// <summary>
    /// Whether the welcome message carries a button to the account link page, for a member who has
    /// not linked. Off by default.
    /// </summary>
    public bool DiscordWelcomeLinkButton { get; set; }

    // --- Account links: the one-time reminder ---

    /// <summary>
    /// When staff started sending the link reminder, or null while it is not running. Only members who
    /// were in the server by then are sent it, so it ends when they have all been.
    /// </summary>
    public DateTimeOffset? DiscordLinkNudgeStartedAt { get; set; }

    /// <summary>
    /// When staff last started the link reminder; kept after it stops. The tab says "The reminder
    /// stopped" for a message Discord turned away only if nobody started it again since.
    /// </summary>
    public DateTimeOffset? DiscordLinkNudgeLastStartedAt { get; set; }

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
    /// How long a closed member report keeps what was written and the copy of the message, in days
    /// (Discord commands design §3.4, decision 10). 0 keeps them forever.
    /// </summary>
    /// <remarks>
    /// Its own setting: a report is a member's words to the mods, which is neither moderation
    /// history nor presence. After this many days from the report being closed, the text and the
    /// message copy are removed and the bare record stays. An open report is never touched: nobody
    /// has answered it yet.
    /// </remarks>
    public int MemberReportRetentionDays { get; set; } = MemberReport.DefaultRetentionDays;

    /// <summary>
    /// The ready-made messages staff can send the reporter when they close a report, as a JSON list
    /// of <c>{ "name", "text" }</c> (see <see cref="ReportReplies"/>). Empty to start: staff can still
    /// write their own.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string MemberReportReplies { get; set; } = "[]";

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
