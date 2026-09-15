using Modbot.Client.Instances;
using Modbot.Client.Journal;
using Modbot.Client.Routing;
using Modbot.Client.Time;
using Modbot.Core.Time;

namespace Modbot.Client.Ingest;

/// <summary>Where one server connection currently stands.</summary>
public enum ConnectionState
{
    /// <summary>Working normally.</summary>
    Healthy,

    /// <summary>Waiting out a failure or a <c>Retry-After</c>. Still buffering.</summary>
    Waiting,

    /// <summary>
    /// The moderator pressed pause. Nothing is captured for this server and nothing is sent.
    /// </summary>
    Paused,

    /// <summary>
    /// The token was rejected. Reporting has stopped and the moderator is told — a revoked
    /// moderator's client must stop, and be seen to stop, rather than retry quietly forever.
    /// </summary>
    Stopped,

    /// <summary>The server's API moved past this client's range; the pairing needs renegotiating.</summary>
    NeedsRenegotiation,
}

/// <summary>
/// Everything to do with one paired server: its token, its buffer, its clock offset, its pause
/// state, and its share of the observations.
/// </summary>
/// <remarks>
/// <para><strong>One of these per server, and nothing is shared between them</strong> beyond the
/// log being read once. That separation is the point: a moderator staffing two communities must be
/// able to pause one without pausing the other, and neither operator gains visibility into the
/// other's instances.</para>
/// <para><strong>What it sends.</strong> Batches of <see cref="ClientEvent"/>, to one URL, over
/// HTTPS, with one bearer token. Nothing else. The server never sends anything back that this
/// client acts on — there is no command channel, by design, so the client's behaviour stays fully
/// described by its own source.</para>
/// <para><strong>What pausing means.</strong> While paused, observations for this server are
/// discarded rather than queued, and nothing is transmitted. Pausing stops Modbot reporting what
/// you do; it does not save it up to report later. Anything buffered <em>before</em> the pause is
/// still owed to the server and is sent when reporting resumes.</para>
/// </remarks>
public sealed class ServerConnection : IIngestTarget
{
    /// <summary>Protocol 4.4: send at about fifty events or thirty seconds, whichever is first.</summary>
    public const int DefaultBatchSize = 50;

    public static readonly TimeSpan DefaultBatchInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long an arrival, a departure or a stopped log waits before it is sent. Protocol 4.4.
    /// </summary>
    /// <remarks>
    /// <para>The Live page shows who is in a room right now, and a thirty-second wait made "right
    /// now" half a minute old. Two seconds is quick enough to read as live and slow enough to
    /// group the changes that land together -- a friend group walking in, the tail of an arrival
    /// burst -- into one request.</para>
    /// <para>Measured from the <em>first</em> change still waiting, not the latest, so a steady
    /// stream of arrivals cannot keep pushing the send back.</para>
    /// <para>Avatar changes do not start this wait. They are not who is in the room, and a room
    /// full of people trying on avatars would otherwise send every two seconds.</para>
    /// </remarks>
    public static readonly TimeSpan DefaultChangeDelay = TimeSpan.FromSeconds(2);

    private readonly FileEventBuffer _buffer;
    private readonly PresenceEventMapper _mapper;
    private readonly IIngestTransport _transport;
    private readonly IModbotClock _clock;
    private readonly BackoffPolicy _backoff;
    private readonly string _clientVersion;
    private readonly TimeSpan _batchInterval;
    private readonly TimeSpan _changeDelay;
    private readonly SentJournal? _journal;

    /// <summary>When the oldest unsent arrival, departure or stop was put in the buffer, or null.</summary>
    private DateTimeOffset? _changeWaitingSince;

    private int _batchSize = DefaultBatchSize;
    private int _consecutiveFailures;
    private DateTimeOffset? _notBefore;
    private DateTimeOffset _lastSendAttempt;
    private string? _inFlightBatchId;
    private bool _paused;

    public ServerConnection(
        ServerPairing pairing,
        FileEventBuffer buffer,
        PresenceEventMapper mapper,
        ServerClock serverClock,
        IIngestTransport transport,
        IModbotClock clock,
        string clientVersion,
        BackoffPolicy? backoff = null,
        TimeSpan? batchInterval = null,
        SentJournal? journal = null,
        TimeSpan? changeDelay = null)
    {
        Pairing = pairing;
        _buffer = buffer;
        _mapper = mapper;
        ServerClock = serverClock;
        _transport = transport;
        _clock = clock;
        _clientVersion = clientVersion;
        _backoff = backoff ?? new BackoffPolicy();
        _batchInterval = batchInterval ?? DefaultBatchInterval;
        _changeDelay = changeDelay ?? DefaultChangeDelay;
        _journal = journal;
        _lastSendAttempt = clock.UtcNow;

        // A buffer carried over from the last run may hold changes nobody has been told about yet.
        // They are already late, so they go at the next chance rather than waiting out a timer.
        NoteWaitingChanges();
    }

    public ServerPairing Pairing { get; private set; }

    public ServerClock ServerClock { get; }

    /// <summary>
    /// What this server last said about log backup to Modbot Cloud, or null until it has answered a
    /// time probe. Read by <see cref="CloudBackup.CloudDestination"/>; nothing else acts on it.
    /// </summary>
    public ServerCloudAnswer? Cloud { get; set; }

    public string ServerId => Pairing.ServerId;

    public string ManagedGroupId => Pairing.ManagedGroupId;

    /// <summary>How many observations are waiting to be sent. Shown to the moderator.</summary>
    public int Pending => _buffer.Count;

    /// <summary>How many events were accepted over this connection's lifetime.</summary>
    public int AcceptedTotal { get; private set; }

    /// <summary>How many the server already had from somebody else. Expected, not a fault.</summary>
    public int DeduplicatedTotal { get; private set; }

    /// <summary>Batches the server called malformed. Non-zero means a bug worth an alarm.</summary>
    public int MalformedBatches { get; private set; }

    public ConnectionState State { get; private set; } = ConnectionState.Healthy;

    /// <summary>
    /// The moderator's pause switch. Setting it stops transmission immediately and stops this
    /// server being told anything new.
    /// </summary>
    public bool IsPaused
    {
        get => _paused;
        set
        {
            if (_paused == value)
                return;

            _paused = value;
            if (value)
            {
                State = ConnectionState.Paused;
                _journal?.RecordNote(ServerId, "Paused. Nothing is being captured or sent for this server.");
            }
            else
            {
                if (State == ConnectionState.Paused)
                    State = ConnectionState.Healthy;

                _journal?.RecordNote(ServerId, "Resumed reporting.");
            }
        }
    }

    /// <summary>Records a renegotiated API version after a 409.</summary>
    public void Renegotiated(int apiVersion)
    {
        Pairing = Pairing with { ApiVersion = apiVersion };
        State = ConnectionState.Healthy;
    }

    /// <summary>
    /// Takes one observation the router decided this server is entitled to, converts it to the wire
    /// shape, and puts it on disk.
    /// </summary>
    public void Accept(ObservedPresence observation)
    {
        // Paused means this server is told nothing about what happens from now on. Not queued for
        // later: not captured. The journal records the refusal, because "nothing was sent" and
        // "nothing happened" look identical to somebody reading a list of disclosures.
        if (_paused || State is ConnectionState.Stopped)
        {
            _journal?.RecordWithheld(
                ServerId,
                _paused
                    ? "Not sent — reporting is paused."
                    : "Not sent — this pairing has stopped; the server rejected its token.");
            return;
        }

        if (_mapper.Map(observation) is { } clientEvent)
        {
            _buffer.Add(clientEvent);

            if (IsChange(clientEvent.Type))
                _changeWaitingSince ??= _clock.UtcNow;
        }
    }

    /// <summary>
    /// Whether an event changes who is in a room -- and so is worth sending within seconds rather
    /// than at the next thirty-second batch.
    /// </summary>
    public static bool IsChange(ClientEventType type) => type is
        ClientEventType.InstanceJoined
        or ClientEventType.InstancePresenceObserved
        or ClientEventType.InstanceLeft
        or ClientEventType.LogStopped;

    /// <summary>
    /// Whether there is something to send and the client is allowed to send it yet.
    /// </summary>
    /// <remarks>
    /// <para>Three rules, whichever comes first: fifty events, thirty seconds, or two seconds after
    /// somebody arrived or left. Walking into a busy instance produces a burst of forty
    /// observations at once and goes straight away; people coming and going go within seconds; a
    /// quiet room sends nothing extra, because nothing is waiting.</para>
    /// <para>Backoff and <c>Retry-After</c> still win over all three. A server that asked for a
    /// pause gets one, however lively the room.</para>
    /// </remarks>
    public bool IsDueToSend()
    {
        if (_paused || State is ConnectionState.Stopped or ConnectionState.NeedsRenegotiation)
            return false;

        if (_buffer.Count == 0)
            return false;

        if (_notBefore is { } waitUntil && _clock.UtcNow < waitUntil)
            return false;

        return _buffer.Count >= _batchSize
            || _clock.UtcNow - _lastSendAttempt >= _batchInterval
            || (_changeWaitingSince is { } since && _clock.UtcNow - since >= _changeDelay);
    }

    /// <summary>
    /// Sends one batch if one is due. Returns the server's answer, or <c>null</c> when nothing was
    /// sent.
    /// </summary>
    public async Task<IngestResult?> PumpAsync(CancellationToken cancellationToken = default)
    {
        if (!IsDueToSend())
            return null;

        var events = _buffer.Peek(Math.Min(_batchSize, EventBatch.MaxEvents));
        if (events.Count == 0)
            return null;

        // The batch id is kept across retries of the same batch, so a server that answered a
        // request the client never received recognises the repeat.
        _inFlightBatchId ??= Guid.NewGuid().ToString("n");
        _lastSendAttempt = _clock.UtcNow;

        var batch = EventBatch.Create(_inFlightBatchId, _clientVersion, ServerClock, events);

        IngestResult result;
        try
        {
            result = await _transport.SendAsync(Pairing, batch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Any transport fault is a transient one from the buffer's point of view. Nothing is
            // removed, so nothing is lost; the events go out on the next attempt.
            result = new IngestResult(IngestOutcome.NetworkFailure);
        }

        Apply(result, events);
        return result;
    }

    private void Apply(IngestResult result, IReadOnlyList<ClientEvent> sent)
    {
        switch (result.Outcome)
        {
            case IngestOutcome.Accepted:
                // The server has made a final decision about every event in the batch -- taken,
                // already known, or refused -- so all of them come out of the buffer. Partial
                // acceptance is the normal case, not an error.
                AcceptedTotal += result.Accepted;
                DeduplicatedTotal += result.Deduplicated;

                // Written before the buffer is cleared, so the record of a disclosure cannot be
                // lost by a crash between the two.
                _journal?.RecordSent(ServerId, sent);
                _buffer.Remove(sent.Select(e => e.ClientEventId));
                Succeeded();
                NoteWaitingChanges();
                break;

            case IngestOutcome.Malformed:
                // A permanent error. Retrying a batch the server will always refuse is how a buffer
                // fills up forever and stops reporting anything at all, so these are dropped and
                // counted loudly instead.
                MalformedBatches++;
                _journal?.RecordNote(
                    ServerId,
                    $"The server refused a batch of {sent.Count} as malformed. They were dropped, not retried.");
                _buffer.Remove(sent.Select(e => e.ClientEventId));
                Succeeded();
                NoteWaitingChanges();
                break;

            case IngestOutcome.Unauthorised:
                // Terminal for this pairing. The buffer is kept -- the moderator may re-pair -- but
                // nothing more is sent and the state is surfaced rather than retried quietly.
                State = ConnectionState.Stopped;
                _journal?.RecordNote(
                    ServerId,
                    "The server rejected this device token. Reporting has stopped and will not be retried.");
                _inFlightBatchId = null;
                break;

            case IngestOutcome.VersionUnsupported:
                State = ConnectionState.NeedsRenegotiation;
                _inFlightBatchId = null;
                break;

            case IngestOutcome.TooLarge:
                // Halve and try again. Nothing is dropped: the events are still in the buffer.
                _batchSize = Math.Max(1, _batchSize / 2);
                _inFlightBatchId = null;
                State = ConnectionState.Waiting;
                break;

            case IngestOutcome.RateLimited:
                // Honour what the server asked for. Modbot sends Retry-After even though VRChat
                // does not, so there is no guessing to do here.
                _consecutiveFailures++;
                _notBefore = _clock.UtcNow + (result.RetryAfter ?? _backoff.Delay(_consecutiveFailures));
                State = ConnectionState.Waiting;
                break;

            default:
                _consecutiveFailures++;
                _notBefore = _clock.UtcNow + _backoff.Delay(_consecutiveFailures);
                State = ConnectionState.Waiting;
                break;
        }
    }

    /// <summary>
    /// Looks at what is still in the buffer after a send and starts the short wait again if any of
    /// it is an arrival or a departure -- the second half of a burst bigger than one batch.
    /// </summary>
    private void NoteWaitingChanges()
    {
        _changeWaitingSince = _buffer.Count > 0
            && _buffer.Peek(Math.Min(_buffer.Count, EventBatch.MaxEvents)).Any(e => IsChange(e.Type))
            ? _clock.UtcNow
            : null;
    }

    private void Succeeded()
    {
        _consecutiveFailures = 0;
        _notBefore = null;
        _inFlightBatchId = null;
        _batchSize = DefaultBatchSize;
        State = _paused ? ConnectionState.Paused : ConnectionState.Healthy;
    }
}
