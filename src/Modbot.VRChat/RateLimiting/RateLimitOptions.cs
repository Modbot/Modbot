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
/// <param name="CountsAgainstGlobal">
/// Whether the global backstop bucket also has to grant a token. False only for
/// <c>users.read</c> and its neighbours, which spec 4.2.5 exempts deliberately.
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
    bool CountsAgainstGlobal = true,
    bool ResourceScoped = false,
    int BurstTokens = 1);

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
    /// The classes spec 4.2's table schedules as background sync, in its order.
    /// </summary>
    /// <remarks>
    /// Exactly the rows spec 4.2 sums to 1.450 req/s, and no others. The settings screen shows
    /// that sum against the 2 req/s ceiling so an operator can see how much room they
    /// are leaving (spec 4.2.1) — a figure that would mean nothing if it also counted classes
    /// nothing schedules. <c>moderation.write</c> and <c>groups.invites</c> pass the same backstop
    /// but are driven by a moderator, so they are what the room left is <em>for</em>, not part of
    /// what consumes it; <c>users.read</c> is exempt from the ceiling entirely (spec 4.2.5).
    /// </remarks>
    public static IReadOnlyList<string> Scheduled { get; } =
    [
        VRChatEndpointClass.GroupsMembers,
        VRChatEndpointClass.GroupsBans,
        VRChatEndpointClass.GroupsAuditLog,
        VRChatEndpointClass.GroupsInstances,
        VRChatEndpointClass.GroupsRead,
    ];

    public static IReadOnlyDictionary<string, RateLimitClassOptions> Defaults { get; } =
        new Dictionary<string, RateLimitClassOptions>(StringComparer.Ordinal)
        {
            // The backstop. Not the model -- it exists because an account-wide limit may also
            // apply and Modbot cannot see it (spec 4.3.1).
            [VRChatEndpointClass.Global] = new(
                VRChatEndpointClass.Global, GroupLane,
                HardMaxPerSecond: 2.0, DefaultCeilingPerSecond: CeilingFor(2.0),
                CountsAgainstGlobal: false),

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

            // No prior data; spec 4.3.4 matches it to the conservative neighbour.
            [VRChatEndpointClass.GroupsInvites] = new(
                VRChatEndpointClass.GroupsInvites, GroupLane,
                HardMaxPerSecond: PerSeconds(3.5), DefaultCeilingPerSecond: CeilingFor(PerSeconds(3.5)),
                ResourceScoped: true),

            // Interactive and low-volume. Obeys the global ceiling -- it is what spec 4.2's
            // 0.55 req/s of reserved instance is for -- but its own stop is separate, so a cold
            // members bucket never blocks a ban.
            [VRChatEndpointClass.ModerationWrite] = new(
                VRChatEndpointClass.ModerationWrite, GroupLane,
                HardMaxPerSecond: 0.3, DefaultCeilingPerSecond: CeilingFor(0.3),
                ResourceScoped: true),

            // NOT MEASURED -- the group kick, ban and unban a moderator presses (M4 §4). Nobody has
            // asked VRChat what these allow, and spec 4.3.4 forbids borrowing a neighbour's number,
            // so the maintainer set a deliberately low starting rate: one request per two seconds,
            // shared by all three. Its own lane, so the moderator waiting on it never queues behind
            // a member sweep; scoped to the group; still counted against the global backstop. A 429
            // cold stops this class and nothing else, and is never retried -- the action failed.
            [VRChatEndpointClass.GroupsModerate] = new(
                VRChatEndpointClass.GroupsModerate, GroupsModerateLane,
                HardMaxPerSecond: PerSeconds(2), DefaultCeilingPerSecond: CeilingFor(PerSeconds(2)),
                ResourceScoped: true),

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
                CountsAgainstGlobal: false),

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
                CountsAgainstGlobal: false),

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
                CountsAgainstGlobal: false),

            // A login is GetCurrentUser, Verify2FA, GetCurrentUser. Pacing that at one per two
            // seconds would make a sign-in look like a hang, so this is the one bucket with a
            // burst -- of exactly the size of the sequence it exists to admit. How many of those
            // may be sent in an hour is RateLimitOptions.SignInsPerHour (spec 4.1.2), not this.
            [VRChatEndpointClass.Auth] = new(
                VRChatEndpointClass.Auth, AuthLane,
                HardMaxPerSecond: 0.5, DefaultCeilingPerSecond: CeilingFor(0.5),
                CountsAgainstGlobal: false, BurstTokens: 3),

            // Verify Auth Token, the session check (spec 4.1.2). NOT MEASURED -- the maintainer gave
            // no limit for it, and this needs confirming. One per ten seconds is a guess kept
            // deliberately low: it is asked on start-up and after a 401 and nowhere else, so it
            // never needs more. On the auth lane, and not counted against the sign-ins per hour,
            // because the maintainer confirmed it does not sign in again.
            [VRChatEndpointClass.AuthVerify] = new(
                VRChatEndpointClass.AuthVerify, AuthLane,
                HardMaxPerSecond: PerSeconds(10), DefaultCeilingPerSecond: CeilingFor(PerSeconds(10)),
                CountsAgainstGlobal: false),
        };
}
