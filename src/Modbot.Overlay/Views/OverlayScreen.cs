using Modbot.Companion.Clips;
using Modbot.Companion.Overlay;

namespace Modbot.Overlay.Views;

/// <summary>A point on the panel as fractions: 0 at the left or top edge, 1 at the right or bottom.</summary>
public readonly record struct PanelCursor(float Across, float Down);

/// <summary>Which screen of the main panel is showing.</summary>
/// <remarks>
/// Three to move between by the tabs across the top and by tapping a roster row, and a fourth the
/// panel picks for itself. Reading is fine in VR; scrolling and typing are hostile, so each screen
/// is one short list or one short card rather than a page that has to be worked through (two
/// overlay modes design §3).
/// </remarks>
public enum OverlayPage
{
    /// <summary>Who is in the instance. Where the panel opens.</summary>
    Instance,

    /// <summary>What the live link has heard here, newest first.</summary>
    Events,

    /// <summary>One person, opened by tapping their row.</summary>
    Person,

    /// <summary>
    /// Four big lines for a panel worn on a wrist. Not a tab: the panel is on this screen exactly
    /// while it is worn on a hand, and on one of the other three the rest of the time.
    /// </summary>
    Wrist,
}

/// <summary>
/// Everything the overlay draws, in one immutable snapshot.
/// </summary>
/// <remarks>
/// <para><strong>A value, not a live view.</strong> The overlay renders from what the client
/// already holds — never from a request made while drawing — so the thing handed to the renderer
/// is a copy of the cache at a moment, with its age already worked out. That is what lets the
/// overlay show useful information when a server is unreachable instead of a spinner.</para>
/// <para><strong>It always says which group it is speaking for.</strong> A moderator staffing two
/// communities seeing a flag needs to know whose flag it is, and with several servers paired the
/// overlay follows the instance rather than picking one.</para>
/// </remarks>
/// <param name="GroupLabel">
/// The moderator's own label for the server whose instance this is. Null when they are not in a
/// group instance at all, which is most of anybody's VRChat use.
/// </param>
/// <param name="Freshness">How old the roster is, and whether to say so.</param>
/// <param name="Alert">A flagged user who just arrived, or null.</param>
/// <param name="Health">A Modbot fault worth interrupting for, or null.</param>
/// <param name="ShowIdleCard">Draw the idle card rather than nothing; only the debug page asks.</param>
/// <param name="Person">A person's card, opened by tapping their row, or null.</param>
/// <param name="RosterSkip">How many rows the roster has been scrolled past.</param>
/// <param name="Cursor">Where a controller points at the panel, or null when none does.</param>
/// <param name="Page">Which of the three screens the panel is on.</param>
/// <param name="Events">What the live link has heard for this instance, newest first.</param>
/// <param name="Clips">
/// What the Save a clip control says, and whether it can be pressed. Nothing is drawn for it while
/// Clips is switched off, which is what a fresh install has.
/// </param>
/// <param name="GroupIconUrl">
/// Where the group's icon is, as the server gave it at pairing, or null when it gave none. An
/// address, not a picture: the panel is a value, and what fetches the picture is the companion's
/// own cache — the same one the window's server cards draw from, so the overlay asks for nothing
/// the client was not already holding.
/// </param>
/// <param name="RosterFilters">What the Instance list is cut down to, and which of its filters is open.</param>
/// <param name="EventFilters">The same for the Audit Log, kept apart from the Instance list's.</param>
/// <param name="Arrivals">
/// When each person in the instance got here, from this PC's own copy of VRChat's log: a time, or
/// null for somebody already here when the moderator arrived. Only people on the roster.
/// </param>
/// <param name="Now">
/// The drive loop's clock when the screen was made. Join times and the time filters are measured
/// against it; only its minute decides whether the panel is drawn again.
/// </param>
/// <param name="NoKeyboard">
/// The panel this is drawn on has no way to type: a headset whose runtime offers no keyboard. The
/// Name filter is left off there unless a name is already set somewhere that can type.
/// </param>
/// <param name="HeadsUps">The heads-ups standing in this instance, oldest first (heads-ups, 2026-10-03).</param>
/// <param name="Draft">The heads-up being written on the panel, or null.</param>
/// <param name="CanPlaceHeadsUps">
/// Whether this panel can place and clear heads-ups. Without it there is no "+" on a row and no
/// Clear on a heads-up, so nothing is drawn that a tap would have to ignore.
/// </param>
/// <param name="Left">
/// The people who left in the last minute, as the drive loop counts it, first to go first. Their
/// rows stay on the Instance list, greyed, and are not counted in "here". Never in
/// <see cref="Roster"/>, which is who is present.
/// </param>
/// <param name="NotSynced">
/// The moderator is in a public, friends-only or private instance, and the lists are made from this
/// PC's own copy of VRChat's log rather than from a server. The panel says so, draws no rank, flags
/// or heads-up "+", and keeps the heads-up controls off. False in a group instance.
/// </param>
/// <param name="ModeratorArrived">
/// When the moderator got into this instance, from this PC's own copy of VRChat's log, or null when it
/// has no record of it (the overlay was switched on mid-instance). Somebody who was already here has
/// been here at least this long, which is what their row says (<see cref="ListFiltering.HereBeforeYouWords"/>).
/// </param>
/// <param name="ModeratorId">
/// The moderator's own VRChat id as this PC's log reading last said, or null while it is not known. The
/// desktop window's list puts their own row first, in a banner of its own. Never sent anywhere.
/// </param>
public sealed record OverlayScreen(
    string? GroupLabel,
    Cached<InstanceContext> Roster,
    Freshness Freshness,
    FlaggedJoinAlert? Alert = null,
    string? Health = null,
    bool ShowIdleCard = false,
    UserSummary? Person = null,
    int RosterSkip = 0,
    PanelCursor? Cursor = null,
    OverlayPage Page = OverlayPage.Instance,
    IReadOnlyList<LiveEvent>? Events = null,
    ClipButton Clips = default,
    string? GroupIconUrl = null,
    ListFilters? RosterFilters = null,
    ListFilters? EventFilters = null,
    IReadOnlyDictionary<string, DateTimeOffset?>? Arrivals = null,
    DateTimeOffset Now = default,
    bool NoKeyboard = false,
    IReadOnlyList<HeadsUp>? HeadsUps = null,
    HeadsUpDraft? Draft = null,
    bool CanPlaceHeadsUps = false,
    IReadOnlyList<RecentLeaver>? Left = null,
    bool NotSynced = false,
    DateTimeOffset? ModeratorArrived = null,
    string? ModeratorId = null)
{
    private static readonly IReadOnlyDictionary<string, DateTimeOffset?> NoArrivals = new Dictionary<string, DateTimeOffset?>();

    /// <summary>What the live link has heard, never null.</summary>
    public IReadOnlyList<LiveEvent> EventsOrNone => Events ?? [];

    /// <summary>
    /// How many people are here: the one count every surface says, in "Users (15)", "15 here" and the
    /// SteamVR page's People. Only the people present; somebody who has just left is a row and is not
    /// counted (<see cref="Left"/>).
    /// </summary>
    public int HereCount => Roster.Value?.Members.Count ?? 0;

    /// <summary>The Instance list's filters, never null.</summary>
    public ListFilters RosterFiltersOrNone => RosterFilters ?? ListFilters.None;

    /// <summary>The Audit Log's filters, never null.</summary>
    public ListFilters EventFiltersOrNone => EventFilters ?? ListFilters.None;

    /// <summary>The heads-ups standing here, never null.</summary>
    public IReadOnlyList<HeadsUp> HeadsUpsOrNone => HeadsUps ?? [];

    /// <summary>Who left in the last minute, never null.</summary>
    public IReadOnlyList<RecentLeaver> LeftOrNone => Left ?? [];

    /// <summary>When each person got here, never null.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset?> ArrivalsOrNone => Arrivals ?? NoArrivals;

    /// <summary>
    /// Every picture address a row on this screen may ask for: the people present and the ones who just
    /// left. The companion keeps these pictures in memory while the screen shows them and lets go of
    /// them after.
    /// </summary>
    public IReadOnlyCollection<string> PictureAddresses()
    {
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in Roster.Value?.Members ?? [])
        {
            if (!string.IsNullOrWhiteSpace(member.PictureUrl))
                found.Add(member.PictureUrl);
        }

        foreach (var leaver in LeftOrNone)
        {
            if (!string.IsNullOrWhiteSpace(leaver.Member.PictureUrl))
                found.Add(leaver.Member.PictureUrl);
        }

        return found;
    }

    /// <summary>The filters of the list showing now, or null on a screen with no list.</summary>
    public ListFilters? ShownFilters => Page switch
    {
        OverlayPage.Instance => RosterFiltersOrNone,
        OverlayPage.Events => EventFiltersOrNone,
        _ => null,
    };

    /// <summary>
    /// Nothing to say: no group, no roster, no alert, no problem, and not a list made from the log.
    /// Drawn as nothing at all unless <see cref="ShowIdleCard"/> asks for the card, which only the
    /// companion's debug page does.
    /// </summary>
    public bool IsIdle => GroupLabel is null && Roster.Value is null && Alert is null && Health is null && !NotSynced;

    /// <summary>The overlay when the moderator is not in any managed group's instance.</summary>
    public static OverlayScreen Idle { get; } = new(
        null,
        new Cached<InstanceContext>(null, Freshness.Never, TimeSpan.Zero),
        Freshness.Never);

    /// <summary>The same screen with the cursor at a point, or with none; the idle screen stays idle.</summary>
    public OverlayScreen WithCursor(PanelCursor? cursor) => Cursor == cursor ? this : this with { Cursor = cursor };

    /// <summary>
    /// Whether two screens would draw the same pixels. The compositor uses this to avoid
    /// rasterising a frame nobody would see any difference in.
    /// </summary>
    public bool LooksTheSameAs(OverlayScreen other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return GroupLabel == other.GroupLabel
            && GroupIconUrl == other.GroupIconUrl
            && Clips == other.Clips
            && ShowIdleCard == other.ShowIdleCard
            && NotSynced == other.NotSynced
            && ModeratorArrived == other.ModeratorArrived
            && ModeratorId == other.ModeratorId
            && RosterSkip == other.RosterSkip
            && Cursor == other.Cursor
            && Page == other.Page
            && NoKeyboard == other.NoKeyboard
            && CanPlaceHeadsUps == other.CanPlaceHeadsUps
            && Draft == other.Draft
            && HeadsUpsOrNone.SequenceEqual(other.HeadsUpsOrNone)
            && RosterFiltersOrNone == other.RosterFiltersOrNone
            && EventFiltersOrNone == other.EventFiltersOrNone
            && SameMinute(other)
            && SameArrivals(ArrivalsOrNone, other.ArrivalsOrNone)
            && SameLeft(other)
            && SameEvents(EventsOrNone, other.EventsOrNone)
            && Person?.SubjectId == other.Person?.SubjectId
            && Person?.DisplayName == other.Person?.DisplayName
            && Person?.Roles.Count == other.Person?.Roles.Count
            && Freshness == other.Freshness
            && Health == other.Health
            && Alert?.AlertId == other.Alert?.AlertId
            && Roster.Describe() == other.Roster.Describe()
            && SameRoster(Roster.Value, other.Roster.Value);
    }

    /// <summary>
    /// Whether the clock has moved on far enough to change what is drawn. Only the two lists, and the
    /// wrist's newest event (which can be a person who was here before the moderator), show anything
    /// measured against it, and nothing they show is finer than a minute.
    /// </summary>
    private bool SameMinute(OverlayScreen other)
    {
        if (Page is not (OverlayPage.Instance or OverlayPage.Events or OverlayPage.Wrist))
            return true;

        return Now.UtcTicks / TimeSpan.TicksPerMinute == other.Now.UtcTicks / TimeSpan.TicksPerMinute;
    }

    /// <summary>
    /// Whether the rows of people who just left, and the seconds each has left, would draw the same.
    /// Only the Instance list draws them, so no other screen is redrawn for a countdown.
    /// </summary>
    private bool SameLeft(OverlayScreen other)
    {
        if (Page is not OverlayPage.Instance)
            return true;

        var (mine, theirs) = (LeftOrNone, other.LeftOrNone);
        if (mine.Count != theirs.Count)
            return false;

        for (var i = 0; i < mine.Count; i++)
        {
            if (mine[i].Member.SubjectId != theirs[i].Member.SubjectId
                || mine[i].Member.DisplayName != theirs[i].Member.DisplayName
                || mine[i].SecondsLeft(Now) != theirs[i].SecondsLeft(other.Now))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameArrivals(IReadOnlyDictionary<string, DateTimeOffset?> a, IReadOnlyDictionary<string, DateTimeOffset?> b)
    {
        if (ReferenceEquals(a, b))
            return true;

        if (a.Count != b.Count)
            return false;

        foreach (var (subject, at) in a)
        {
            if (!b.TryGetValue(subject, out var other) || at != other)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Events only ever arrive at the front, so the count and the newest one's id are enough to
    /// tell two lists apart without walking them on every tick.
    /// </summary>
    private static bool SameEvents(IReadOnlyList<LiveEvent> a, IReadOnlyList<LiveEvent> b)
        => a.Count == b.Count && (a.Count == 0 || string.Equals(a[0].Id, b[0].Id, StringComparison.Ordinal));

    private static bool SameRoster(InstanceContext? a, InstanceContext? b)
    {
        if (a is null || b is null)
            return ReferenceEquals(a, b);

        if (a.InstanceId != b.InstanceId || a.Members.Count != b.Members.Count)
            return false;

        for (var i = 0; i < a.Members.Count; i++)
        {
            var (left, right) = (a.Members[i], b.Members[i]);
            if (left.SubjectId != right.SubjectId
                || left.Standing != right.Standing
                || left.DisplayName != right.DisplayName
                || left.PriorActions != right.PriorActions
                || left.TrustRank != right.TrustRank
                || left.EighteenPlus != right.EighteenPlus
                || left.PictureUrl != right.PictureUrl
                || !left.Flags.SequenceEqual(right.Flags, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
