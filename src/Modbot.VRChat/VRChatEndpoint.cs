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
    /// <summary>
    /// The backstop bucket every background call also passes through (spec 4.3.1): the sweeps,
    /// the audit log, places, the calendar. Not the calls a person is waiting on -- those pass
    /// through <see cref="Interactive"/> instead (spec 4.3.5).
    /// </summary>
    public const string Global = "global";

    /// <summary>
    /// The backstop bucket for the calls a moderator is waiting on: kicks, bans, unbans and the
    /// other writes a person presses a button for. Sized to the room spec 4.2 leaves under the
    /// global ceiling once background sync has its share, so a moderator's action never waits
    /// for a token the sweeps just took (spec 4.3.5).
    /// </summary>
    /// <remarks>
    /// Never acquired directly. A class names it as its backstop the way the background classes
    /// name <see cref="Global"/>; a 429 on one of those classes halves this bucket as an
    /// ancestor and halves <see cref="Global"/> as evidence, the way <see cref="UsersRead"/>
    /// already does.
    /// </remarks>
    public const string Interactive = "interactive";

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

    /// <summary>
    /// Inviting somebody to the managed group — <c>POST /groups/{groupId}/invites</c>
    /// (auto-invites design §5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One request every thirty seconds, across the whole deployment.</strong> That is the
    /// maintainer's answer to spec 4.3.4's standing question, given on 2026-09-19, and not a
    /// number read off a neighbour. It is also the user-facing limit of the feature: no group is
    /// invited into faster than that, whatever is happening in its instances.
    /// </para>
    /// <para>
    /// Counted against <see cref="Global"/> rather than <see cref="Interactive"/>. Nobody is
    /// waiting on an invite: a timer decided to send it. The room spec 4.2 reserves is for the
    /// moderator who <em>is</em> waiting, and letting a loop that runs forever draw from it would
    /// put Modbot's own invites in front of a moderator's ban — the contention spec 4.3.5 was
    /// written to end.
    /// </para>
    /// </remarks>
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
    /// <remarks>Read only to look for an earlier copy before a create that got no answer is sent again (calendar design §3.1).</remarks>
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
    /// One person, read because somebody is waiting for the answer: the link check that reads a
    /// bio for the code in it, and any other read of one user by id that a person presses a
    /// button for. The same two endpoints as <see cref="UsersRead"/> and
    /// <see cref="UsersProfile"/>, on a budget and a lane of their own (spec 4.3.5).
    /// </summary>
    /// <remarks>
    /// Split from the two sync classes so that a cold stop earned by the background profile sync
    /// never stops a person linking their account, and a person's lookups never spend the sync's
    /// allowance. The rate is foundation spec 4.2.5's original 1 req/s for the users lane --
    /// deliberately under the 3.5 req/s the maintainer measured, because these reads land on the
    /// same endpoints the two sync classes already use at that rate.
    /// </remarks>
    public const string UsersLookup = "users.lookup";

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
    /// <remarks>
    /// Declared by spec 4.2 and still unused. The group kick, ban and unban a moderator presses in
    /// the Modbot UI are <see cref="GroupsModerate"/> instead: the maintainer asked for those three
    /// to be paced on a lane of their own until somebody measures them, and a class that shared a
    /// bucket with role changes would have handed them a number nobody has checked. Passes
    /// through the <see cref="Interactive"/> backstop with them, so that when it is used it is
    /// paced with the rest of what a moderator presses rather than behind the sweeps.
    /// </remarks>
    public const string ModerationWrite = "moderation.write";

    /// <summary>
    /// Kicking, banning and unbanning a person in the managed group —
    /// <c>DELETE /groups/{groupId}/members/{userId}</c>, <c>POST /groups/{groupId}/bans</c>,
    /// <c>DELETE /groups/{groupId}/bans/{userId}</c> (M4 §4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Not measured.</strong> Spec 4.3.4 forbids inferring a limit from a neighbouring
    /// endpoint, and nobody has asked VRChat what these three allow. The maintainer set the
    /// starting number deliberately low — one request per two seconds, shared by all three — to be
    /// replaced when the real one is known. Treat it as a guess that is meant to be too low.
    /// </para>
    /// <para>
    /// Its own lane, so a moderator pressing Ban never waits behind a member sweep and never holds
    /// one up; resource-scoped on the group; counted against the <see cref="Interactive"/>
    /// backstop rather than <see cref="Global"/> since 2026-09-17, because the global bucket is
    /// the one the sweeps keep empty and a ban was waiting for its next token behind them
    /// (spec 4.3.5). A 429 cold stops this class alone, and is never retried (spec 4.3.1) — the
    /// action simply failed, and the moderator is told so.
    /// </para>
    /// </remarks>
    public const string GroupsModerate = "groups.moderate";

    /// <summary>
    /// The people waiting to be let into the managed group —
    /// <c>GET /groups/{groupId}/requests</c> (join requests design §2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Not measured.</strong> Nobody has asked VRChat what this allows, and spec 4.3.4
    /// forbids borrowing a neighbour's number, so it is budgeted at <c>groups.read</c>'s
    /// 0.2 req/s — one request per five seconds — which is the conservative reading spec 4.3.4.1
    /// already applies to an unmeasured endpoint that returns group data. Treat it as a guess
    /// that is meant to be too low.
    /// </para>
    /// <para>
    /// Its own lane and its own class, so a 429 here stops the Requests screen and nothing else:
    /// not the sweeps, and not <see cref="GroupsRequestsAnswer"/>, which a moderator needs most
    /// at the moment a list read has just been refused. One request per page a moderator asks
    /// for; nothing polls it.
    /// </para>
    /// </remarks>
    public const string GroupsRequests = "groups.requests";

    /// <summary>
    /// Approving or rejecting one join request —
    /// <c>PUT /groups/{groupId}/requests/{userId}</c> (join requests design §4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Not measured.</strong> One request per two seconds, shared by approve and reject:
    /// deliberately low, and the same starting point the maintainer chose for the other group
    /// writes rather than a number inferred from them. It needs confirming.
    /// </para>
    /// <para>
    /// Apart from <see cref="GroupsModerate"/> on purpose. Working a join queue is many small
    /// writes in a row and a ban is one; if the two shared a bucket, a moderator clearing a
    /// backlog of requests could cold stop the ban button, which is the one action that must
    /// still work. Counted against the <see cref="Interactive"/> backstop, scoped to the group,
    /// and never retried on a 429 (spec 4.3.1) — the answer simply did not reach VRChat.
    /// </para>
    /// </remarks>
    public const string GroupsRequestsAnswer = "groups.requests.answer";

    /// <summary>
    /// A request forwarded to VRChat as it was written, on the service account's session --
    /// <c>/api/proxy/vrchat/…</c> (VRChat proxy design). Any endpoint, any method.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its own class and lane, so a script's traffic never queues in front of a sweep or a ban
    /// and a 429 it earns stops the proxy and nothing else. Counted against <see cref="Global"/>,
    /// because these requests go out as the service account and the account-wide limit the
    /// backstop stands for applies to them like anything else.
    /// </para>
    /// <para>
    /// <strong>The limiter cannot tell which VRChat endpoint a proxied request reaches</strong>,
    /// so a proxied read of the member list spends this bucket and not <c>groups.members</c>. A
    /// 429 earned that way is recorded here, and the sweep's own bucket learns nothing. The cap is
    /// kept at the bottom of spec 4.3.4's provisional range for that reason: the proxy is for
    /// trying an endpoint and for one-off scripts, not for sweeping anything.
    /// </para>
    /// </remarks>
    public const string Proxy = "proxy";

    /// <summary>
    /// A request forwarded to VRChat with the caller's own VRChat cookie, not the service
    /// account's. A different account, paced apart so that its 429s and Modbot's never mix.
    /// </summary>
    /// <remarks>
    /// Still paced, because it leaves from this host's address and a burst from one caller could
    /// have Cloudflare block the host for the service account too. Not counted against
    /// <see cref="Global"/> -- that backstop is about the service account's own allowance -- and a
    /// 429 here is not evidence about the service account either, so it halves nothing but its
    /// own bucket.
    /// </remarks>
    public const string ProxyPassthrough = "proxy.passthrough";

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
