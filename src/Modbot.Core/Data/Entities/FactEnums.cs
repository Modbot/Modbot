namespace Modbot.Core.Data.Entities;

/// <summary>
/// Which system an identifier belongs to.
/// </summary>
/// <remarks>
/// Spec 5.3. Its own column rather than a namespaced string (<c>dc:123…</c>) so the identifier
/// stays joinable against the user tables without parsing, and so a Discord snowflake can never
/// collide with a VRChat id in the same text column.
/// <para><strong>Persisted as smallint. Never renumber a member.</strong></para>
/// </remarks>
public enum FactPlatform : short
{
    VRChat = 1,
    Discord = 2,

    /// <summary>Modbot itself: the subject or actor of its own audit entries (spec 5.9).</summary>
    Modbot = 3,
}

/// <summary>
/// Where a fact came from, which is also what tells you how much to trust its timestamp.
/// </summary>
/// <remarks>
/// Spec 5.3. An <see cref="AuditLog"/> ban is exact; a <see cref="SyncDiff"/> one is an inference
/// with a window. Same event, different confidence -- and when both arrive, the authoritative one
/// wins.
/// <para><strong>Persisted as smallint. Never renumber a member.</strong></para>
/// </remarks>
public enum FactSource : short
{
    /// <summary>VRChat's own group audit log. Authoritative and exact.</summary>
    AuditLog = 1,

    /// <summary>Inferred by comparing two syncs. Carries an <c>occurred_before</c> window.</summary>
    SyncDiff = 2,

    /// <summary>Reported by a moderator's Windows client (M3). The deduplicated path.</summary>
    Client = 3,

    /// <summary>Observed in Discord (M5).</summary>
    Discord = 4,

    /// <summary>Entered by a person through Modbot.</summary>
    Manual = 5,

    /// <summary>
    /// Modbot's own record of what happened inside Modbot: logins, settings changes, sync
    /// failures. There is no second audit system -- the fact log already is the audit log
    /// (spec 5.9).
    /// </summary>
    Modbot = 6,
}

/// <summary>
/// What happened.
/// </summary>
/// <remarks>
/// <para>Spec 5.3 and 5.9.2. <strong>Persisted as smallint. Never renumber a member.</strong></para>
/// <para>
/// Values are grouped in blocks by retention class (spec 5.5), which is what the retention job
/// prunes on. Neither class is pruned by default -- Modbot keeps everything until an operator
/// configures a window -- but they are configured separately, so they are numbered separately.
/// </para>
/// </remarks>
public enum FactType : short
{
    // --- Membership and moderation (retention: forever) ---
    MemberJoined = 100,
    MemberLeft = 101,
    MemberBanned = 102,
    MemberUnbanned = 103,
    MemberKicked = 104,
    RoleGranted = 105,
    RoleRevoked = 106,
    InviteCreated = 107,

    /// <summary>
    /// The managed group's own metadata changed: its name, description, privacy, owner, member
    /// count, or the definition of one of its roles. The subject is the <em>group</em>, which is
    /// the only fact type for which that is true.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Appended, not inserted: spec 5.3's list of types was written before there was a producer
    /// for group metadata, and the group-info sync has nowhere honest to put its observations
    /// without it. <c>SettingsChanged</c> is Modbot's own configuration and means something else.
    /// </para>
    /// <para>
    /// Moderation retention rather than presence, because it is the answer to "what did this
    /// group look like when that ban happened" -- a role renamed after the fact is exactly what
    /// makes an old <see cref="RoleGranted"/> unreadable -- and because it is written only when
    /// something actually changed, so it does not accumulate the way operational noise does.
    /// </para>
    /// <para>
    /// It is also what lets the member-count series start from a real headcount rather than from
    /// zero: the rollup job's <c>members.net</c> is the net of recorded joins and leaves, and
    /// the baseline it needs was always going to come from a sync (see <c>RollupMetrics</c>).
    /// </para>
    /// </remarks>
    GroupInfoChanged = 108,

    // --- Presence (retention: configurable, off by default) ---
    InstanceJoined = 200,
    InstanceLeft = 201,
    AvatarChanged = 202,

    /// <summary>
    /// This person was already here when the reporting client arrived. Their arrival time is
    /// unknown and earlier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a weaker <see cref="InstanceJoined"/> — a different claim. VRChat's log emits an
    /// <c>OnPlayerJoined</c> for everyone already present whenever anybody enters, so a client
    /// that recorded those as arrivals would invent a join for every occupant every time any
    /// moderator walked in. With four to six moderators cycling through a busy instance the noise
    /// dwarfs the signal, and it does it silently: nothing errors, the numbers are simply wrong.
    /// See <c>.agent/research/vrchat-log-events.md</c> §3.
    /// </para>
    /// <para>
    /// Keeping it separate is also what lets deduplication prefer the better source. A moderator
    /// who was present from the start and watched someone arrive reports an exact
    /// <see cref="InstanceJoined"/>; one who walked in later reports this. Same event, different
    /// certainty, and the precise one wins (spec 5.7.1).
    /// </para>
    /// <para>
    /// Carries <c>occurred_before</c> rather than a point in time: the observation bounds the
    /// arrival from above and says nothing about how much earlier it was (spec 5.3).
    /// </para>
    /// </remarks>
    InstancePresenceObserved = 203,

    // --- Discord (M5) ---
    DiscordMemberJoined = 300,
    DiscordMemberLeft = 301,
    DiscordVoiceJoined = 302,
    DiscordVoiceLeft = 303,
    DiscordRoleGranted = 304,
    DiscordRoleRevoked = 305,

    // --- Modbot's own audit entries (spec 5.9.2) ---
    Login = 400,
    LoginFailed = 401,
    PasswordChanged = 402,
    ApiKeyCreated = 403,
    ApiKeyRevoked = 404,
    SettingsChanged = 405,

    // --- Modbot operational events. Presence retention: "a sync failed last March" is not
    //     history anyone needs, and letting it accumulate would bury the records that are. ---
    SyncFailed = 500,
    RateLimitColdStop = 501,
    WafBlocked = 502,
    MigrationApplied = 503,
    RetentionPruned = 504,
    PartitionCreated = 505,

    /// <summary>
    /// Every fact about one subject was erased on request (spec 5.5). Moderation retention: this
    /// one is a record of a deletion, and the record of a deletion is the part that must survive.
    /// It deliberately names no user -- see <c>UserPurger</c>.
    /// </summary>
    UserPurged = 506,
}
