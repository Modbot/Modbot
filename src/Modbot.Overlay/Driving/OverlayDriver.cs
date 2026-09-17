using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Overlay;
using Modbot.Core.Time;
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
/// the loop dropped as not this room, or as a repeat, is not passed on either.
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

    private readonly IOverlayPresenter _presenter;
    private readonly IOverlayReadClient _reads;
    private readonly ILiveSocketFactory? _sockets;
    private readonly IModbotClock _clock;
    private readonly IOverlayListener? _listener;
    private readonly List<Server> _servers = [];
    private readonly Dictionary<string, DateTimeOffset> _lastAlerted = new(StringComparer.Ordinal);

    private InstanceLocation? _instance;
    private FlaggedJoinAlert? _showing;
    private DateTimeOffset? _showingSince;

    // What a tap on the panel opened: a person's card, and how far the roster is scrolled.
    private UserSummary? _person;
    private string? _personWanted;
    private int _rosterSkip;

    /// <param name="listener">Told when an alert becomes a card and when a server rejects the token. Optional.</param>
    /// <param name="sockets">
    /// Opens the live WebSocket. Null means the link only ever long-polls, which is what the tests
    /// use and what a build without a socket would do.
    /// </param>
    public OverlayDriver(
        IOverlayPresenter presenter,
        IOverlayReadClient reads,
        IModbotClock clock,
        IOverlayListener? listener = null,
        ILiveSocketFactory? sockets = null)
    {
        ArgumentNullException.ThrowIfNull(presenter);

        _presenter = presenter;
        _reads = reads;
        _clock = clock;
        _listener = listener;
        _sockets = sockets;
    }

    /// <summary>Adds a paired server the overlay may speak for.</summary>
    /// <param name="label">
    /// The moderator's own name for it, shown on the card so somebody seeing a flag knows whose
    /// flag it is.
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

        // A card about the room you just left is worse than no card. Leaving clears it, and
        // so does an open person card and the scroll position: another room, another list.
        _showing = null;
        _showingSince = null;
        _person = null;
        _personWanted = null;
        _rosterSkip = 0;
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

    /// <summary>
    /// A tap on the panel. The alert card dismisses, a roster row opens that person, the open
    /// card closes, and a tap anywhere else closes an open card.
    /// </summary>
    public void Tap(OverlayTarget? target)
    {
        switch (target)
        {
            case OverlayTarget.DismissAlert:
                Dismiss();
                break;
            case OverlayTarget.Person person:
                _ = OpenPersonAsync(person.SubjectId);
                break;
            default:
                _person = null;
                _personWanted = null;
                break;
        }
    }

    /// <summary>Scrolls the roster by whole rows; the view keeps it inside the list.</summary>
    public void ScrollRoster(int rows)
    {
        var count = Current() is { } server && _instance is not null
            ? server.Cache.Context(_instance.InstanceId).Value?.Members.Count ?? 0
            : 0;

        _rosterSkip = Math.Clamp(_rosterSkip + rows, 0, Math.Max(0, count - 1));
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

        var known = server.Cache.Context(_instance.InstanceId).Value?.Members.FirstOrDefault(m => m.SubjectId == subjectId);
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
    /// One turn. Safe to call on a timer; it does nothing at all when nothing is due.
    /// </summary>
    public async Task<OverlayTick> TickAsync(CancellationToken cancellationToken = default)
    {
        var server = Current();

        if (server is null)
        {
            // Not in any paired group's instance: no server is contacted, and a link that was
            // open for the room just left is closed.
            foreach (var paired in _servers)
                paired.Link.Follow(null);

            ExpireAlert();
            return new OverlayTick(_presenter.Update(OverlayScreen.Idle), false, false);
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

        return new OverlayTick(_presenter.Update(Build(server)), refreshed, raised);
    }

    public void Show() => _presenter.Show();

    public void Hide() => _presenter.Hide();

    public void Dispose()
    {
        foreach (var server in _servers)
        {
            server.Cache.Clear();
            server.Link.Dispose();
        }

        _servers.Clear();
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

        var result = await _reads
            .GetContextAsync(server.Pairing, _instance!.InstanceId, cancellationToken)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case ReadOutcome.Fetched when result.Value is not null:
                server.Cache.RecordContext(server.Label, result.Value);
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
    /// from a poll into something that follows the room.</para>
    /// </remarks>
    private async Task<bool> PumpLiveAsync(Server server, CancellationToken cancellationToken)
    {
        if (server.TokenRejected)
        {
            server.Link.Follow(null);
            return false;
        }

        server.Link.Follow(_instance!.InstanceId);
        await server.Link.PumpAsync(cancellationToken).ConfigureAwait(false);

        if (server.Link.IsStopped)
        {
            // Terminal for this pairing. Surfaced on the overlay, and not retried.
            RejectToken(server);
            return false;
        }

        var raised = false;

        foreach (var @event in server.Link.Drain())
        {
            if (LiveEventKinds.ChangesRoster(@event.Kind)
                && string.Equals(@event.InstanceId, _instance.InstanceId, StringComparison.Ordinal))
            {
                server.LastContextAttempt = null;
            }

            // The reporting client already knows: it read the join out of its own log a moment
            // ago, and a card would be in front of the one moderator who does not need it.
            if (@event.ByThisDevice)
                continue;

            if (@event.ToAlert() is { } alert && Accept(alert))
                raised = true;
        }

        return raised;
    }

    /// <summary>
    /// Decides whether an alert becomes a card.
    /// </summary>
    /// <remarks>
    /// <para><strong>The answer to "several servers, one headset".</strong> The headset has room
    /// for one card, and two paired servers can have something to say at once. The rule is that an
    /// alert is shown only when it names the instance the moderator is standing in — which means
    /// at most one server can ever qualify, because a person is in one instance at a time. That is
    /// the same boundary the roster already follows, and it is the right one on its own merits:
    /// a flagged user walking into a room the moderator is not in is not something they can act
    /// on, and interrupting them with it would spend the only push channel there is on something
    /// they cannot use.</para>
    /// <para>Alerts that do not qualify are dropped rather than queued. An alert is about a
    /// moment; one delivered later interrupts a moderator with news from twenty minutes ago, and
    /// the instance it referred to has usually emptied.</para>
    /// </remarks>
    private bool Accept(FlaggedJoinAlert alert)
    {
        // Not this room. The moderator cannot act on it, so it is not worth the one card.
        if (!string.Equals(alert.InstanceId, _instance?.InstanceId, StringComparison.Ordinal))
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
        var roster = server.Cache.Context(_instance!.InstanceId);

        return new OverlayScreen(
            server.Label,
            roster,
            roster.Freshness,
            _showing,
            Health(server, roster),
            Person: _person,
            RosterSkip: _rosterSkip);
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
