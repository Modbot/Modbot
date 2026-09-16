namespace Modbot.VRChat;

/// <summary>
/// The endpoint classes Modbot budgets separately.
/// </summary>
/// <remarks>
/// <para>
/// VRChat's rate limits are per-endpoint and sometimes per-resource (spec 4.3), so there is no
/// single allowance to reason about. A class is the unit a limit is believed to apply to, and it
/// is what a cold stop halts: a 429 on <see cref="GroupsMembers"/> must not stop audit-log
/// ingestion, which is cheap and is the authoritative fact source.
/// </para>
/// <para>
/// Adding a member here is a decision, not a formality. Spec 4.3.4 is a standing instruction:
/// <strong>ask about the rate limit before building against an endpoint Modbot has not used
/// before</strong>, and never infer one from a neighbouring endpoint.
/// </para>
/// </remarks>
public static class VRChatEndpointClass
{
    /// <summary>The backstop bucket every group-lane call also passes through (spec 4.3.1).</summary>
    public const string Global = "global";

    public const string GroupsMembers = "groups.members";
    public const string GroupsBans = "groups.bans";
    public const string GroupsAuditLog = "groups.auditlog";
    /// <summary>
    /// Which instances the managed group has open right now -- <c>/groups/{groupId}/instances</c>.
    /// Measured at one request per ten seconds by the maintainer on 2026-09-13.
    /// </summary>
    public const string GroupsInstances = "groups.instances";

    /// <summary>Group info and group roles both land here (spec 4.2).</summary>
    public const string GroupsRead = "groups.read";

    public const string GroupsInvites = "groups.invites";

    /// <summary>
    /// A world's own page -- <c>/worlds/{worldId}</c>. Read to learn a world's name, its author
    /// and how many people it holds, so a timeline can say "The Black Cat" instead of
    /// <c>wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b</c>.
    /// </summary>
    /// <remarks>
    /// Measured at one request per second by the maintainer on 2026-09-13. That is a finding, not
    /// a guess -- but it is still its own class, so a 429 here stops world names and nothing else.
    /// </remarks>
    public const string WorldsRead = "worlds.read";

    /// <summary>
    /// One instance by its location -- <c>/instances/{location}</c>. Read for instances Modbot
    /// learns about from a client rather than from the group's own list.
    /// </summary>
    /// <remarks>
    /// Measured at one request per second by the maintainer on 2026-09-13, same as
    /// <see cref="WorldsRead"/>, and budgeted separately for the same reason.
    /// </remarks>
    public const string InstancesRead = "instances.read";

    /// <summary>
    /// Opening a group instance -- <c>POST /instances</c>. Used by the calendar to open an event's
    /// instance a few minutes before it starts (calendar design §4).
    /// </summary>
    /// <remarks>Measured at one request per five seconds by the maintainer on 2026-09-15.</remarks>
    public const string InstancesCreate = "instances.create";

    /// <summary>
    /// Creating, changing and deleting the group's VRChat calendar events --
    /// <c>POST/PUT/DELETE /calendar/{groupId}/…</c> (calendar design §3.1).
    /// </summary>
    /// <remarks>
    /// <strong>Not measured.</strong> VRChat's calendar is known to be strict, and the maintainer
    /// asked for a very gentle default until the real number is known. One class for all three
    /// writes, so they share one allowance.
    /// </remarks>
    public const string CalendarWrite = "calendar.write";

    /// <summary>Reading the group's VRChat calendar events. <strong>Not measured.</strong></summary>
    /// <remarks>Nothing reads the calendar yet (calendar design §3.1); the budget is set so a later read-back has one.</remarks>
    public const string CalendarRead = "calendar.read";

    /// <summary>
    /// The full user object -- <c>GET /users/{userId}</c>. Runs in its own lane, exempt from the
    /// global ceiling (spec 4.2.5).
    /// </summary>
    /// <remarks>
    /// No longer the main profile read. VRChat stopped returning the bio on this call, and what it
    /// still carries alone -- the join date, the full tag list, the status line, the avatar
    /// pictures -- changes slowly or not at all, so it is read rarely and
    /// <see cref="UsersProfile"/> does the frequent work (research:
    /// <c>vrchat-public-profile-findings.md</c>).
    /// </remarks>
    public const string UsersRead = "users.read";

    /// <summary>
    /// A person's public profile -- <c>GET /profile/{userId}</c>. The main profile read: bio,
    /// pronouns, display name and age verification.
    /// </summary>
    /// <remarks>
    /// <strong>The same rate as <see cref="UsersRead"/>, and its own budget</strong> -- the
    /// maintainer's answer on 2026-09-15 to spec 4.3.4's standing question. Its own lane too, so a
    /// profile read and a user read never queue behind one another and neither can starve the
    /// other: two budgets that shared a lane would share a queue, which is most of what a budget is.
    /// </remarks>
    public const string UsersProfile = "users.profile";

    /// <summary>
    /// Which groups an account belongs to, and what it may do in each —
    /// <c>/users/{id}/groups</c> and <c>/users/{id}/groups/permissions</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The limit here is not measured.</strong> These endpoints are used by one
    /// interactive step — picking the managed group during onboarding — and nothing else calls
    /// them. Rather than assume they behave like a neighbour, they get their own class, so a 429
    /// on either cold-stops <em>only</em> group selection: not profile fetches, not member sync,
    /// not the audit log. That is what spec 4.3's per-endpoint model is for, and what makes an
    /// unmeasured endpoint safe to use at all.
    /// </para>
    /// <para>
    /// The budget is set to the most conservative plausible neighbour (<c>groups.read</c>, spec
    /// 4.2) because these return group data. If a real limit is ever measured, change it here —
    /// and until then, treat the number as a guess that is deliberately too low rather than as a
    /// finding.
    /// </para>
    /// </remarks>
    public const string UsersGroups = "users.groups";

    /// <summary>
    /// Finding a user Modbot does not already know. Severely limited and interactive-only
    /// (spec 4.2.5.1) — never called by a sync, a background job or a scheduled task.
    /// </summary>
    public const string UsersSearch = "users.search";

    /// <summary>Bans, kicks, role changes — interactive, low volume, preempts sync.</summary>
    public const string ModerationWrite = "moderation.write";

    /// <summary>Login and re-login only.</summary>
    public const string Auth = "auth";

    /// <summary>
    /// <c>GET /auth</c>, Verify Auth Token: whether a stored session still works, without signing in
    /// again (spec 4.1.2). Asked on start-up and after a 401, and nowhere else.
    /// </summary>
    /// <remarks>
    /// Its own class, so a 429 here stops the session check and not the sign-in or anything else.
    /// <strong>The limit is not measured</strong>; see its budget in <c>VRChatRateLimits</c>.
    /// </remarks>
    public const string AuthVerify = "auth.verify";
}

/// <summary>
/// Identifies one outbound call for budgeting and logging.
/// </summary>
/// <param name="Class">An endpoint class from <see cref="VRChatEndpointClass"/>.</param>
/// <param name="ResourceId">
/// The id a per-resource limit would be scoped to — usually the managed group. Optional because
/// not every class is resource-scoped, and never validated: VRChat ids are opaque (spec 3.1.1).
/// </param>
/// <param name="Operation">
/// The SDK method being called, for the HTTP log. Presentation only; nothing branches on it.
/// </param>
/// <remarks>
/// The endpoint is a required argument of <see cref="IVRChatGate.ExecuteAsync{T}"/> rather than
/// something inferred from the callback, because a callback is an opaque delegate and the limiter
/// cannot see which URL it will reach. Naming the class at the call site is the point at which an
/// implementer is forced to have asked spec 4.3.4's question.
/// </remarks>
public readonly record struct VRChatEndpoint(string Class, string? ResourceId = null, string? Operation = null)
{
    public override string ToString() =>
        ResourceId is null ? Class : $"{Class}:{ResourceId}";
}

/// <summary>
/// Who a call is for. The queue is the budget allocator, not a nicety (spec 4.3.3).
/// </summary>
public enum VRChatCallPriority
{
    /// <summary>Sync jobs. Yields to anything a human is waiting on.</summary>
    Background = 0,

    /// <summary>A moderator is watching a spinner. Preempts queued background work.</summary>
    Interactive = 1,
}
