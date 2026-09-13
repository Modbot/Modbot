using Modbot.Client.Ingest;
using Modbot.Client.Instances;
using Modbot.Client.Overlay;
using Modbot.Core.Time;
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
    private readonly IModbotClock _clock;
    private readonly List<Server> _servers = [];
    private readonly Dictionary<string, DateTimeOffset> _lastAlerted = new(StringComparer.Ordinal);

    private InstanceLocation? _instance;
    private FlaggedJoinAlert? _showing;
    private DateTimeOffset? _showingSince;

    public OverlayDriver(IOverlayPresenter presenter, IOverlayReadClient reads, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(presenter);

        _presenter = presenter;
        _reads = reads;
        _clock = clock;
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
            Alerts = new AlertChannel(_reads, _clock),
        });
    }

    public void Remove(string serverId)
    {
        if (_servers.FirstOrDefault(s => s.Pairing.ServerId == serverId) is not { } server)
            return;

        // Its cached roster goes with it. Group context is the operator's data, not the
        // moderator's, and it has no business outliving the pairing.
        server.Cache.Clear();
        _servers.Remove(server);
    }

    /// <summary>
    /// Tells the loop which instance the moderator is in, as the log reader last understood it.
    /// </summary>
    public void EnteredInstance(InstanceLocation? instance)
    {
        if (Equals(_instance, instance))
            return;

        _instance = instance;

        // A card about the room you just left is worse than no card. Leaving clears it.
        _showing = null;
        _showingSince = null;
    }

    /// <summary>The moderator waved the card away. It does not come back.</summary>
    public void Dismiss()
    {
        _showing = null;
        _showingSince = null;
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
            ExpireAlert();
            return new OverlayTick(_presenter.Update(OverlayScreen.Idle), false, false);
        }

        // No ConfigureAwait(false) on these two, on purpose. What follows them builds Avalonia
        // controls, and Avalonia allows that only on the thread that owns them. The tick is called
        // from the UI thread; these awaits are the two places it could come back on a thread-pool
        // thread instead, and did: "Call from invalid thread" every tick that reached a server.
        var refreshed = await RefreshContextAsync(server, cancellationToken);
        var raised = await PumpAlertsAsync(server, cancellationToken);

        ExpireAlert();

        return new OverlayTick(_presenter.Update(Build(server)), refreshed, raised);
    }

    public void Show() => _presenter.Show();

    public void Hide() => _presenter.Hide();

    public void Dispose()
    {
        foreach (var server in _servers)
            server.Cache.Clear();

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
                server.TokenRejected = true;
                break;

            default:
                server.Cache.RecordUnreachable();
                break;
        }

        return true;
    }

    /// <summary>
    /// Keeps one long poll in flight for the current server and harvests it when it finishes.
    /// </summary>
    /// <remarks>
    /// <para>The poll is never awaited inside a tick. It is held open for up to half a minute by
    /// design, and a loop that waited on it would stop refreshing rosters and stop redrawing for
    /// that whole time.</para>
    /// <para>A poll that a proxy cut short is not a failure and does not back anything off — the
    /// channel measures that for itself and shortens the next wait to fit under whatever cap is in
    /// the way.</para>
    /// </remarks>
    private async Task<bool> PumpAlertsAsync(Server server, CancellationToken cancellationToken)
    {
        var raised = false;

        if (server.InFlightAlert is { IsCompleted: true } finished)
        {
            server.InFlightAlert = null;

            // Completed, so this does not block; awaiting is how the result and any fault surface.
            var poll = await finished.ConfigureAwait(false);
            raised = Accept(poll, server);
        }

        if (server.InFlightAlert is null
            && !server.TokenRejected
            && !server.Alerts.IsStopped
            && !server.Alerts.IsBackingOff)
        {
            server.InFlightAlert = server.Alerts.PollOnceAsync(server.Pairing, cancellationToken);
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
    private bool Accept(AlertPoll poll, Server server)
    {
        if (poll.Outcome is AlertPollOutcome.Unauthorised)
        {
            server.TokenRejected = true;
            return false;
        }

        if (poll is not { Outcome: AlertPollOutcome.Alert, Alert: { } alert })
            return false;

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
        return true;
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
            Health(server, roster));
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

        public required AlertChannel Alerts { get; init; }

        public DateTimeOffset? LastContextAttempt { get; set; }

        public Task<AlertPoll>? InFlightAlert { get; set; }

        /// <summary>Sticky. A 401 is terminal for a pairing and is never retried into.</summary>
        public bool TokenRejected { get; set; }
    }
}
