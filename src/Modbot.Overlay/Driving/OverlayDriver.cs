using Modbot.Companion.Clips;
using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Overlay;
using Modbot.Companion.Sounds;
using Modbot.Companion.Time;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Modbot.Shared.HeadsUps;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Driving;

/// <summary>What one turn of the drive loop did.</summary>
/// <param name="Drew">
/// Whether a frame was actually rasterised and uploaded. False is the healthy common case: the
/// overlay is already showing the right thing.
/// </param>
/// <param name="ContextRefreshed">Whether a roster read was attempted this tick.</param>
/// <param name="AlertShown">Whether a new alert card was raised this tick.</param>
public readonly record struct OverlayTick(bool Drew, bool ContextRefreshed, bool AlertShown);

/// <summary>
/// Told the two things the loop learns that somebody other than the panel wants to hear: the
/// companion's voice, today.
/// </summary>
/// <remarks>
/// Told after the loop has decided, so a listener hears exactly what the panel shows — an alert
/// the loop dropped as not this instance, or as a repeat, is not passed on either.
/// </remarks>
public interface IOverlayListener
{
    /// <summary>A flagged-join alert became a card.</summary>
    void AlertShown(FlaggedJoinAlert alert);

    /// <summary>A server rejected this device's token; reads from it have stopped.</summary>
    void TokenRejected(string label);
}

/// <summary>
/// The loop that makes the overlay run: poll one server's roster, wait on its alerts, and push a
/// screen only when the screen would look different.
/// </summary>
/// <remarks>
/// <para><strong>Draw only on change, and that is a measurement rather than a preference.</strong>
/// An OpenVR overlay texture is submitted once and re-projected by the compositor at the headset's
/// own rate with the application uninvolved, so the cost that matters is per <em>change</em> — a
/// roster row, an alert, a freshness label ticking over — not per frame. A full 1024×1024 frame
/// costs about 1.3 ms end to end, which is affordable a few times a minute and wasteful ninety
/// times a second. This loop shares a machine with a game that wants every core and every spare
/// gigabyte, so a tick that would change nothing does nothing.</para>
/// <para><strong>It renders from the local cache and never from a live request.</strong> The reads
/// here fill the cache; the screen is built from whatever the cache holds, including when that is
/// old or when the server cannot be reached at all. The overlay's highest-value moment is a
/// flagged user arriving, which is also the moment the network is most likely to be hurting, so an
/// overlay that blanks in that window is backwards. Staleness is shown rather than hidden.</para>
/// <para><strong>The overlay follows the instance.</strong> With several servers paired, exactly
/// one of them manages the instance the moderator is standing in, and that is the only one this
/// loop reads from or shows. A moderator not in any group instance sees the idle screen, and no
/// server is contacted at all.</para>
/// <para><strong>Nothing here can be commanded.</strong> Every request is client-initiated, and
/// what comes back is data to display. There is no endpoint that tells this loop to do anything.</para>
/// </remarks>
public sealed class OverlayDriver : IDisposable
{
    /// <summary>
    /// How often the roster is re-read. Instance populations turn over in minutes, and this is
    /// also what the freshness label is measured against.
    /// </summary>
    public static readonly TimeSpan ContextRefreshInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long an alert card stays up before clearing itself.
    /// </summary>
    /// <remarks>
    /// Alerts must be dismissible and rate-limited: an overlay that interrupts constantly gets
    /// disabled, and a disabled overlay notifies nobody. A card that never leaves is the same
    /// failure arriving slowly.
    /// </remarks>
    public static readonly TimeSpan AlertDwell = TimeSpan.FromSeconds(25);

    /// <summary>
    /// How long before the same person can raise another card.
    /// </summary>
    /// <remarks>
    /// Somebody rejoining repeatedly — which is exactly what a person being kicked does — would
    /// otherwise produce a card every time, turning the one channel that must not become noise
    /// into noise.
    /// </remarks>
    public static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many live events the Events screen keeps. More than fits the panel, so scrolling has
    /// somewhere to go, and far less than a session's worth: this is what just happened, not a
    /// record. The record is the server's audit log.
    /// </summary>
    public const int MostEventsKept = 50;

    private readonly IOverlayReadClient _reads;
    private readonly ILiveSocketFactory? _sockets;
    private readonly IModbotClock _clock;
    private readonly IOverlayListener? _listener;
    private readonly PopUps? _popUps;
    private readonly IHeadsUpClient? _headsUpClient;
    private readonly List<Server> _servers = [];
    private readonly Dictionary<string, DateTimeOffset> _lastAlerted = new(StringComparer.Ordinal);

    // What the live link has heard for the instance the moderator is in, newest first.
    private readonly List<LiveEvent> _events = [];

    private InstanceLocation? _instance;
    private FlaggedJoinAlert? _showing;
    private DateTimeOffset? _showingSince;

    // What a tap on the panel opened: a person's card, which screen is showing, and how far the
    // roster is scrolled.
    private UserSummary? _person;
    private string? _personWanted;
    private int _rosterSkip;
    private OverlayPage _page = OverlayPage.Instance;

    // What each list is cut down to. Kept apart, so neither list looks empty for a reason set on
    // the other, and gone with the instance.
    private ListFilters _rosterFilters = ListFilters.None;
    private ListFilters _eventFilters = ListFilters.None;

    // The heads-up being written, if one is, and the ones a card has already been raised for in
    // this instance, so each is told once.
    private HeadsUpDraft? _draft;
    private readonly HashSet<string> _headsUpsTold = new(StringComparer.Ordinal);

    /// <summary>Reads VRChat's timestamps, which carry no zone, as instants on this PC's clock.</summary>
    private readonly LogTimestampConverter _timestamps = new();

    /// <summary>Which problem the notification overlay was last told about, so it is said once.</summary>
    private string? _problemShown;

    /// <summary>
    /// What the Save a clip control on the panel says, and whether it can be pressed. Set by the
    /// companion, which is the half that owns the recorder; the overlay never works it out itself
    /// and has no recorder to ask.
    /// </summary>
    /// <remarks>
    /// Hidden until the companion says otherwise, so a build with nothing wired to this — and a
    /// moderator who has never switched Clips on — gets no control at all.
    /// </remarks>
    public ClipButton Clips { get; set; } = ClipButton.None;

    /// <summary>
    /// Save a clip was pressed on either panel.
    /// </summary>
    /// <remarks>
    /// The loop does not save anything. It says that a moderator asked, and the companion — which
    /// owns the recorder, the folder and the limit on it — decides what happens. Nothing here
    /// reaches a server, and no server can raise this.
    /// </remarks>
    public event Action? SaveClipAsked;

    /// <param name="listener">Told when an alert becomes a card and when a server rejects the token. Optional.</param>
    /// <param name="sockets">
    /// Opens the live WebSocket. Null means the link only ever long-polls, which is what the tests
    /// use and what a build without a socket would do.
    /// </param>
    /// <param name="popUps">
    /// The notification overlay's stack, when there is one. The loop puts a pop-up up for a
    /// flagged arrival, a heads-up and a Modbot fault; everything else stays on the main panel.
    /// </param>
    /// <param name="headsUps">
    /// Places and clears heads-ups. Null leaves the panel able to show them and not to place or
    /// clear one, which is what the tests that are not about them get.
    /// </param>
    public OverlayDriver(
        IOverlayPresenter presenter,
        IOverlayReadClient reads,
        IModbotClock clock,
        IOverlayListener? listener = null,
        ILiveSocketFactory? sockets = null,
        PopUps? popUps = null,
        IHeadsUpClient? headsUps = null)
    {
        ArgumentNullException.ThrowIfNull(presenter);

        Presenter = presenter;
        _reads = reads;
        _clock = clock;
        _listener = listener;
        _sockets = sockets;
        _popUps = popUps;
        _headsUpClient = headsUps;
    }

    /// <summary>
    /// Where the screen goes. Swapped when the main panel is switched on or off, because the loop
    /// keeps running for the notification overlay either way (<see cref="NoPanel"/>).
    /// </summary>
    public IOverlayPresenter Presenter { get; set; }

    /// <summary>Adds a paired server the overlay may speak for.</summary>
    /// <param name="label">
    /// What to call this community on the panel, so somebody seeing a flag knows whose flag it is:
    /// the group's name, falling back to the server's address
    /// (<see cref="ServerPairing.OverlayLabel"/>). It used to be handed the pairing's local id,
    /// which is in practice the server's hostname — a panel saying a Railway address where it
    /// should have said the group.
    /// </param>
    public void Add(ServerPairing pairing, string label)
    {
        ArgumentNullException.ThrowIfNull(pairing);

        _servers.Add(new Server
        {
            Pairing = pairing,
            Label = label,
            Cache = new OverlayCache(_clock),
            Link = new LiveLink(_sockets, _reads, _clock, pairing),
        });
    }

    public void Remove(string serverId)
    {
        if (_servers.FirstOrDefault(s => s.Pairing.ServerId == serverId) is not { } server)
            return;

        // Its cached roster goes with it. Group context is the operator's data, not the
        // moderator's, and it has no business outliving the pairing.
        server.Cache.Clear();
        server.Link.Dispose();
        _servers.Remove(server);
    }

    /// <summary>Each paired server's live link, in one word, for the Servers card.</summary>
    public IReadOnlyDictionary<string, string> LiveWords()
        => _servers.ToDictionary(s => s.Pairing.ServerId, s => s.Link.Word, StringComparer.Ordinal);

    /// <summary>
    /// Tells the loop which instance the moderator is in, as the log reader last understood it.
    /// </summary>
    public void EnteredInstance(InstanceLocation? instance)
    {
        if (Equals(_instance, instance))
            return;

        _instance = instance;

        // A card about the instance you just left is worse than no card. Leaving clears it, and
        // so does an open person card, the events list, the screen and the scroll position:
        // another instance, another list.
        _showing = null;
        _showingSince = null;
        _person = null;
        _personWanted = null;
        _rosterSkip = 0;
        _page = OverlayPage.Instance;
        _rosterFilters = ListFilters.None;
        _eventFilters = ListFilters.None;
        _events.Clear();
        _problemShown = null;
        _draft = null;
        _headsUpsTold.Clear();
        _popUps?.ClearAll();
    }

    /// <summary>The moderator waved the card away. It does not come back.</summary>
    public void Dismiss()
    {
        _showing = null;
        _showingSince = null;
    }

    /// <summary>The person card that is open, or null.</summary>
    public UserSummary? Person => _person;

    /// <summary>How many roster rows are scrolled past.</summary>
    public int RosterSkip => _rosterSkip;

    /// <summary>Which screen the main panel is on.</summary>
    public OverlayPage Page => _page;

    /// <summary>What the live link has heard here, newest first.</summary>
    public IReadOnlyList<LiveEvent> Events => _events;

    /// <summary>What the Instance list is cut down to.</summary>
    public ListFilters RosterFilters => _rosterFilters;

    /// <summary>What the Audit Log is cut down to.</summary>
    public ListFilters EventFilters => _eventFilters;

    /// <summary>The heads-up being written, or null.</summary>
    public HeadsUpDraft? Draft => _draft;

    /// <summary>
    /// The words of the heads-up being written, as typed on SteamVR's keyboard or the desktop
    /// window. Cut at the longest a heads-up may be; nothing is sent until Place.
    /// </summary>
    public void SetHeadsUpText(string? text)
    {
        if (_draft is not { Sending: false } draft)
            return;

        var typed = text ?? string.Empty;
        if (typed.Length > HeadsUpRules.MaxTextLength)
            typed = typed[..HeadsUpRules.MaxTextLength];

        _draft = draft with { Text = typed, Problem = null };
    }

    /// <summary>
    /// When each person in the moderator's instance got here, as this PC's copy of VRChat's log
    /// says: VRChat's own timestamp, or null for somebody who was already here when the moderator
    /// arrived. Set by the companion, which reads the log; the overlay has no log of its own.
    /// </summary>
    /// <remarks>
    /// Read, never sent. It puts a join time on each roster row and is what the Joined filter
    /// measures, and nothing else.
    /// </remarks>
    public IReadOnlyDictionary<string, DateTime?>? ArrivedAt { get; set; }

    /// <summary>
    /// The name a list is searched for, as typed on SteamVR's keyboard or the desktop window.
    /// Blank clears it. The list goes back to its top, because the rows above are different rows.
    /// </summary>
    public void SetName(OverlayPage list, string? name)
    {
        var clean = ListFilters.CleanName(name);
        Change(list, filters => filters with { Name = clean });
    }

    private void Change(OverlayPage list, Func<ListFilters, ListFilters> change)
    {
        if (list is OverlayPage.Events)
        {
            _eventFilters = change(_eventFilters);
        }
        else
        {
            var before = _rosterFilters;
            _rosterFilters = change(_rosterFilters);

            if (before with { Open = null } != _rosterFilters with { Open = null })
                _rosterSkip = 0;
        }
    }

    /// <summary>
    /// A tap on a list's filter row: open or close a filter, take a choice, or clear the lot.
    /// </summary>
    private void Filter(OverlayTarget target)
    {
        switch (target)
        {
            case OverlayTarget.Filter chip:
                Change(chip.List, filters => filters with { Open = filters.Open == chip.Part ? null : chip.Part });
                break;

            case OverlayTarget.ClearFilters clear:
                Change(clear.List, _ => ListFilters.None);
                break;

            case OverlayTarget.Pick pick:
                Change(pick.List, filters => Picked(filters, pick));
                break;
        }
    }

    /// <summary>
    /// A choice taken. One-choice filters close once it is taken, so the list is in view again;
    /// rank and kind stay open for the next tick.
    /// </summary>
    private static ListFilters Picked(ListFilters filters, OverlayTarget.Pick pick) => pick.Part switch
    {
        FilterPart.Who when Enum.IsDefined((Who)pick.Choice) => filters with { Who = (Who)pick.Choice, Open = null },
        FilterPart.Time when Enum.IsDefined((TimeWindow)pick.Choice) => filters with { Time = (TimeWindow)pick.Choice, Open = null },
        FilterPart.Sort when Enum.IsDefined((RosterOrder)pick.Choice) => filters with { Order = (RosterOrder)pick.Choice, Open = null },
        FilterPart.Rank => filters with { Ranks = filters.Ranks.Toggle(pick.Choice < 0 ? null : (TrustRank)pick.Choice) },
        FilterPart.Kind when pick.Choice >= 0 && pick.Choice < KindPick.Offered.Count
            => filters with { Kinds = filters.Kinds.Toggle(KindPick.Offered[pick.Choice]) },
        FilterPart.Name => filters with { Name = null, Open = null },
        _ => filters,
    };

    /// <summary>Shows one of the panel's screens, as a tab does.</summary>
    public void GoTo(OverlayPage page)
    {
        // The Person screen with nobody open is a blank card. Asking for it shows the roster.
        _page = page is OverlayPage.Person && _person is null ? OverlayPage.Instance : page;
    }

    /// <summary>
    /// A tap on the panel. The alert card dismisses, a tab shows that screen, a roster or event
    /// row opens that person, Back returns to the roster, Refresh reads that person again, and
    /// Save a clip asks the companion to keep the last few minutes.
    /// </summary>
    public void Tap(OverlayTarget? target)
    {
        switch (target)
        {
            case OverlayTarget.DismissAlert:
                Dismiss();
                break;

            // Only while something is actually being kept. A tap that arrived from a stale frame,
            // drawn a moment before VRChat closed, must not look like it saved anything.
            case OverlayTarget.SaveClip when Clips.CanPress:
                SaveClipAsked?.Invoke();
                break;
            case OverlayTarget.GoTo tab:
                GoTo(tab.Page);
                break;
            case OverlayTarget.Person person:
                _ = OpenPersonAsync(person.SubjectId);
                break;
            case OverlayTarget.RefreshPerson when _person is { } open:
                _ = OpenPersonAsync(open.SubjectId);
                break;
            case OverlayTarget.ClosePerson:
                _person = null;
                _personWanted = null;
                _page = OverlayPage.Instance;
                break;
            case OverlayTarget.Filter or OverlayTarget.Pick or OverlayTarget.ClearFilters:
                // A filter's choices and a heads-up being written share the space under the tabs.
                _draft = _draft is { Sending: true } ? _draft : null;
                Filter(target);
                break;
            case OverlayTarget.AddHeadsUp add:
                StartHeadsUp(add.SubjectId);
                break;
            case OverlayTarget.HeadsUpKindPick kind when _draft is { Sending: false } draft:
                _draft = draft with { Kind = kind.Kind, Problem = null };
                break;
            case OverlayTarget.HeadsUpAboutPick about when _draft is { Sending: false } draft:
                _draft = draft with { OnInstance = about.Instance, Problem = null };
                break;
            case OverlayTarget.HeadsUpPlacePick place when _draft is { Sending: false } draft:
                _draft = draft with { Place = HeadsUpRules.Place(place.Place), Problem = null };
                break;
            case OverlayTarget.PlaceHeadsUp:
                _ = PlaceHeadsUpAsync();
                break;
            case OverlayTarget.CancelHeadsUp when _draft is { Sending: false }:
                _draft = null;
                break;
            case OverlayTarget.ClearHeadsUp clear:
                _ = ClearHeadsUpAsync(clear.Id);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Opens the strip for writing a heads-up from one roster row, with Message picked. Any open
    /// filter's choices close, because the strip takes their place.
    /// </summary>
    private void StartHeadsUp(string subjectId)
    {
        if (Current() is not { } server || _instance is null || _draft is { Sending: true })
            return;

        var member = server.Cache.Context(_instance.InstanceId, _instance.WorldId).Value?.Members
            .FirstOrDefault(m => string.Equals(m.SubjectId, subjectId, StringComparison.Ordinal));

        _rosterFilters = _rosterFilters with { Open = null };
        _page = OverlayPage.Instance;
        _draft = new HeadsUpDraft(subjectId, member?.DisplayName);
    }

    /// <summary>
    /// Place: sends the heads-up being written to the server whose instance this is, and closes
    /// the strip once it has been taken. A refusal stays on the strip, in the server's words.
    /// </summary>
    /// <remarks>
    /// This is the one moment a heads-up leaves the PC. The roster is read again straight after,
    /// so the moderator sees it standing as everybody else will.
    /// </remarks>
    public async Task PlaceHeadsUpAsync()
    {
        if (_draft is not { Sending: false } draft || Current() is not { } server || _instance is not { } here)
            return;

        if (draft.Missing is { } missing)
        {
            _draft = draft with { Problem = missing };
            return;
        }

        if (_headsUpClient is null || server.TokenRejected)
        {
            _draft = draft with { Problem = "Heads-ups cannot be placed from here." };
            return;
        }

        _draft = draft with { Sending = true, Problem = null };

        HeadsUpSent sent;
        try
        {
            sent = await _headsUpClient.PlaceAsync(server.Pairing, here.InstanceId, here.WorldId, draft, CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            sent = new HeadsUpSent(HeadsUpSendOutcome.Unreachable);
        }

        // The moderator walked somewhere else while it was on its way: the strip is gone already.
        if (!ReferenceEquals(_instance, here) || _draft is not { Sending: true } waiting)
            return;

        switch (sent.Outcome)
        {
            case HeadsUpSendOutcome.Done:
                _draft = null;
                server.LastContextAttempt = null;
                break;
            case HeadsUpSendOutcome.Unauthorised:
                _draft = null;
                RejectToken(server);
                break;
            case HeadsUpSendOutcome.Refused:
                _draft = waiting with { Sending = false, Problem = sent.Message ?? "Not placed." };
                break;
            default:
                _draft = waiting with { Sending = false, Problem = $"Cannot reach {server.Label}. Not placed." };
                break;
        }
    }

    /// <summary>Clear: takes a standing heads-up down for everybody in the instance.</summary>
    public async Task ClearHeadsUpAsync(string id)
    {
        if (_headsUpClient is null || Current() is not { } server || server.TokenRejected)
            return;

        HeadsUpSent sent;
        try
        {
            sent = await _headsUpClient.ClearAsync(server.Pairing, id, CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return;
        }

        if (sent.Outcome is HeadsUpSendOutcome.Unauthorised)
        {
            RejectToken(server);
            return;
        }

        // Done or refused, the list on the panel is read again: a refusal here means it is gone.
        _popUps?.Clear("heads-up:" + id);
        server.LastContextAttempt = null;
    }

    /// <summary>Scrolls the roster by whole rows, inside what the filters leave of it.</summary>
    public void ScrollRoster(int rows)
    {
        var count = Current() is { } server && _instance is not null
            && server.Cache.Context(_instance.InstanceId, _instance.WorldId).Value is { } context
            ? ListFiltering.Roster(context.Members, _rosterFilters, Arrivals(context), _clock.UtcNow).Count
            : 0;

        _rosterSkip = Math.Clamp(_rosterSkip + rows, 0, Math.Max(0, count - 1));
    }

    /// <summary>
    /// When each person on the roster got here, as instants, for the people the log has
    /// mentioned. A person on the server's roster whose arrival this PC never saw is left out,
    /// and their row says nothing about when they came.
    /// </summary>
    private Dictionary<string, DateTimeOffset?> Arrivals(InstanceContext context)
    {
        var arrivals = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
        if (ArrivedAt is not { Count: > 0 } arrived)
            return arrivals;

        foreach (var member in context.Members)
        {
            if (arrived.TryGetValue(member.SubjectId, out var at))
                arrivals[member.SubjectId] = at is { } local ? _timestamps.ToInstant(local) : null;
        }

        return arrivals;
    }

    /// <summary>
    /// Opens a person's card: the roster's own row at once, then the server's profile read when it
    /// lands. A card for someone the moderator has since tapped away from is not shown.
    /// </summary>
    /// <remarks>
    /// One GET to the current server, for the one person tapped, with the device token; the
    /// answer is shown and kept only while the card is open. Nothing is asked about anyone who
    /// was not tapped.
    /// </remarks>
    public async Task OpenPersonAsync(string subjectId)
    {
        if (Current() is not { } server || _instance is null)
            return;

        _personWanted = subjectId;
        _page = OverlayPage.Person;

        var known = server.Cache.Context(_instance.InstanceId, _instance.WorldId).Value?.Members.FirstOrDefault(m => m.SubjectId == subjectId);
        _person = known is null
            ? new UserSummary(subjectId, null, RosterStanding.Ordinary, 0, null, [], [])
            : new UserSummary(known.SubjectId, known.DisplayName, known.Standing, known.PriorActions, null, known.Flags, []);

        ReadResult<UserSummary> read;
        try
        {
            read = await _reads.GetUserAsync(server.Pairing, subjectId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return;
        }

        if (read.Outcome == ReadOutcome.Fetched && read.Value is { } summary && _personWanted == subjectId)
            _person = summary;
    }

    /// <summary>The server whose group owns the instance the moderator is standing in.</summary>
    /// <remarks>
    /// Null when they are in a public, friends-only or private instance, which is most of anybody's
    /// VRChat use, and null when the group is one no paired server manages. In both cases the
    /// overlay shows the idle screen and nothing is contacted.
    /// </remarks>
    public ServerPairing? CurrentServer => Current()?.Pairing;

    /// <summary>
    /// A person's VRChat trust rank and 18+ mark, as the server that manages the moderator's
    /// instance last said them, or null when it has said neither.
    /// </summary>
    /// <remarks>
    /// Read from what is already held — the roster and the live events for this instance — and
    /// never asked of the server. A person who has only just walked in is usually not known yet;
    /// the server learns of them from this client's own report a couple of seconds later.
    /// </remarks>
    public PersonInfo? InfoOf(string subjectId)
    {
        if (Current() is not { } server || _instance is null)
            return null;

        var member = server.Cache.Context(_instance.InstanceId, _instance.WorldId).Value?.Members
            .FirstOrDefault(m => string.Equals(m.SubjectId, subjectId, StringComparison.Ordinal));
        if (member is not null && PersonInfo.Of(member.TrustRank, member.EighteenPlus) is { } listed)
            return listed;

        var heard = _events
            .Select(e => e.Person)
            .FirstOrDefault(p => p is not null
                && (p.TrustRank is not null || p.EighteenPlus is not null)
                && string.Equals(p.SubjectId, subjectId, StringComparison.Ordinal));

        return heard is null
            ? null
            : PersonInfo.Of(heard.TrustRank is { } word ? TrustRanks.Parse(word) : null, heard.EighteenPlus);
    }

    /// <summary>
    /// One turn. Safe to call on a timer; it does nothing at all when nothing is due.
    /// </summary>
    public async Task<OverlayTick> TickAsync(CancellationToken cancellationToken = default)
    {
        var server = Current();

        if (server is null)
        {
            // Not in any paired group's instance: no server is contacted, and a link that was
            // open for the instance just left is closed.
            foreach (var paired in _servers)
                paired.Link.Follow(null);

            ExpireAlert();

            // Still idle — the panel says nothing about a group it is not in. Save a clip travels
            // with it, because the recorder runs wherever VRChat does.
            return new OverlayTick(Presenter.Update(OverlayScreen.Idle with { Clips = Clips }), false, false);
        }

        foreach (var other in _servers.Where(s => s != server))
            other.Link.Follow(null);

        // No ConfigureAwait(false) on these two, on purpose. What follows them builds Avalonia
        // controls, and Avalonia allows that only on the thread that owns them. The tick is called
        // from the UI thread; these awaits are the two places it could come back on a thread-pool
        // thread instead, and did: "Call from invalid thread" every tick that reached a server.
        var raised = await PumpLiveAsync(server, cancellationToken);
        var refreshed = await RefreshContextAsync(server, cancellationToken);

        ExpireAlert();

        return new OverlayTick(Presenter.Update(Build(server)), refreshed, raised);
    }

    public void Show() => Presenter.Show();

    public void Hide() => Presenter.Hide();

    public void Dispose()
    {
        foreach (var server in _servers)
        {
            server.Cache.Clear();
            server.Link.Dispose();
        }

        _servers.Clear();
        _events.Clear();
        _popUps?.ClearAll();
    }

    private Server? Current()
        => _instance?.GroupId is { } groupId
            ? _servers.FirstOrDefault(s =>
                string.Equals(s.Pairing.ManagedGroupId, groupId, StringComparison.Ordinal))
            : null;

    /// <summary>
    /// Re-reads the roster when it is due, and records a failure as a failure rather than as an
    /// empty roster.
    /// </summary>
    /// <remarks>
    /// Nothing cached is discarded on a failure. What was last known is still the best thing to
    /// show, and it is now shown with its age.
    /// </remarks>
    private async Task<bool> RefreshContextAsync(Server server, CancellationToken cancellationToken)
    {
        if (server.LastContextAttempt is { } last && _clock.UtcNow - last < ContextRefreshInterval)
            return false;

        if (server.TokenRejected)
            return false;

        server.LastContextAttempt = _clock.UtcNow;

        // Kept under the address this was asked for. The moderator may have walked into another
        // instance while the read was on its way, and its answer belongs to the one it named.
        var worldId = _instance!.WorldId;

        var result = await _reads
            .GetContextAsync(server.Pairing, _instance!.InstanceId, _instance.WorldId, cancellationToken)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case ReadOutcome.Fetched when result.Value is not null:
                server.Cache.RecordContext(server.Label, result.Value, worldId);
                TellHeadsUps(result.Value, worldId);
                break;

            case ReadOutcome.Unauthorised:
                // Terminal for this pairing. Surfaced on the overlay, and not retried: a revoked
                // moderator's client must stop, and be seen to stop.
                RejectToken(server);
                break;

            default:
                server.Cache.RecordUnreachable();
                break;
        }

        return true;
    }

    /// <summary>
    /// Keeps the current server's live link following the instance the moderator is in, and
    /// harvests what it received: a roster that needs re-reading, and flagged joins that become
    /// cards.
    /// </summary>
    /// <remarks>
    /// <para>Nothing here is awaited across the network. The link holds at most one request in
    /// flight and a tick only harvests what finished, so the loop keeps redrawing however slow the
    /// server is.</para>
    /// <para>A join or a leave means the roster on screen is out of date, so the next roster read
    /// is due at once rather than at the next twenty-second mark. That is what turns the roster
    /// from a poll into something that follows the instance.</para>
    /// </remarks>
    private async Task<bool> PumpLiveAsync(Server server, CancellationToken cancellationToken)
    {
        if (server.TokenRejected)
        {
            server.Link.Follow(null);
            return false;
        }

        server.Link.Follow(_instance!.InstanceId, _instance.WorldId);
        await server.Link.PumpAsync(cancellationToken).ConfigureAwait(false);

        if (server.Link.IsStopped)
        {
            // Terminal for this pairing. Surfaced on the overlay, and not retried.
            RejectToken(server);
            return false;
        }

        // The server said the heads-ups here changed. The roster read carries them.
        if (server.Link.TakeHeadsUpsChanged())
            server.LastContextAttempt = null;

        var raised = false;

        foreach (var @event in server.Link.Drain())
        {
            if (LiveEventKinds.ChangesRoster(@event.Kind) && IsHere(@event.InstanceId, @event.WorldId))
            {
                server.LastContextAttempt = null;
            }

            // The Events screen. Only this instance's, newest first, and a fixed number of them:
            // this is what just happened, not a record. The record is the server's audit log.
            if (IsHere(@event.InstanceId, @event.WorldId))
            {
                _events.Insert(0, @event);
                if (_events.Count > MostEventsKept)
                    _events.RemoveRange(MostEventsKept, _events.Count - MostEventsKept);
            }

            // A flagged join is a card here even when this client reported the join itself. It
            // used to be skipped on the grounds that the reporting client "already knows", but
            // what it read out of its own log is that somebody joined, not that the group has
            // kicked or banned them: the moderator alone in an instance, whose client is always
            // the one reporting, was the one moderator never warned. The cooldown in Accept still
            // keeps it to one card per person.
            if (@event.ToAlert() is { } alert && Accept(alert))
                raised = true;

            if (@event.Kind is LiveEventKinds.PersonJoined or LiveEventKinds.FlaggedJoin && IsHere(@event.InstanceId, @event.WorldId))
                TellKeptAnEyeOn(server, @event);
        }

        return raised;
    }

    /// <summary>
    /// A card for each heads-up a roster read for this instance brought that has not had one yet,
    /// and that another moderator placed.
    /// </summary>
    /// <remarks>
    /// Walking into an instance where heads-ups already stand tells them too: an ask for help placed
    /// a minute before is still one. Ones placed from this PC raise nothing here, because the
    /// moderator who pressed Place already knows.
    /// </remarks>
    /// <param name="worldId">The world the read was asked for, which its answer belongs to.</param>
    private void TellHeadsUps(InstanceContext context, string? worldId)
    {
        if (_instance is null
            || !string.Equals(context.InstanceId, _instance.InstanceId, StringComparison.Ordinal)
            || !string.Equals(worldId, _instance.WorldId, StringComparison.Ordinal))
        {
            return;
        }

        foreach (var headsUp in context.HeadsUpsOrNone)
        {
            if (!_headsUpsTold.Add(headsUp.Id) || headsUp.Mine || headsUp.KindOrNull is not { } kind)
                continue;

            var info = headsUp.SubjectId is { } subject ? InfoOf(subject) : null;
            var about = headsUp.About;

            _popUps?.Show(new PopUp(
                "heads-up:" + headsUp.Id,
                HeadsUpRules.Name(kind) + " · " + headsUp.PlacedBy,
                about ?? headsUp.Text ?? HeadsUpRules.Name(kind),
                about is null ? null : headsUp.Text,
                PopUpTone.Plain,
                info?.Rank,
                info?.EighteenPlus == true));
        }
    }

    /// <summary>
    /// Somebody a Keep an eye stands on has just walked in again: a card, for everybody here,
    /// placer included, because coming back is what the heads-up was for.
    /// </summary>
    private void TellKeptAnEyeOn(Server server, LiveEvent @event)
    {
        if (@event.Person is not { } person || _instance is null)
            return;

        var headsUp = server.Cache.Context(_instance.InstanceId, _instance.WorldId).Value?.HeadsUpsOrNone
            .FirstOrDefault(h => h.KindOrNull is HeadsUpKind.KeepAnEye
                && string.Equals(h.SubjectId, person.SubjectId, StringComparison.Ordinal));

        if (headsUp is null)
            return;

        var info = InfoOf(person.SubjectId);

        _popUps?.Show(new PopUp(
            "heads-up-joined:" + person.SubjectId,
            "Keep an eye · joined",
            person.DisplayName ?? headsUp.SubjectName ?? person.SubjectId,
            headsUp.Text,
            PopUpTone.Plain,
            info?.Rank,
            info?.EighteenPlus == true));
    }

    /// <summary>
    /// Whether something the server named by this number (and this world) happened in the instance
    /// the moderator is standing in.
    /// </summary>
    /// <remarks>
    /// A number is only unique inside one world, so two instances in two worlds can share one, and
    /// a card about the other one is somebody else's. A server that did not say the world (an
    /// older one) leaves nothing to compare, and then the number alone decides, as it always did.
    /// </remarks>
    private bool IsHere(string? instanceId, string? worldId)
        => _instance is { } here
            && string.Equals(instanceId, here.InstanceId, StringComparison.Ordinal)
            && (worldId is null || string.Equals(worldId, here.WorldId, StringComparison.Ordinal));

    /// <summary>
    /// Decides whether an alert becomes a card.
    /// </summary>
    /// <remarks>
    /// <para><strong>The answer to "several servers, one headset".</strong> The headset has room
    /// for one card, and two paired servers can have something to say at once. The rule is that an
    /// alert is shown only when it names the instance the moderator is standing in — which means
    /// at most one server can ever qualify, because a person is in one instance at a time. That is
    /// the same boundary the roster already follows, and it is the right one on its own merits:
    /// a flagged user walking into an instance the moderator is not in is not something they can act
    /// on, and interrupting them with it would spend the only push channel there is on something
    /// they cannot use.</para>
    /// <para>Alerts that do not qualify are dropped rather than queued. An alert is about a
    /// moment; one delivered later interrupts a moderator with news from twenty minutes ago, and
    /// the instance it referred to has usually emptied.</para>
    /// </remarks>
    private bool Accept(FlaggedJoinAlert alert)
    {
        // Not this instance. The moderator cannot act on it, so it is not worth the one card.
        if (!IsHere(alert.InstanceId, alert.WorldId))
            return false;

        // Somebody rejoining repeatedly is exactly what being kicked looks like, and a card each
        // time would turn the channel into noise.
        if (_lastAlerted.TryGetValue(alert.SubjectId, out var previous)
            && _clock.UtcNow - previous < AlertCooldown)
        {
            return false;
        }

        _lastAlerted[alert.SubjectId] = _clock.UtcNow;
        _showing = alert;
        _showingSince = _clock.UtcNow;
        _listener?.AlertShown(alert);

        // The same news on the notification overlay, where a moderator who is not looking at the
        // main panel will actually see it. Same rule, one decision: an alert the loop dropped is
        // not put up here either.
        _popUps?.Show(new PopUp(
            "alert:" + alert.AlertId,
            Current()?.Label is { Length: > 0 } label ? "Flagged user joined · " + label : "Flagged user joined",
            alert.DisplayName ?? alert.SubjectId,
            alert.Reason,
            PopUpTone.Flagged),
            NotificationKind.FlaggedJoin);

        return true;
    }

    /// <summary>Sticky, and told once: the same rejection reaching both reads does not say it twice.</summary>
    private void RejectToken(Server server)
    {
        if (server.TokenRejected)
            return;

        server.TokenRejected = true;
        _listener?.TokenRejected(server.Label);
    }

    private void ExpireAlert()
    {
        if (_showingSince is { } since && _clock.UtcNow - since >= AlertDwell)
            Dismiss();
    }

    private OverlayScreen Build(Server server)
    {
        var roster = server.Cache.Context(_instance!.InstanceId, _instance.WorldId);
        var problem = Health(server, roster);

        // A fault is the other thing worth a pop-up: inside VRChat there is no email, no Discord
        // and no browser. Said once per problem, not once per tick.
        if (problem != _problemShown)
        {
            _problemShown = problem;
            if (problem is { Length: > 0 })
                _popUps?.Show(new PopUp("problem", "Modbot", problem, null, PopUpTone.Problem), NotificationKind.Problem);
            else
                _popUps?.Clear("problem");
        }

        return new OverlayScreen(
            server.Label,
            roster,
            roster.Freshness,
            _showing,
            problem,
            Person: _person,
            RosterSkip: _rosterSkip,
            Page: _page,

            // A copy, not the list itself. The screen is a snapshot, and one that kept changing
            // under the compositor would compare equal to itself and never redraw.
            Events: [.. _events],
            Clips: Clips,

            // The address the server gave at pairing. The panel draws the picture the companion
            // already holds for it; nothing here fetches anything.
            GroupIconUrl: server.Pairing.ManagedGroupIconUrl,
            RosterFilters: _rosterFilters,
            EventFilters: _eventFilters,
            Arrivals: roster.Value is { } context ? Arrivals(context) : null,
            Now: _clock.UtcNow,
            HeadsUps: roster.Value?.HeadsUpsOrNone,
            Draft: _draft,
            CanPlaceHeadsUps: _headsUpClient is not null && !server.TokenRejected);
    }

    /// <summary>
    /// The line worth interrupting for, or null.
    /// </summary>
    /// <remarks>
    /// Inside VRChat there is no email, no Discord and no browser, so for the person doing
    /// moderation at the moment it matters this is the entire notification surface. It is
    /// therefore kept for the two things that mean presence has actually stopped, rather than used
    /// for every transient hiccup — the roster's own age already says when data is merely old.
    /// </remarks>
    private static string? Health(Server server, Cached<InstanceContext> roster)
    {
        if (server.TokenRejected)
        {
            return $"{server.Label} rejected this device. Reporting and lookups have stopped; "
                + "if you were removed from that group's staff, this is expected.";
        }

        if (server.Cache.ServerUnreachable && roster.Freshness != Freshness.Fresh)
            return $"Cannot reach {server.Label}. Showing what was last known.";

        return null;
    }

    private sealed class Server
    {
        public required ServerPairing Pairing { get; init; }

        public required string Label { get; init; }

        public required OverlayCache Cache { get; init; }

        /// <summary>Live updates for this server: the socket first, long polling behind it.</summary>
        public required LiveLink Link { get; init; }

        /// <summary>Null when the roster is due now: never read, or a live event changed it.</summary>
        public DateTimeOffset? LastContextAttempt { get; set; }

        /// <summary>Sticky. A 401 is terminal for a pairing and is never retried into.</summary>
        public bool TokenRejected { get; set; }
    }
}
