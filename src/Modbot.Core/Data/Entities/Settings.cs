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
    /// here, because the public rooms report is how the landing page knows what a group looks
    /// like, and a group with no picture is a grey box on that page.
    /// </remarks>
    public string? ManagedGroupIconUrl { get; set; }

    /// <inheritdoc cref="ManagedGroupIconUrl"/>
    public string? ManagedGroupBannerUrl { get; set; }

    // --- Public rooms on modbot.co (central services design 4.6) ---

    /// <summary>
    /// Whether this server tells Modbot Cloud which of the group's rooms are open to everyone, so
    /// they are listed on modbot.co. On unless somebody turns it off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only rooms anyone can join are ever sent. A room limited to group members, or to members
    /// and their friends, is not a public room and never leaves this server — putting a link to
    /// one on a public web page would hand out an address the group deliberately kept inside.
    /// </para>
    /// <para>
    /// Nobody is counted. No head count, no member count, no list of who is in the room: the page
    /// says a room is open, not how many people are in it.
    /// </para>
    /// <para>
    /// <c>MODBOT_CLOUD_DISABLED</c> beats this setting. A server told not to talk to Cloud sends
    /// nothing, whatever is saved here.
    /// </para>
    /// </remarks>
    public bool SharePublicRooms { get; set; } = true;

    /// <summary>
    /// This server's id on Modbot Cloud for the public rooms report, made up here on the first
    /// report and kept afterwards, with the secret that proves it is the same server.
    /// </summary>
    /// <remarks>
    /// Cloud keeps only a hash of the secret, and takes the first report under an id as the one
    /// that claims it. So a server that keeps its row keeps its rooms, and nobody else can
    /// overwrite them.
    /// </remarks>
    public Guid? PublicRoomsServerId { get; set; }

    /// <inheritdoc cref="PublicRoomsServerId"/>
    public string? PublicRoomsSecretEncrypted { get; set; }

    /// <summary>When the public rooms report last reached Cloud.</summary>
    public DateTimeOffset? PublicRoomsReportedAt { get; set; }

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
    /// time a room opens must not be able to ping a server at four in the morning.
    /// </remarks>
    public string? DiscordInstanceMessage { get; set; }

    /// <summary>
    /// Whether an instance card lists the display names of the people in the room while a moderator
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
    /// Per-tool on/off switches, as a JSON object of tool name to true or false.
    /// </summary>
    /// <remarks>
    /// Only switches somebody changed are stored. A tool with no entry is on when it only reads
    /// and off when it acts, so a tool added in a later version starts the way its kind should.
    /// </remarks>
    public string AiChatToolSwitches { get; set; } = "{}";

    // --- AI moderation (AI moderation design) ---

    /// <summary>The one switch for term lists and AI topics. Off by default, like everything in M8.</summary>
    public bool AiModerationEnabled { get; set; }

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
    /// moderation is off, so switching it on checks the profiles seen in between.
    /// </summary>
    public long AiModerationProfileFactsReadThrough { get; set; }

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

    // ── Places: which rooms the group has open, and which worlds still need a name ──────────

    /// <summary>
    /// When the group's live instance list was last read. This is the only view Modbot has of a
    /// room nobody running the client is standing in, so how fresh it is decides how quickly an
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
}
