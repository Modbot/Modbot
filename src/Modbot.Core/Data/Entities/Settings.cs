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

    // --- Managed group (spec 2.4) ---
    public string? ManagedGroupId { get; set; }
    public string? ManagedGroupName { get; set; }

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
    /// The Discord channel moderation events are posted to (foundation §9). Null means the bot
    /// answers commands but posts nothing.
    /// </summary>
    public string? DiscordLogChannelId { get; set; }

    /// <summary>
    /// Which event types are posted to that channel, comma-separated fact types. Null means the
    /// default set: bans, unbans, kicks, warns, join-request rejections and role changes.
    /// </summary>
    /// <remarks>
    /// Only group audit-log types are ever honoured, whatever this column says: the poster
    /// filters against a closed list, so an account or sign-in fact can never reach Discord even
    /// if somebody writes its type here by hand.
    /// </remarks>
    public string? DiscordLogEventTypes { get; set; }

    /// <summary>
    /// The largest fact id the channel poster has read. Same shape as
    /// <see cref="UserProfileEventsReadThrough"/>, for the same reason.
    /// </summary>
    /// <remarks>
    /// Null means "not started". When the poster finds it null with a channel configured it
    /// jumps to the newest fact and posts nothing, so turning the channel on never replays the
    /// group's whole history into Discord. Clearing the channel sets it back to null, so turning
    /// it back on later starts from that moment too. Null rather than zero because an empty log
    /// has no newest fact to jump to, and zero -- "everything after fact 0" -- is then exactly the
    /// right cursor.
    /// </remarks>
    public long? DiscordLogPostedThrough { get; set; }

    /// <summary>
    /// The Discord channel that open instances are announced in. Null means no announcements,
    /// which is the default and is not a fault.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="DiscordLogChannelId"/> on purpose. The moderation log is a record
    /// for the team and reads like a ledger; this is a notice board for members, saying "we are
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

    // --- Operator-supplied SMTP (spec 7.4) ---
    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    public string? SmtpUsername { get; set; }
    public string? SmtpPasswordEncrypted { get; set; }
    public string? SmtpFromAddress { get; set; }

    /// <summary>Defaults on: an SMTP relay that needs it turned off is the unusual one.</summary>
    public bool SmtpUseTls { get; set; } = true;

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
