using System.Collections.Concurrent;
using Modbot.Companion.Ingest;
using Modbot.Core.Time;

namespace Modbot.Companion.Overlay;

/// <summary>Where one server's live link stands. Shown as one word on the Servers card.</summary>
public enum LiveLinkState
{
    /// <summary>Not in one of this server's instances, so nothing is open.</summary>
    Off,

    /// <summary>Opening the WebSocket, or waiting to try it again.</summary>
    Connecting,

    /// <summary>The WebSocket is open.</summary>
    Live,

    /// <summary>The WebSocket could not be kept; the same events come by long polling for a while.</summary>
    Polling,

    /// <summary>The token was rejected. Terminal for this pairing.</summary>
    Stopped,
}

/// <summary>
/// One server's live updates: the WebSocket first, long polling when the socket cannot be kept,
/// and the socket again on a schedule (live updates design §6).
/// </summary>
/// <remarks>
/// <para><strong>What this sends and receives.</strong> Nothing itself: it drives an
/// <see cref="ILiveSocketFactory"/> and an <see cref="IOverlayReadClient"/>, whose own remarks say
/// what leaves the machine. What comes back is who joined and left the instance the moderator is
/// in, which fills the roster and raises the flagged-join card.</para>
/// <para><strong>The fallback rule.</strong> The socket is tried first. When it cannot connect,
/// or drops, <see cref="DropsBeforePolling"/> times within <see cref="DropWindow"/>, the link
/// polls for <see cref="PollingSpell"/> and then tries the socket again. A proxy or captive portal
/// that will not carry WebSockets therefore costs some latency, never the updates; and a proxy
/// that later starts carrying them is noticed within a few minutes.</para>
/// <para><strong>Resume, never restart.</strong> Every event and heartbeat carries a cursor. A
/// reconnect, by either route, asks for events after the last one seen, so a dropped connection
/// costs delay and not data. Without a cursor -- the first connection -- the stream starts from
/// now, because the roster read covers what came before.</para>
/// <para><strong>Driven by ticks, never awaited.</strong> Like the roster read, this is pumped
/// from the overlay's loop and holds at most one request in flight. A tick harvests what finished
/// and starts what is due; it never waits on the network, because the loop that calls it also
/// redraws the panel.</para>
/// </remarks>
public sealed class LiveLink : IDisposable
{
    /// <summary>Failed connects or drops within <see cref="DropWindow"/> before the link polls instead.</summary>
    public const int DropsBeforePolling = 3;

    public static readonly TimeSpan DropWindow = TimeSpan.FromMinutes(2);

    /// <summary>How long the link polls before trying the socket again.</summary>
    public static readonly TimeSpan PollingSpell = TimeSpan.FromMinutes(5);

    /// <summary>
    /// What each poll asks the server to wait. Under the HTTP client's own thirty-second timeout,
    /// with room for the answer to travel.
    /// </summary>
    public const int PollWaitSeconds = 20;

    private readonly ILiveSocketFactory? _sockets;
    private readonly IOverlayReadClient _reads;
    private readonly IModbotClock _clock;
    private readonly ServerPairing _pairing;
    private readonly BackoffPolicy _backoff;
    private readonly ConcurrentQueue<LiveEvent> _events = new();
    private readonly Queue<DateTimeOffset> _drops = new();

    private string? _instanceId;
    private string? _connectingFor;
    private Task<LiveConnect>? _connecting;
    private ILiveSocket? _socket;
    private Task? _receiving;
    private CancellationTokenSource? _receiveStop;
    private volatile bool _dropped;
    private Task<ReadResult<LivePollPage>>? _polling;
    private DateTimeOffset? _notBefore;
    private DateTimeOffset? _pollUntil;
    private int _failures;

    public LiveLink(
        ILiveSocketFactory? sockets,
        IOverlayReadClient reads,
        IModbotClock clock,
        ServerPairing pairing,
        BackoffPolicy? backoff = null)
    {
        ArgumentNullException.ThrowIfNull(reads);
        ArgumentNullException.ThrowIfNull(pairing);

        _sockets = sockets;
        _reads = reads;
        _clock = clock;
        _pairing = pairing;
        _backoff = backoff ?? new BackoffPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
    }

    public LiveLinkState State { get; private set; } = LiveLinkState.Off;

    /// <summary>The last cursor seen, sent back on every reconnect and poll.</summary>
    public string? Cursor { get; private set; }

    public bool IsStopped => State is LiveLinkState.Stopped;

    /// <summary>The state in one word, for the Servers card.</summary>
    public string Word => State switch
    {
        LiveLinkState.Off => "Off",
        LiveLinkState.Connecting => "Connecting",
        LiveLinkState.Live => "Live",
        LiveLinkState.Polling => "Polling",
        _ => "Stopped",
    };

    /// <summary>
    /// The instance to follow, or null to close everything. A change while the socket is open is
    /// sent as a subscribe rather than a reconnect.
    /// </summary>
    public void Follow(string? instanceId)
    {
        if (string.Equals(_instanceId, instanceId, StringComparison.Ordinal))
            return;

        _instanceId = instanceId;

        if (instanceId is null)
        {
            CloseSocket();
            _connecting = null;
            _polling = null;
            if (State is not LiveLinkState.Stopped)
                State = LiveLinkState.Off;
            return;
        }

        if (_socket is not null && State is LiveLinkState.Live)
            _ = SubscribeAsync(_socket, instanceId);
    }

    /// <summary>Everything received since the last drain, oldest first.</summary>
    public IReadOnlyList<LiveEvent> Drain()
    {
        if (_events.IsEmpty)
            return [];

        var drained = new List<LiveEvent>();
        while (_events.TryDequeue(out var @event))
            drained.Add(@event);

        return drained;
    }

    /// <summary>One turn: harvest what finished, start what is due. Never waits on the network.</summary>
    public async Task PumpAsync(CancellationToken cancellationToken = default)
    {
        if (State is LiveLinkState.Stopped || _instanceId is not { } instance)
            return;

        var now = _clock.UtcNow;

        // A socket that closed under us.
        if (_dropped)
        {
            _dropped = false;
            CloseSocket();
            Dropped(now);
        }

        // A connect that finished.
        if (_connecting is { IsCompleted: true } connected)
        {
            _connecting = null;
            var result = await connected.ConfigureAwait(false);

            switch (result.Outcome)
            {
                case LiveConnectOutcome.Connected when result.Socket is not null:
                    _socket = result.Socket;
                    _failures = 0;
                    _notBefore = null;
                    State = LiveLinkState.Live;
                    StartReceiving(result.Socket);

                    // The moderator may have changed instances while the connect was in flight.
                    if (!string.Equals(_connectingFor, instance, StringComparison.Ordinal))
                        _ = SubscribeAsync(result.Socket, instance);
                    break;

                case LiveConnectOutcome.Unauthorised:
                    Stop();
                    return;

                default:
                    Dropped(now);
                    break;
            }
        }

        // A poll that finished.
        if (_polling is { IsCompleted: true } polled)
        {
            _polling = null;
            var result = await polled.ConfigureAwait(false);

            switch (result.Outcome)
            {
                case ReadOutcome.Fetched when result.Value is not null:
                    foreach (var @event in result.Value.Events)
                        _events.Enqueue(@event);
                    Cursor = result.Value.Cursor;
                    _failures = 0;
                    _notBefore = null;
                    break;

                case ReadOutcome.NothingWaiting:
                    _failures = 0;
                    _notBefore = null;
                    break;

                case ReadOutcome.Unauthorised:
                    Stop();
                    return;

                default:
                    _failures++;
                    _notBefore = now + _backoff.Delay(_failures);
                    break;
            }
        }

        if (_socket is not null && State is LiveLinkState.Live)
            return;

        if (_notBefore is { } until && now < until)
            return;

        // Polling for a spell, or for good when there is no socket to try.
        if (_sockets is null || (_pollUntil is { } spell && now < spell))
        {
            State = LiveLinkState.Polling;
            _polling ??= _reads.PollLiveAsync(_pairing, instance, Cursor, PollWaitSeconds, cancellationToken);
            return;
        }

        _pollUntil = null;
        State = LiveLinkState.Connecting;

        if (_connecting is null)
        {
            _connectingFor = instance;
            _connecting = _sockets.ConnectAsync(_pairing, instance, Cursor, cancellationToken);
        }
    }

    public void Dispose()
    {
        CloseSocket();
        _connecting = null;
        _polling = null;
    }

    /// <summary>Counts a failed connect or a drop, and switches to polling when they pile up.</summary>
    private void Dropped(DateTimeOffset now)
    {
        _drops.Enqueue(now);
        while (_drops.Count > 0 && now - _drops.Peek() > DropWindow)
            _drops.Dequeue();

        if (_drops.Count >= DropsBeforePolling)
        {
            _drops.Clear();
            _pollUntil = now + PollingSpell;
            _failures = 0;
            _notBefore = null;
            State = LiveLinkState.Polling;
            return;
        }

        _failures++;
        _notBefore = now + _backoff.Delay(_failures);
        State = LiveLinkState.Connecting;
    }

    private void Stop()
    {
        CloseSocket();
        _connecting = null;
        _polling = null;
        State = LiveLinkState.Stopped;
    }

    private void StartReceiving(ILiveSocket socket)
    {
        var stop = new CancellationTokenSource();
        _receiveStop = stop;
        _receiving = ReceiveLoopAsync(socket, stop.Token);
    }

    /// <summary>
    /// Reads until the socket closes. Runs beside the ticks so a burst of arrivals is queued at
    /// once rather than one per tick; the ticks drain the queue.
    /// </summary>
    private async Task ReceiveLoopAsync(ILiveSocket socket, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var message = await socket.ReceiveAsync(stop).ConfigureAwait(false);
                if (message is null)
                    break;

                switch (message.Kind)
                {
                    case "event" when message.Event is { } @event:
                        _events.Enqueue(@event);
                        Cursor = @event.Cursor;
                        break;

                    case "hello" or "heartbeat" when message.Cursor is { } cursor:
                        Cursor = cursor;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Closed on purpose.
            return;
        }

        if (!stop.IsCancellationRequested)
            _dropped = true;
    }

    private static async Task SubscribeAsync(ILiveSocket socket, string instanceId)
    {
        try
        {
            await socket.SubscribeAsync(instanceId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The receive loop will notice the socket is gone and the link will reconnect.
        }
    }

    private void CloseSocket()
    {
        if (_socket is not { } socket)
            return;

        _socket = null;
        _receiveStop?.Cancel();
        _receiveStop?.Dispose();
        _receiveStop = null;
        _receiving = null;

        _ = CloseQuietlyAsync(socket);
    }

    private static async Task CloseQuietlyAsync(ILiveSocket socket)
    {
        try
        {
            await socket.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Already gone.
        }
        finally
        {
            socket.Dispose();
        }
    }
}
