namespace Modbot.VRChat.RateLimiting;

/// <summary>
/// The per-class budget: what Modbot believes VRChat allows, and the fraction of it Modbot uses.
/// </summary>
/// <param name="Name">The endpoint class this governs.</param>
/// <param name="Lane">
/// Calls in a lane are issued one at a time and share a queue. Lanes exist because
/// <c>users.read</c> is governed separately and far more permissively (spec 4.2.5): holding it
/// behind the group queue would make its own 1 req/s allowance unreachable.
/// </param>
/// <param name="HardMaxPerSecond">
/// Spec 4.2's authoritative pacing cap. Configuration may lower the effective rate but never
/// raise it past this, and the clamp is applied on write rather than in the UI (spec 4.2.1).
/// </param>
/// <param name="DefaultCeilingPerSecond">
/// The operator-configurable estimate of VRChat's real limit. Modbot runs at
/// <see cref="RateLimitOptions.DefaultFraction"/> of it and never bursts to it (spec 4.3.1).
/// </param>
/// <param name="Backstop">
/// The backstop bucket that also has to grant a token, or null for none. Background sync passes
/// through <c>global</c>; the calls a moderator presses a button for pass through
/// <c>interactive</c>, so they never wait for a token the sweeps just took (spec 4.3.5); the
/// user reads spec 4.2.5 exempts on evidence pass through nothing.
/// </param>
/// <param name="ServiceAccount">
/// Whether calls in this class go out as Modbot's own VRChat account. False only for the proxy's
/// pass-through, which carries a caller's cookie: a 429 there says nothing about the service
/// account, so it halves nothing but its own bucket.
/// </param>
/// <param name="ResourceScoped">
/// Whether a third, per-resource bucket applies. Some VRChat limits key on a group or user id
/// rather than the account, and in a single-group appliance this dimension usually collapses to
/// one — but the model must keep it, because the appliance assumption must not be baked in.
/// </param>
/// <param name="BurstTokens">
/// Bucket capacity. One almost everywhere, which is what "at most one request per interval"
/// (spec 4.2) means. Authentication is the exception: a login is a fixed three-call sequence and
/// pacing it out over half a minute would make every restart feel broken.
/// </param>
public sealed record RateLimitClassOptions(
    string Name,
    string Lane,
    double HardMaxPerSecond,
    double DefaultCeilingPerSecond,
    string? Backstop = VRChatEndpointClass.Global,
    bool ResourceScoped = false,
    int BurstTokens = 1,
    bool ServiceAccount = true)
{
    /// <summary>Whether the global backstop bucket also has to grant a token.</summary>
    public bool CountsAgainstGlobal => Backstop == VRChatEndpointClass.Global;
}

/// <summary>
/// Everything the limiter is allowed to guess about someone else's undocumented system.
/// </summary>
/// <remarks>
/// Every number here is an estimate about a system with no published limit, no
/// <c>Retry-After</c>, and a penalty that grows when you retry (spec 4.3). They are therefore all
/// settings rather than constants, and the asymmetry runs one way: overshooting costs an opaque
/// multi-minute outage, undershooting costs slightly staler data.
/// </remarks>
public sealed record RateLimitOptions
{
    /// <summary>Spec 4.3.1's proposed default: run at 60% of the estimated ceiling.</summary>
    public const double DefaultFraction = 0.6;

    /// <summary>
    /// How long a cold-stopped bucket issues absolutely nothing before its first probe
    /// (spec 4.3.1, proposed 15 minutes).
    /// </summary>
    /// <remarks>
    /// Fifteen rather than the old implementation's three. That one waited three minutes and then
    /// retried through a <c>goto</c>, which against a ~10 minute penalty is three or more
    /// premature probes at 45–80 s of self-inflicted extension each (spec 4.3.4).
    /// </remarks>
    public TimeSpan ColdStopBase { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Added to the wait after each failed probe. Linear, not exponential: under a per-probe
    /// penalty the objective is to minimise the number of probes, not the total wait, and
    /// exponential backoff optimises for exactly the wrong one.
    /// </summary>
    public TimeSpan ColdStopIncrement { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// After this many consecutive failed probes the bucket stays stopped and the operator is
    /// alerted. Something is wrong that waiting will not fix.
    /// </summary>
    public int MaxProbeFailures { get; init; } = 4;

    /// <summary>Multiplicative decrease on a 429. Halve; the estimate was wrong.</summary>
    public double DecreaseFactor { get; init; } = 0.5;

    /// <summary>
    /// Additive increase per hour of sustained success. Recovery is measured in hours because the
    /// loss signal is far more punishing than a dropped packet.
    /// </summary>
    public double IncreasePerHour { get; init; } = 0.1;

    /// <summary>
    /// Floor for the adapted budget, so a run of 429s cannot drive a bucket to a rate that would
    /// never recover — an effective rate of zero produces no successes, and only successes
    /// restore budget.
    /// </summary>
    public double MinimumBudgetMultiplier { get; init; } = 1.0 / 16.0;

    /// <summary>
    /// How stale the persisted token count may get while nothing else is being written. Bounds
    /// how much allowance a crash can hand back (spec 4.3.2).
    /// </summary>
    public TimeSpan StateFlushInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The most requests that could count as a VRChat sign-in Modbot will send in any rolling hour
    /// (spec 4.1.2). Configuration may lower it; nothing raises it.
    /// </summary>
    /// <remarks>
    /// VRChat allows about four or five an hour and answers the next with an hour-long block, so
    /// the ceiling sits at the bottom of that range rather than in it. Sits beside the
    /// <c>auth</c> bucket in <see cref="VRChatRateLimits.Defaults"/>, which paces the same requests
    /// over seconds; this counts them over the hour.
    /// </remarks>
    public const int MaxSignInsPerHour = 4;

    private readonly int _signInsPerHour = MaxSignInsPerHour;

    /// <summary>See <see cref="MaxSignInsPerHour"/>. Clamped to between one and that ceiling on write.</summary>
    public int SignInsPerHour
    {
        get => _signInsPerHour;
        init => _signInsPerHour = Math.Clamp(value, 1, MaxSignInsPerHour);
    }

    /// <summary>The per-class budgets, keyed by endpoint class.</summary>
    public IReadOnlyDictionary<string, RateLimitClassOptions> Classes { get; init; } =
        VRChatRateLimits.Defaults;
}

/// <summary>
/// The default budgets, copied from spec 4.2's table and spec 4.3.4's provisional rates.
/// </summary>
/// <remarks>
/// <para>
/// The ceilings are expressed as <em>estimates of VRChat's limit</em>, and the effective rate is
/// the estimate times the fraction. The defaults are therefore the spec 4.2 rate divided by
/// <see cref="RateLimitOptions.DefaultFraction"/>, so that out of the box Modbot runs at exactly
/// the pacing spec 4.2 authorises while still having a fraction an operator can turn down.
/// </para>
/// <para>
/// Where spec 4.2 and spec 4.3.4 disagree — bans and the audit log — spec 4.3.4 settles it
/// explicitly: the spec 4.2 pacing caps and the 2 req/s global ceiling are authoritative, and the
/// provisional table only covers classes spec 4.2 does not schedule.
/// </para>
/// </remarks>
public static class VRChatRateLimits
{
    private const double Fraction = RateLimitOptions.DefaultFraction;

    /// <summary>Requests per second implied by "one request per <paramref name="seconds"/>".</summary>
    private static double PerSeconds(double seconds) => 1.0 / seconds;

    /// <summary>The estimate whose 60% is <paramref name="rate"/>.</summary>
    private static double CeilingFor(double rate) => rate / Fraction;

    /// <summary>Calls in this lane pass through the global backstop bucket.</summary>
    public const string GroupLane = "group";

    /// <summary>Spec 4.2.5's separate lane for the full user object.</summary>
    public const string UsersLane = "users";

    /// <summary>
    /// The public profile, apart from the user object. Same rate, separate queue, so the frequent
    /// read and the rare one never wait on each other.
    /// </summary>
    public const string UsersProfileLane = "users.profile";

    /// <summary>
    /// Its own queue, so an interactive onboarding step is never held behind a full member sweep
    /// -- and never holds one up either.
    /// </summary>
    public const string UsersGroupsLane = "users.groups";

    /// <summary>Spec 4.2.5.1: interactive search, one request per 3.5 seconds, its own lane.</summary>
    public const string SearchLane = "search";

    /// <summary>Login and re-login, which must not queue behind a group sync.</summary>
    public const string AuthLane = "auth";

    /// <summary>
    /// Worlds and instances -- the places a person can be, as opposed to the people. Its own
    /// queue so that naming a world a moderator is looking at never waits behind a member sweep.
    /// </summary>
    public const string PlacesLane = "places";

    /// <summary>
    /// VRChat calendar writes. Its own queue, because a write waits up to a minute for its turn and
    /// must not hold anything else up while it does.
    /// </summary>
    public const string CalendarLane = "calendar";

    /// <summary>VRChat calendar reads, apart from the writes for the same reason.</summary>
    public const string CalendarReadLane = "calendar.read";

    /// <summary>Opening instances for calendar events.</summary>
    public const string InstancesCreateLane = "instances.create";

    /// <summary>
    /// Kicks, bans and unbans a moderator presses. Its own queue, so the one thing a human is
    /// actually waiting on is never behind a member sweep, and a slow moderation write never holds
    /// the sweeps up either.
    /// </summary>
    public const string GroupsModerateLane = "groups.moderate";

    /// <summary>
    /// Reading the people waiting to be let into the group. Its own queue, so opening the
    /// Requests screen never waits behind a member sweep.
    /// </summary>
    public const string GroupsRequestsLane = "groups.requests";

    /// <summary>
    /// Approving and rejecting join requests. Apart from the list read, so the answer a moderator
    /// just pressed is never behind a refresh of the list they pressed it on.
    /// </summary>
    public const string GroupsRequestsAnswerLane = "groups.requests.answer";

    /// <summary>
    /// Inviting somebody to the group. Its own queue so that an invite waiting thirty seconds for
    /// its token holds nothing else up, and nothing else holds it up either.
    /// </summary>
    public const string GroupInvitesLane = "groups.invites";

    /// <summary>
    /// The backstop for what a moderator presses. Never entered as a queue -- a backstop is only
    /// ever an ancestor -- but every class names a lane, and this one names its own so nothing
    /// reads it as belonging to the group queue.
    /// </summary>
    public const string InteractiveLane = "interactive";

    /// <summary>
    /// One person read because somebody is waiting: its own queue, apart from both sync reads,
    /// so a link check never waits behind a profile sweep and never holds one up.
    /// </summary>
    public const string UsersLookupLane = "users.lookup";

    /// <summary>Requests forwarded as the service account, one at a time, apart from everything Modbot does itself.</summary>
    public const string ProxyLane = "proxy";

    /// <summary>Requests forwarded with a caller's own cookie: a different account, a different queue.</summary>
    public const string ProxyPassthroughLane = "proxy.passthrough";

    /// <summary>
    /// The classes spec 4.2's table schedules as background sync, in its order.
    /// </summary>
    /// <remarks>
    /// Exactly the rows spec 4.2 sums to 1.425 req/s, and no others. The settings screen shows
    /// that sum against the 2 req/s ceiling so an operator can see how much room they
    /// are leaving (spec 4.2.1) — a figure that would mean nothing if it also counted classes
    /// nothing schedules. What a moderator presses is what the room left is <em>for</em>, not
    /// part of what consumes it, and since 2026-09-17 it has that room as a bucket of its own:
    /// <see cref="InteractiveRoomPerSecond"/>. <c>users.read</c> is exempt from the ceiling
    /// entirely (spec 4.2.5).
    /// </remarks>
    public static IReadOnlyList<string> Scheduled { get; } =
    [
        VRChatEndpointClass.GroupsMembers,
        VRChatEndpointClass.GroupsBans,
        VRChatEndpointClass.GroupsAuditLog,
        VRChatEndpointClass.GroupsInstances,
        VRChatEndpointClass.GroupsRead,
    ];

    /// <summary>Spec 4.2's global ceiling: two requests a second across background sync.</summary>
    public const double GlobalCeilingPerSecond = 2.0;

    /// <summary>
    /// What spec 4.2's scheduled classes add up to at their caps: members and bans at one per 2 s,
    /// the audit log at one per 8 s, instances at one per 10 s, group info and roles at 0.2.
    /// </summary>
    /// <remarks>
    /// Written out rather than summed from <see cref="Defaults"/>, because the table below needs
    /// the number before the table exists: the <c>interactive</c> backstop is the ceiling minus
    /// this. <c>BudgetCoverageTests</c> holds the two in agreement.
    /// </remarks>
    public const double ScheduledTotalPerSecond = 0.5 + 0.5 + 0.125 + 0.1 + 0.2;

    /// <summary>
    /// The room spec 4.2 leaves under the ceiling once background sync has its share, which it
    /// says is reserved for interactive work: 0.575 req/s. The <c>interactive</c> backstop's cap
    /// (spec 4.3.5).
    /// </summary>
    public const double InteractiveRoomPerSecond = GlobalCeilingPerSecond - ScheduledTotalPerSecond;

    public static IReadOnlyDictionary<string, RateLimitClassOptions> Defaults { get; } =
        new Dictionary<string, RateLimitClassOptions>(StringComparer.Ordinal)
        {
            // The backstop for background sync. Not the model -- it exists because an
            // account-wide limit may also apply and Modbot cannot see it (spec 4.3.1). What a
            // moderator presses no longer passes through it: see `interactive` below.
            [VRChatEndpointClass.Global] = new(
                VRChatEndpointClass.Global, GroupLane,
                HardMaxPerSecond: GlobalCeilingPerSecond, DefaultCeilingPerSecond: CeilingFor(GlobalCeilingPerSecond),
                Backstop: null),

            // The backstop for what a moderator is waiting on (spec 4.3.5). Until 2026-09-17 a
            // ban drew its token from `global`, which the sweeps keep empty: the priority queue
            // is per lane, and the global bucket has no queue at all, so a moderator's action
            // waited for the next global token and then raced the sweeps for it. This bucket is
            // exactly the room spec 4.2 says the ceiling leaves for interactive work, so the
            // ceiling still holds -- background sync is capped at the scheduled sum through
            // `global`, and moderators at the rest through this -- and the two never share a
            // token. A 429 on a class under it halves this bucket as an ancestor and `global` as
            // evidence, like every class the global bucket does not chain.
            [VRChatEndpointClass.Interactive] = new(
                VRChatEndpointClass.Interactive, InteractiveLane,
                HardMaxPerSecond: InteractiveRoomPerSecond, DefaultCeilingPerSecond: CeilingFor(InteractiveRoomPerSecond),
                Backstop: null),

            [VRChatEndpointClass.GroupsMembers] = new(
                VRChatEndpointClass.GroupsMembers, GroupLane,
                HardMaxPerSecond: PerSeconds(2), DefaultCeilingPerSecond: CeilingFor(PerSeconds(2)),
                ResourceScoped: true),

            [VRChatEndpointClass.GroupsBans] = new(
                VRChatEndpointClass.GroupsBans, GroupLane,
                HardMaxPerSecond: PerSeconds(2), DefaultCeilingPerSecond: CeilingFor(PerSeconds(2)),
                ResourceScoped: true),

            [VRChatEndpointClass.GroupsAuditLog] = new(
                VRChatEndpointClass.GroupsAuditLog, GroupLane,
                HardMaxPerSecond: PerSeconds(8), DefaultCeilingPerSecond: CeilingFor(PerSeconds(8)),
                ResourceScoped: true),

            // One per ten seconds, measured by the maintainer on 2026-09-13 -- slower than the
            // 1-per-8s this originally guessed. Every open instance is in one response, so the
            // poll rate is what decides how quickly Modbot notices an instance opening or
            // closing, and ten seconds is close enough for an instance that lives for hours.
            [VRChatEndpointClass.GroupsInstances] = new(
                VRChatEndpointClass.GroupsInstances, GroupLane,
                HardMaxPerSecond: PerSeconds(10), DefaultCeilingPerSecond: CeilingFor(PerSeconds(10)),
                ResourceScoped: true),

            // Group info and group roles are both spec 4.2's 1-per-10s, and they share this
            // class, so the class ceiling is the sum of the two. Pacing each type apart from the
            // other is the scheduler's job, not the bucket's.
            [VRChatEndpointClass.GroupsRead] = new(
                VRChatEndpointClass.GroupsRead, GroupLane,
                HardMaxPerSecond: 0.2, DefaultCeilingPerSecond: CeilingFor(0.2),
                ResourceScoped: true),

            // One invite every thirty seconds, across the whole deployment -- the maintainer's
            // answer to spec 4.3.4's standing question for POST /groups/{groupId}/invites, given
            // on 2026-09-19 when auto-invites were asked for. It replaces the conservative
            // neighbour's 1-per-3.5s this carried while nothing used the class.
            //
            // Backstop `global`, not `interactive`: an invite is timer-driven work nobody is
            // waiting on, and the room spec 4.2 reserves is for the moderator who is (auto-invites
            // design §5.1). Deliberately not in `Scheduled` -- adding a bucket that spends one
            // token every thirty seconds to that sum would shrink the moderators' room to pay for
            // it, and the global bucket's own ceiling still bounds everything drawing from it.
            [VRChatEndpointClass.GroupsInvites] = new(
                VRChatEndpointClass.GroupsInvites, GroupInvitesLane,
                HardMaxPerSecond: PerSeconds(30), DefaultCeilingPerSecond: CeilingFor(PerSeconds(30)),
                ResourceScoped: true),

            // Interactive and low-volume. Paced under the interactive backstop -- it is what spec
            // 4.2's reserved room is for -- and its own stop is separate, so a cold members bucket
            // never blocks a ban.
            [VRChatEndpointClass.ModerationWrite] = new(
                VRChatEndpointClass.ModerationWrite, GroupLane,
                HardMaxPerSecond: 0.3, DefaultCeilingPerSecond: CeilingFor(0.3),
                Backstop: VRChatEndpointClass.Interactive,
                ResourceScoped: true),

            // NOT MEASURED -- the group kick, ban and unban a moderator presses (M4 §4). Nobody has
            // asked VRChat what these allow, and spec 4.3.4 forbids borrowing a neighbour's number,
            // so the maintainer set a deliberately low starting rate: one request per two seconds,
            // shared by all three. Its own lane, so the moderator waiting on it never queues behind
            // a member sweep; scoped to the group; counted against the interactive backstop, not
            // the global one the sweeps keep empty (spec 4.3.5). A 429 cold stops this class and
            // nothing else, and is never retried -- the action failed.
            [VRChatEndpointClass.GroupsModerate] = new(
                VRChatEndpointClass.GroupsModerate, GroupsModerateLane,
                HardMaxPerSecond: PerSeconds(2), DefaultCeilingPerSecond: CeilingFor(PerSeconds(2)),
                Backstop: VRChatEndpointClass.Interactive,
                ResourceScoped: true),

            // NOT MEASURED -- the people waiting to be let into the group (join requests design
            // §3). Nobody has asked VRChat what GET /groups/{groupId}/requests allows, so the
            // rate is groups.read's 0.2 req/s: these return group data, and spec 4.3.4.1 already
            // settled that the conservative reading for an unmeasured group endpoint is a
            // group-shaped one. Its own lane, so opening the screen never queues behind a sweep;
            // scoped to the group; counted against the interactive backstop, because a moderator
            // opened the page and nothing polls this on its own.
            [VRChatEndpointClass.GroupsRequests] = new(
                VRChatEndpointClass.GroupsRequests, GroupsRequestsLane,
                HardMaxPerSecond: 0.2, DefaultCeilingPerSecond: CeilingFor(0.2),
                Backstop: VRChatEndpointClass.Interactive,
                ResourceScoped: true),

            // NOT MEASURED -- approving and rejecting one join request, PUT
            // /groups/{groupId}/requests/{userId}. One per two seconds, shared by both answers:
            // deliberately low, the same starting point the maintainer set for groups.moderate
            // rather than a number read off it. Its own class and lane on purpose -- working a
            // queue is many small writes in a row, and a 429 earned doing that must not take the
            // ban button down with it.
            [VRChatEndpointClass.GroupsRequestsAnswer] = new(
                VRChatEndpointClass.GroupsRequestsAnswer, GroupsRequestsAnswerLane,
                HardMaxPerSecond: PerSeconds(2), DefaultCeilingPerSecond: CeilingFor(PerSeconds(2)),
                Backstop: VRChatEndpointClass.Interactive,
                ResourceScoped: true),

            // A request forwarded as it was written (VRChat proxy design). The limiter cannot see
            // which VRChat endpoint it reaches, so the cap sits at the bottom of spec 4.3.4's
            // provisional range: a script that wants more than one request every three seconds
            // is sweeping, and sweeping is what the producers are for. Own lane; counted against
            // the global backstop, because it goes out as the service account; a 429 cold stops
            // the proxy and nothing Modbot does for itself.
            [VRChatEndpointClass.Proxy] = new(
                VRChatEndpointClass.Proxy, ProxyLane,
                HardMaxPerSecond: 0.3, DefaultCeilingPerSecond: CeilingFor(0.3)),

            // The same, with the caller's own VRChat cookie. A different account, so not counted
            // against the service account's backstop and not evidence about it (ServiceAccount:
            // false) -- but still paced, because it leaves from this host's address and Cloudflare
            // does not know whose cookie it was. One per two seconds; a guess, kept low.
            [VRChatEndpointClass.ProxyPassthrough] = new(
                VRChatEndpointClass.ProxyPassthrough, ProxyPassthroughLane,
                HardMaxPerSecond: PerSeconds(2), DefaultCeilingPerSecond: CeilingFor(PerSeconds(2)),
                Backstop: null, ServiceAccount: false),

            // Exempt from the global ceiling, deliberately and on evidence (spec 4.2.5). If 429s
            // start appearing on other classes shortly after user-sync bursts, that is the
            // signature of an account-wide limit and this exemption is what to withdraw.
            //
            // 3.5 req/s, not the 1 req/s the foundation spec wrote: the maintainer raised it on
            // 2026-09-13 (user profile sync design §5). Everything else about the lane stands --
            // a 429 cold-stops it, is never retried, and still halves the global bucket.
            [VRChatEndpointClass.UsersRead] = new(
                VRChatEndpointClass.UsersRead, UsersLane,
                HardMaxPerSecond: Sync.UserProfileSyncOptions.RequestsPerSecondCap,
                DefaultCeilingPerSecond: CeilingFor(Sync.UserProfileSyncOptions.RequestsPerSecondCap),
                Backstop: null),

            // The public profile -- GET /profile/{userId}, the main profile read.
            //
            // THE RATE CAME FROM THE MAINTAINER on 2026-09-15, answering spec 4.3.4's standing
            // question: "the same rate as users.read, but its own budget". So the numbers here
            // are deliberately the same as the users.read bucket above and are read from the same
            // constant rather than copied, and everything else about the two is separate: its own
            // bucket, so a 429 on one does not spend the other's allowance; its own lane, so the
            // two never queue behind each other and neither can starve the other.
            //
            // Exempt from the global ceiling for the same reason users.read is (spec 4.2.5), and
            // withdrawing that exemption means withdrawing both.
            [VRChatEndpointClass.UsersProfile] = new(
                VRChatEndpointClass.UsersProfile, UsersProfileLane,
                HardMaxPerSecond: Sync.UserProfileSyncOptions.RequestsPerSecondCap,
                DefaultCeilingPerSecond: CeilingFor(Sync.UserProfileSyncOptions.RequestsPerSecondCap),
                Backstop: null),

            // One person, read because somebody is waiting for the answer (spec 4.3.5). The same
            // endpoints as the two sync classes above, on a budget and a lane of their own, so a
            // cold stop the background sync earned never stops a person linking their account and
            // a person's lookups never spend the sync's allowance. Exempt from the backstops for
            // the reason the sync classes are: the exemption is about the endpoint, not who asked.
            // The rate is spec 4.2.5's original 1 req/s for the users lane, kept under the
            // maintainer's 3.5 because these reads share the endpoints already running at that.
            [VRChatEndpointClass.UsersLookup] = new(
                VRChatEndpointClass.UsersLookup, UsersLookupLane,
                HardMaxPerSecond: 1.0, DefaultCeilingPerSecond: CeilingFor(1.0),
                Backstop: null),

            // Unmeasured (spec 4.3.4), so: the most conservative plausible neighbour, its own
            // lane, and counted against the global ceiling. The burst of 2 is the whole point --
            // group selection is exactly two calls, and pacing them five seconds apart would put
            // a stall in the middle of a wizard step for no benefit, since the pair is issued
            // once and never repeated. Same reasoning as the Auth bucket below.
            [VRChatEndpointClass.UsersGroups] = new(
                VRChatEndpointClass.UsersGroups, UsersGroupsLane,
                HardMaxPerSecond: 0.2, DefaultCeilingPerSecond: CeilingFor(0.2),
                BurstTokens: 2),

            // Measured at 1 req/s by the maintainer on 2026-09-13, so these are findings rather
            // than spec 4.3.4 guesses -- but they still count against the global backstop, which
            // exists for the account-wide limit Modbot cannot see (spec 4.3.1). A world is read
            // once and then never again. An instance page is read about once every thirty seconds
            // per open group instance, for its head count (InstanceHeadCountSync), so its steady rate is
            // the number of open instances divided by thirty -- which moves with the evening rather
            // than being a fixed schedule, and is capped by this bucket either way. Both are
            // therefore absent from `Scheduled` below.
            [VRChatEndpointClass.WorldsRead] = new(
                VRChatEndpointClass.WorldsRead, PlacesLane,
                HardMaxPerSecond: 1.0, DefaultCeilingPerSecond: CeilingFor(1.0)),

            [VRChatEndpointClass.InstancesRead] = new(
                VRChatEndpointClass.InstancesRead, PlacesLane,
                HardMaxPerSecond: 1.0, DefaultCeilingPerSecond: CeilingFor(1.0)),

            // Measured by the maintainer on 2026-09-15: one instance created per five seconds. The
            // calendar opens at most one instance per event occurrence, so this is never close.
            [VRChatEndpointClass.InstancesCreate] = new(
                VRChatEndpointClass.InstancesCreate, InstancesCreateLane,
                HardMaxPerSecond: PerSeconds(5), DefaultCeilingPerSecond: CeilingFor(PerSeconds(5))),

            // NOT MEASURED -- these must be confirmed. VRChat's calendar is strict, and the
            // maintainer asked for "a very lax rate limit by default" until the real number is
            // known: one write a minute, shared by create, update and delete, and one read every ten
            // seconds. Scoped to the group, counted against the global backstop, and a 429 is a cold
            // stop like everywhere else -- never retried (calendar design §5).
            [VRChatEndpointClass.CalendarWrite] = new(
                VRChatEndpointClass.CalendarWrite, CalendarLane,
                HardMaxPerSecond: PerSeconds(60), DefaultCeilingPerSecond: CeilingFor(PerSeconds(60)),
                ResourceScoped: true),

            [VRChatEndpointClass.CalendarRead] = new(
                VRChatEndpointClass.CalendarRead, CalendarReadLane,
                HardMaxPerSecond: PerSeconds(10), DefaultCeilingPerSecond: CeilingFor(PerSeconds(10)),
                ResourceScoped: true),

            [VRChatEndpointClass.UsersSearch] = new(
                VRChatEndpointClass.UsersSearch, SearchLane,
                HardMaxPerSecond: PerSeconds(3.5), DefaultCeilingPerSecond: CeilingFor(PerSeconds(3.5)),
                Backstop: null),

            // A login is GetCurrentUser, Verify2FA, GetCurrentUser. Pacing that at one per two
            // seconds would make a sign-in look like a hang, so this is the one bucket with a
            // burst -- of exactly the size of the sequence it exists to admit. How many of those
            // may be sent in an hour is RateLimitOptions.SignInsPerHour (spec 4.1.2), not this.
            [VRChatEndpointClass.Auth] = new(
                VRChatEndpointClass.Auth, AuthLane,
                HardMaxPerSecond: 0.5, DefaultCeilingPerSecond: CeilingFor(0.5),
                Backstop: null, BurstTokens: 3),

            // Verify Auth Token, the session check (spec 4.1.2). NOT MEASURED -- the maintainer gave
            // no limit for it, and this needs confirming. One per ten seconds is a guess kept
            // deliberately low: it is asked on start-up and after a 401 and nowhere else, so it
            // never needs more. On the auth lane, and not counted against the sign-ins per hour,
            // because the maintainer confirmed it does not sign in again.
            [VRChatEndpointClass.AuthVerify] = new(
                VRChatEndpointClass.AuthVerify, AuthLane,
                HardMaxPerSecond: PerSeconds(10), DefaultCeilingPerSecond: CeilingFor(PerSeconds(10)),
                Backstop: null),
        };
}
