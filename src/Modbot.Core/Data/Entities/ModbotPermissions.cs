namespace Modbot.Core.Data.Entities;

/// <summary>
/// What a <see cref="ModbotUser"/> is allowed to do, as a bitfield.
/// </summary>
/// <remarks>
/// <para>
/// Foundation spec section 7.3. A bitfield rather than a roles table because the deployment is
/// single-tenant and single-group (§2.4): there is no tenancy to scope a role to, and a staff list
/// of a dozen people does not need a join.
/// </para>
/// <para>
/// <strong>These values are persisted. Never renumber one.</strong> Changing a flag's bit silently
/// re-grants or revokes that permission on every existing account, and nothing errors.
/// <c>ModbotPermissionsTests</c> pins the assignments.
/// </para>
/// <para>
/// Flags for milestones not yet built are allocated here deliberately, for the same reason: a bit
/// claimed later would have to avoid every bit already written into the database, and that is
/// easier to get right once, now, than under pressure later.
/// </para>
/// </remarks>
[Flags]
public enum ModbotPermissions : long
{
    None = 0,

    // --- Reading ---
    ViewMembers = 1L << 0,
    ViewProfile = 1L << 1,
    ViewAnalytics = 1L << 2,

    /// <summary>Moderation history: bans, kicks, role changes (spec 5.9.4).</summary>
    ViewAuditLog = 1L << 3,

    /// <summary>
    /// Modbot's own operational record: sync failures, settings changes, API keys. Separate from
    /// <see cref="ViewAuditLog"/> because a moderator who should see bans does not automatically
    /// need to see that the owner reconfigured SMTP (spec 5.9.4).
    /// </summary>
    ViewOperationalLog = 1L << 4,

    // --- Administration ---
    ManageSettings = 1L << 5,
    ManageUsers = 1L << 6,
    ManageApiKeys = 1L << 7,

    // --- Moderation actions (M4, spec 8.3). Reserved now so the bits stay stable. ---
    Kick = 1L << 8,
    Ban = 1L << 9,
    Unban = 1L << 10,
    Warn = 1L << 11,
    BulkAction = 1L << 12,

    /// <summary>
    /// Deliberately separate from the action flags: the people being reviewed should not be the
    /// people closing the reviews (M4 spec 8.3).
    /// </summary>
    ReviewTickets = 1L << 13,

    EditClassifications = 1L << 14,

    // --- Evidence (evidence design §14) ---

    /// <summary>
    /// Open the evidence attached to a case file — the screenshots and video themselves.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ViewAuditLog"/>, and more restrictive on purpose. The audit log
    /// says a ban happened and who did it; the evidence may be video of the person it happened
    /// to. A moderator who should see that a decision was made does not automatically need to
    /// watch the recording of it, and every access is itself recorded.
    /// </remarks>
    ViewEvidence = 1L << 15,

    /// <summary>Attach evidence to a case file.</summary>
    /// <remarks>
    /// Held by whoever files ban reports. Uploading is the act that puts somebody else's image on
    /// this deployment's disk, so it is granted rather than implied.
    /// </remarks>
    UploadEvidence = 1L << 16,

    /// <summary>
    /// Destroy the bytes of a piece of evidence, keeping the record that it existed.
    /// </summary>
    /// <remarks>
    /// The narrowest of the three and the only irreversible one. The store has no versioning and
    /// no undelete — a destroy is final — and the reason it exists at all is a lawful erasure
    /// request rather than routine tidying. What survives is "this case had a video and an
    /// administrator destroyed it on this date", because a case file that looks like it never had
    /// evidence is indistinguishable from one nobody ever documented.
    /// </remarks>
    DestroyEvidence = 1L << 17,

    // --- VRChat user records (user profile sync design §4) ---

    /// <summary>
    /// Set or clear the "18+ verified" flag on a VRChat user's record by hand.
    /// </summary>
    /// <remarks>
    /// Its own flag rather than part of <see cref="ManageUsers"/> (which is about Modbot's own
    /// accounts) or <see cref="EditClassifications"/>. The flag is sticky: a sync sets it and
    /// nothing automatic ever clears it, so clearing is a deliberate human decision about a
    /// person's record, recorded as a fact naming who made it. That deserves to be granted on
    /// purpose rather than arriving bundled with something else.
    /// </remarks>
    EditAgeVerification = 1L << 18,

    // --- Roles (accounts and access design §3) ---

    /// <summary>
    /// Create, edit and delete roles. Separate from <see cref="ManageUsers"/> because handing
    /// somebody a role is a smaller decision than deciding what the role means.
    /// </summary>
    /// <remarks>Bit 18 belongs to the user-profile work (<c>EditAgeVerification</c>), hence 19.</remarks>
    ManageRoles = 1L << 19,

    // --- Live (M3 section 7.4) ---

    /// <summary>
    /// See the group's open instances right now, their head counts, which moderators are in each
    /// and who is there.
    /// </summary>
    /// <remarks>
    /// Its own flag rather than part of <see cref="ViewAnalytics"/>. Analytics are totals after the
    /// fact; this is where people are standing at this moment, which is a narrower thing to hand
    /// out. Not added to the built-in Moderator or Viewer roles by this change: an operator grants
    /// it on purpose, and Administrator already holds it.
    /// </remarks>
    ViewLiveInstances = 1L << 20,

    // --- AI (AI chat design §4) ---

    /// <summary>
    /// Ask questions on the Chat page. Every tool the model can use runs with this person's own
    /// permissions, so this grants a way of asking, never anything more to see.
    /// </summary>
    /// <remarks>
    /// Not added to the built-in Moderator or Viewer roles: using Chat sends group data to the
    /// provider the operator chose, so it is granted on purpose. Administrator already holds it.
    /// </remarks>
    UseAiChat = 1L << 21,

    // --- Discord account linking (Discord account linking design §11) ---

    /// <summary>
    /// End somebody else's link between their Discord and VRChat accounts.
    /// </summary>
    /// <remarks>
    /// Its own flag rather than part of <see cref="ManageUsers"/>, which is about Modbot's own
    /// accounts, or <see cref="ManageSettings"/>. Ending a link takes away the roles Modbot gave the
    /// member, which is a moderation decision about that member. Seeing a link needs
    /// <see cref="ViewProfile"/> only. Not added to the built-in roles. Bit 21 belongs to
    /// <see cref="UseAiChat"/>, hence 22.

    /// </remarks>
    ManageDiscordLinks = 1L << 22,

    /// <summary>
    /// Not stopped by AI spend limits set on this account or on any of its roles.
    /// </summary>
    /// <remarks>
    /// The limit for everyone together still applies. That one is the operator's ceiling on the
    /// bill, and a permission that could spend past it would make it a suggestion (AI chat design
    /// §10). Not in the built-in Moderator or Viewer roles; Administrator holds it.
    /// </remarks>
    UseAiPastLimits = 1L << 23,

    // --- Discord messages (M5 spec §5.1) ---

    /// <summary>
    /// Read the Discord messages Modbot has stored, deleted ones included.
    /// </summary>
    /// <remarks>
    /// Its own flag rather than part of <see cref="ViewProfile"/> or <see cref="ViewAnalytics"/>.
    /// Analytics only count messages, and a profile is what Modbot recorded about somebody; this is
    /// everything the person wrote, including what they deleted. A moderator who should see that
    /// somebody was timed out does not automatically need to read their conversations. Not added to
    /// the built-in roles. Bit 23 belongs to <see cref="UseAiPastLimits"/>, hence 24.
    /// </remarks>
    ReadDiscordMessages = 1L << 24,

    // --- Calendar (calendar design §7) ---

    /// <summary>See the calendar page: every event, where it is published and how that went.</summary>
    ViewCalendar = 1L << 25,

    /// <summary>
    /// Create, edit, cancel and delete events, and see and replace the calendar feed link.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ViewCalendar"/> because an event can open an instance by itself and
    /// posts to VRChat and Discord in the group's name. Not added to the built-in roles.
    /// </remarks>
    ManageCalendar = 1L << 26,

    // --- The VRChat proxy (VRChat proxy design) ---

    /// <summary>
    /// Send requests to VRChat's own API through Modbot, as the service account, at
    /// <c>/api/proxy/vrchat/…</c>.
    /// </summary>
    /// <remarks>
    /// Its own flag rather than part of <see cref="ManageSettings"/> or <see cref="Ban"/>: a
    /// proxied request can reach any VRChat endpoint the service account can, read or write,
    /// which is more than any one Modbot permission grants. Not added to the built-in roles;
    /// Administrator already holds it. The proxy's own switch, off by default, gates the route
    /// for everyone.
    /// </remarks>
    UseVRChatProxy = 1L << 27,

    // --- Giveaways (giveaways design §7) ---

    /// <summary>See the Giveaways page: the rules, the entrants, the draws and their seeds.</summary>
    /// <remarks>
    /// Its own flag rather than part of <see cref="ViewMembers"/> or <see cref="ViewAnalytics"/>.
    /// An entrant list is a list of named people with a number beside each saying how much time
    /// they spend here, which is a narrower thing to hand out than either. Not added to the
    /// built-in roles.
    /// </remarks>
    ViewGiveaways = 1L << 28,

    /// <summary>Create, edit, open, close, draw and cancel giveaways.</summary>
    /// <remarks>
    /// Separate from <see cref="ViewGiveaways"/> for the same reason the calendar's pair is
    /// separate: a giveaway posts in the group's name, and drawing one decides who gets something.
    /// Not added to the built-in roles.
    /// </remarks>
    RunGiveaways = 1L << 29,

    // --- Importing old data (import design §4) ---

    /// <summary>
    /// Upload a file of another platform's records and have Modbot write each one into the fact
    /// log, and see past imports.
    /// </summary>
    /// <remarks>
    /// Its own flag rather than part of <see cref="ManageSettings"/>, which is what it used to
    /// ride on. Changing a setting changes what Modbot does next; an import writes history that
    /// did not happen inside Modbot at all — bans, warnings and notes dated years ago, about
    /// named people, in the same log a moderator reads to decide what somebody has done before.
    /// Nothing else Modbot offers can put a claim about a person's past into that log in bulk,
    /// and the operator who should be able to change the retention window is not automatically
    /// the person who should be able to do that. Not added to the built-in roles.
    /// </remarks>
    ImportOldData = 1L << 30,

    // --- Join requests (join requests design §6) ---
    //
    // Bits 32 and 33. Bit 31 was spoken for by work in flight when these were added, and a bit
    // claimed twice is the one mistake this enum cannot recover from.

    /// <summary>
    /// See the people waiting to be let into the group, and who each of them is.
    /// </summary>
    /// <remarks>
    /// Its own flag rather than part of <see cref="ViewMembers"/>. The member list is who is
    /// already in; this is a queue of people asking, read live from VRChat, and every read of it
    /// spends VRChat request budget that the member list does not. Not added to the built-in
    /// roles; Administrator already holds it.
    /// </remarks>
    ViewJoinRequests = 1L << 32,

    /// <summary>Approve or reject a join request.</summary>
    /// <remarks>
    /// Separate from <see cref="ViewJoinRequests"/> the way <see cref="ManageCalendar"/> is
    /// separate from <see cref="ViewCalendar"/>: approving one puts a stranger inside the group.
    /// Deliberately not folded into <see cref="Kick"/> or <see cref="Ban"/> either — deciding who
    /// gets in is a different job from removing somebody who is already in, and plenty of groups
    /// hand the first out more freely than the second.
    /// </remarks>
    AnswerJoinRequests = 1L << 33,

    /// <summary>
    /// Satisfies every requirement, including flags added after this account was created. Checked
    /// explicitly rather than defined as an OR of the others, so a new flag does not quietly go
    /// ungranted to the one account that is supposed to have everything.
    /// </summary>
    Administrator = 1L << 62,
}
