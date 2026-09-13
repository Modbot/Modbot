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
    /// The managed group's own metadata changed. The subject is the <em>group</em>, which is the
    /// only fact type for which that is true.
    /// </summary>
    public const string GroupInfoChanged = "vrchat.group.update";

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

    // ── Discord (M5) ───────────────────────────────────────────────────────────────────────
    public const string DiscordMemberJoined = "discord.member.join";
    public const string DiscordMemberLeft = "discord.member.leave";
    public const string DiscordVoiceJoined = "discord.voice.join";
    public const string DiscordVoiceLeft = "discord.voice.leave";
    public const string DiscordRoleGranted = "discord.role.assign";
    public const string DiscordRoleRevoked = "discord.role.unassign";

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
    public const string SettingsChanged = "modbot.settings.change";

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

    // ── Modbot operational events ──────────────────────────────────────────────────────────
    public const string SyncFailed = "modbot.sync.failed";
    public const string RateLimitColdStop = "modbot.ratelimit.coldstop";
    public const string WafBlocked = "modbot.waf.blocked";
    public const string MigrationApplied = "modbot.migration.applied";
    public const string RetentionPruned = "modbot.retention.pruned";
    public const string PartitionCreated = "modbot.partition.created";

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
