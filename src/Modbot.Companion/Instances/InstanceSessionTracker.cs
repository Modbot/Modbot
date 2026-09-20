using Modbot.Companion.LogReading;

namespace Modbot.Companion.Instances;

/// <summary>
/// Turns a stream of recognised log lines into presence facts, discarding the ones VRChat only
/// appears to be reporting.
/// </summary>
/// <remarks>
/// <para><strong>The problem this exists for.</strong> VRChat emits <c>OnPlayerJoined</c> for
/// everyone <em>already present</em> when you arrive, and <c>OnPlayerLeft</c> for everyone
/// <em>still present</em> when you leave. None of those people joined or left. Recorded naively,
/// every moderator walking into a busy instance manufactures forty arrivals and, on the way out,
/// forty departures — inflating join counts by the instance population and destroying time-spent,
/// which is the metric regulars detection and giveaway weighting are built on.</para>
/// <para>It fails <em>silently</em>. Nothing errors; the numbers are simply wrong, and plausible.
/// That is why this class carries more tests than anything else in the client.</para>
/// <para><strong>The markers that make it solvable.</strong> Both bursts are cleanly delimited:</para>
/// <list type="bullet">
/// <item><description>Arrival — every join from <c>Joining &lt;location&gt;</c> up to
/// <strong>and including the local user's own</strong> is roster, not arrival.</description></item>
/// <item><description>Departure — every leave <strong>after <c>OnLeftRoom</c></strong> is
/// phantom.</description></item>
/// <item><description>Local identity — <c>Initialized PlayerAPI "&lt;name&gt;" is local</c> names
/// the moderator; matching it into the join burst yields their user id.</description></item>
/// </list>
/// <para><c>OnLeftRoom</c> (the local user left) is <strong>not</strong> <c>OnPlayerLeftRoom</c>
/// (a remote player left). One character apart, opposite meanings, and confusing them inverts the
/// whole rule — every genuine departure would be discarded and every phantom one kept.</para>
/// <para><strong>Nothing leaves the machine from here.</strong> This class holds a roster in memory
/// and emits descriptions of events. Which of them are ever transmitted, and to which server, is
/// decided afterwards by routing, which drops everything that is not a group instance.</para>
/// </remarks>
public sealed class InstanceSessionTracker
{
    private enum Phase
    {
        /// <summary>No instance known. The client may have started mid-session.</summary>
        Outside,

        /// <summary>Inside the join burst: joins are buffered, not emitted.</summary>
        Arriving,

        /// <summary>Settled. Joins and leaves mean what they say.</summary>
        Present,

        /// <summary>After <c>OnLeftRoom</c>: leaves are phantom.</summary>
        Departed,
    }

    private readonly List<PlayerJoinedEvent> _burst = [];
    private readonly Dictionary<string, string> _displayNameToUserId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _userIdToDisplayName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _userIdToAvatar = new(StringComparer.Ordinal);

    private Phase _phase = Phase.Outside;
    private InstanceLocation? _instance;
    private string? _worldName;

    /// <summary>
    /// The moderator's own VRChat id, learned once and remembered. Needed because their own join
    /// is what terminates the arrival burst.
    /// </summary>
    public string? LocalUserId { get; private set; }

    /// <summary>The moderator's own display name, refreshed whenever VRChat restates it.</summary>
    public string? LocalDisplayName { get; private set; }

    /// <summary>Who is in the instance right now, as far as the log has said.</summary>
    public IReadOnlyCollection<string> Roster => _userIdToDisplayName.Keys;

    /// <summary>The instance the moderator is in, or <c>null</c> when that is not yet known.</summary>
    public InstanceLocation? CurrentInstance => _phase is Phase.Departed or Phase.Outside ? null : _instance;

    /// <summary>
    /// The readable name of the world the moderator is in — "The Black Cat" rather than
    /// <c>wrld_4cf554b4-430c-…</c> — or <c>null</c> when VRChat has not said it.
    /// </summary>
    /// <remarks>
    /// <para>Used for one thing: naming a saved clip, so the right one can be picked out of a
    /// folder afterwards. It decides nothing. The world id is still the identity, and this is
    /// never matched against, routed on, or sent anywhere.</para>
    /// <para>It goes when the instance goes, for the same reason the instance does: a name held
    /// over from the last world would put the wrong world's name on a clip, which is worse than a
    /// clip with no world name on it at all.</para>
    /// </remarks>
    public string? WorldName => _phase is Phase.Departed or Phase.Outside ? null : _worldName;

    /// <summary>
    /// Drops everything learned about the session so far: which instance, who is in it, what they
    /// are wearing, and which of them is the moderator.
    /// </summary>
    /// <remarks>
    /// <para>Called when VRChat starts writing a <em>different</em> log file, which is the one
    /// thing that unambiguously means the previous session ended — VRChat opens a new log on every
    /// launch. Nothing carries over: the moderator is not standing where they were an hour ago, the
    /// people who were with them are not there, and even the local identity is re-learned, because
    /// a restart may be a different VRChat account on the same PC.</para>
    /// <para>Without this the client would spend the minute or two between VRChat launching and
    /// the first world load insisting the moderator was still in last night's instance — and the
    /// overlay would fetch and show that instance's roster, which is both wrong and somebody
    /// else's data.</para>
    /// </remarks>
    public void ForgetSession()
    {
        _burst.Clear();
        _userIdToDisplayName.Clear();
        _displayNameToUserId.Clear();
        _userIdToAvatar.Clear();

        _phase = Phase.Outside;
        _instance = null;
        _worldName = null;
        LocalUserId = null;
        LocalDisplayName = null;
    }

    /// <summary>
    /// Feeds one recognised log event in and gets back whatever facts it completes — usually none,
    /// occasionally a whole buffered roster at once.
    /// </summary>
    public IEnumerable<ObservedPresence> Observe(VRChatLogEvent logEvent) => logEvent switch
    {
        JoiningInstanceEvent joining => EnterInstance(joining),
        WorldNameEvent named => WorldNamed(named),
        PlayerJoinedEvent joined => PlayerJoined(joined),
        PlayerLeftEvent left => PlayerLeft(left),
        LocalPlayerLeftRoomEvent leftRoom => LocalLeftRoom(leftRoom),
        LocalPlayerIdentifiedEvent identified => LocalIdentified(identified),
        AvatarSwitchedEvent avatar => AvatarSwitched(avatar),

        // Destination set, OnPlayerLeftRoom and OnPlayerEnteredRoom carry no identity and change
        // no state. They are recognised so the health check can see the log is still understood,
        // and for nothing else.
        _ => [],
    };

    /// <summary>
    /// Everyone this session believes is in the instance, restated as "already here" at
    /// <paramref name="at"/> -- the moderator included.
    /// </summary>
    /// <remarks>
    /// <para>Used at the two moments a client finds itself holding a roster the far end has never
    /// been told about, and "already here" is exactly what is known about it: these people are in
    /// the log's roster now, and nothing is known about when they got there.</para>
    /// <list type="bullet">
    /// <item><description>VRChat's log starts growing again after this client reported that it had
    /// stopped. The server ended the moderator's watch at that report, so it has to be told the
    /// watch has started again.</description></item>
    /// <item><description>The client caught up with a log that was already being written when it
    /// started. The arrival burst that filled this roster was replayed rather than reported, so
    /// the server has never heard of any of these people.</description></item>
    /// </list>
    /// <para>Empty unless the moderator is settled in an instance. During an arrival burst the
    /// burst itself will report everybody, and outside an instance there is nobody to report.</para>
    /// </remarks>
    public IReadOnlyList<ObservedPresence> SeenAgain(DateTime at)
    {
        if (_phase is not Phase.Present || _instance is not { } instance)
            return [];

        return _userIdToDisplayName
            .Select(entry => new ObservedPresence(
                PresenceKind.PresenceObserved, at, entry.Key, NullIfBlank(entry.Value), instance))
            .ToList();
    }

    /// <summary>
    /// <c>Joining &lt;location&gt;</c>: the moderator is entering an instance, and the phantom join
    /// burst starts here.
    /// </summary>
    private IEnumerable<ObservedPresence> EnterInstance(JoiningInstanceEvent joining)
    {
        // Anything still buffered from the previous instance is flushed as roster rather than
        // dropped -- those people really were observed, just with no arrival time.
        var leftovers = CloseBurst();

        _userIdToDisplayName.Clear();
        _displayNameToUserId.Clear();
        _userIdToAvatar.Clear();

        // The world's readable name arrives on the line after this one. Whatever is held is last
        // world's, so it goes now rather than being allowed to name this world by accident.
        _worldName = null;

        if (InstanceLocation.TryParse(joining.Location, out var location))
        {
            _instance = location;
            _phase = Phase.Arriving;
        }
        else
        {
            // A location Modbot cannot read is not a reason to guess. Reporting stops until the
            // next transition, which is visible and recoverable; attributing events to the wrong
            // instance would not be.
            _instance = null;
            _phase = Phase.Outside;
        }

        return leftovers;
    }

    /// <summary>
    /// <c>Joining or Creating Room: &lt;world name&gt;</c>: the readable name of the world just
    /// entered. Completes no presence fact and changes nothing about who is where.
    /// </summary>
    private IEnumerable<ObservedPresence> WorldNamed(WorldNameEvent named)
    {
        _worldName = named.WorldName;
        return [];
    }

    private IEnumerable<ObservedPresence> PlayerJoined(PlayerJoinedEvent joined)
    {
        Remember(joined.UserId, joined.DisplayName);

        switch (_phase)
        {
            case Phase.Arriving:
                _burst.Add(joined);

                // The local user's own join terminates the burst -- but only once we know which id
                // is theirs. On the very first instance of a session we do not, and the burst stays
                // open until the "is local" line arrives a moment later.
                return LocalUserId is not null && joined.UserId == LocalUserId
                    ? CloseBurst()
                    : [];

            case Phase.Present:
                return Emit(PresenceKind.Joined, joined.Timestamp, joined.UserId, joined.DisplayName);

            // Outside: no instance to attribute this to. Departed: VRChat does not emit joins
            // after OnLeftRoom, and if it did there would be no instance for them either.
            default:
                return [];
        }
    }

    private IEnumerable<ObservedPresence> PlayerLeft(PlayerLeftEvent left)
    {
        var genuine = _phase is Phase.Present;

        Forget(left.UserId, left.DisplayName);

        // Phase.Departed: this is the phantom burst. Every one of these people is still standing in
        // the instance the moderator just walked out of. Their session simply stops being observed,
        // which is not the same as ending, and claiming otherwise would fabricate departures for
        // the entire population every time anybody leaves.
        return genuine
            ? Emit(PresenceKind.Left, left.Timestamp, left.UserId, left.DisplayName)
            : [];
    }

    /// <summary>
    /// <c>OnLeftRoom</c> — the <strong>local</strong> user left. Opens the phantom leave burst.
    /// </summary>
    private IEnumerable<ObservedPresence> LocalLeftRoom(LocalPlayerLeftRoomEvent leftRoom)
    {
        var pending = CloseBurst().ToList();
        var wasInside = _phase is Phase.Present;
        _phase = Phase.Departed;

        // The moderator's own departure is exact and happens here, at this line. The phantom burst
        // that follows will also contain their own OnPlayerLeft, and that copy is dropped with
        // everyone else's -- recording both would double-count the one departure that is real.
        if (wasInside && LocalUserId is not null && _userIdToDisplayName.ContainsKey(LocalUserId))
            pending.AddRange(Emit(PresenceKind.Left, leftRoom.Timestamp, LocalUserId, LocalDisplayName));

        Forget(LocalUserId, LocalDisplayName);
        return pending;
    }

    /// <summary>
    /// <c>Initialized PlayerAPI "&lt;name&gt;" is local</c> — the only line that says which of the
    /// people in the log is the moderator running Modbot.
    /// </summary>
    private IEnumerable<ObservedPresence> LocalIdentified(LocalPlayerIdentifiedEvent identified)
    {
        LocalDisplayName = identified.DisplayName;

        if (_displayNameToUserId.TryGetValue(identified.DisplayName, out var userId))
            LocalUserId = userId;

        // This line always follows the join burst, so it doubles as the burst's backstop: if the
        // local user's own join was never matched, close here rather than buffering forever.
        return CloseBurst();
    }

    private IEnumerable<ObservedPresence> AvatarSwitched(AvatarSwitchedEvent avatar)
    {
        // During the arrival burst VRChat logs what everybody present is *already* wearing, one
        // line per occupant. Nobody changed avatar, so these are dropped exactly as the phantom
        // joins beside them are -- otherwise every moderator walking in would manufacture an
        // avatar change for the whole instance.
        if (_phase is not Phase.Present)
            return [];

        // The line is ambiguous when either the display name or the avatar name contains
        // " to avatar ", and both are user-controlled. The roster settles it: the reading that
        // names somebody actually here is the right one. A reading that names nobody here yields
        // no fact at all, because without a user id there is nothing worth recording -- a display
        // name alone is mutable and collides.
        foreach (var (displayName, avatarName) in avatar.CandidateSplits())
        {
            if (!_displayNameToUserId.TryGetValue(displayName, out var userId))
                continue;

            // VRChat repeats the line for one avatar load -- three times in a row for the same
            // avatar, in the sample. A repeat is not a change, and sending it would be noise on
            // the wire and a fake avatar-change count at the far end.
            if (_userIdToAvatar.TryGetValue(userId, out var worn)
                && string.Equals(worn, avatarName, StringComparison.Ordinal))
            {
                return [];
            }

            _userIdToAvatar[userId] = avatarName;
            return Emit(PresenceKind.AvatarChanged, avatar.Timestamp, userId, displayName, avatarName);
        }

        return [];
    }

    /// <summary>
    /// Ends the arrival burst and turns what was buffered into facts: roster for everybody who was
    /// already there, and one exact arrival for the moderator themselves.
    /// </summary>
    private IEnumerable<ObservedPresence> CloseBurst()
    {
        if (_burst.Count == 0)
        {
            if (_phase is Phase.Arriving)
                _phase = Phase.Present;

            return [];
        }

        var buffered = _burst.ToArray();
        _burst.Clear();
        _phase = _phase is Phase.Arriving ? Phase.Present : _phase;

        var instance = _instance;
        if (instance is null)
            return [];

        return buffered.Select(join => new ObservedPresence(
            // Everyone except the moderator was already standing there, for an unknown length of
            // time -- possibly hours. "Present at this time" is all that is actually known, and
            // claiming an arrival time would invent precision. The moderator's own join, by
            // contrast, is the moment they really did arrive.
            join.UserId == LocalUserId ? PresenceKind.Joined : PresenceKind.PresenceObserved,
            join.Timestamp,
            join.UserId,
            NullIfBlank(join.DisplayName),
            instance));
    }

    private IEnumerable<ObservedPresence> Emit(
        PresenceKind kind,
        DateTime at,
        string subjectId,
        string? displayName,
        string? avatarName = null)
    {
        if (_instance is null)
            return [];

        return [new ObservedPresence(kind, at, subjectId, NullIfBlank(displayName), _instance, avatarName)];
    }

    private void Remember(string userId, string displayName)
    {
        _userIdToDisplayName[userId] = displayName;
        if (!string.IsNullOrEmpty(displayName))
            _displayNameToUserId[displayName] = userId;
    }

    private void Forget(string? userId, string? displayName)
    {
        if (userId is not null)
        {
            _userIdToDisplayName.Remove(userId);
            _userIdToAvatar.Remove(userId);
        }

        if (!string.IsNullOrEmpty(displayName)
            && _displayNameToUserId.TryGetValue(displayName, out var mapped)
            && mapped == userId)
        {
            _displayNameToUserId.Remove(displayName);
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
