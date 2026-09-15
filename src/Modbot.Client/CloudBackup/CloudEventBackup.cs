using System.IO.Compression;
using System.Text.Json;
using Modbot.Client.Ingest;
using Modbot.Client.Instances;
using Modbot.Client.Time;
using Modbot.Core.Time;

namespace Modbot.Client.CloudBackup;

/// <summary>What the backup is doing, in the words the settings screen shows.</summary>
public enum CloudBackupState
{
    /// <summary>The moderator turned it off.</summary>
    Off,

    /// <summary>A paired server's operator turned it off.</summary>
    TurnedOffByServer,

    /// <summary>Queuing, until a paired server says where to send.</summary>
    WaitingForServer,

    /// <summary>Working normally.</summary>
    Sending,

    /// <summary>The last attempt failed and the next waits out a backoff.</summary>
    Retrying,
}

/// <param name="Queued">Events waiting, in memory and on disk.</param>
/// <param name="Dropped">Events deleted unsent this run: the queue or the folder was full, or Cloud refused a batch for good.</param>
public sealed record CloudBackupStatus(CloudBackupState State, long Queued, long Dropped);

/// <summary>Everything the backup needs, in one place.</summary>
/// <param name="Directory">Its outbox folder, <c>%APPDATA%\Modbot\cloud</c>.</param>
/// <param name="TimeZone">This PC's time zone, for reading VRChat's offset-less timestamps.</param>
public sealed record CloudBackupOptions(
    string Directory,
    IModbotClock Clock,
    ICloudLogClient Client,
    ICloudInstallStore Installs,
    string ClientVersion,
    bool Enabled = true,
    TimeZoneInfo? TimeZone = null,
    BackoffPolicy? Backoff = null,
    long OutboxCap = CloudOutbox.DefaultCap,
    IClientEventIdSource? Ids = null);

/// <summary>
/// Where the reader hands every observation it makes, whichever instance it is in.
/// </summary>
/// <remarks>
/// <see cref="Offer"/> must return at once. It runs inside the reader's turn, which also feeds
/// presence reporting to Modbot servers. Nothing is sent from here.
/// </remarks>
public interface IObservationSink
{
    void Offer(IReadOnlyList<ObservedPresence> observations);
}

/// <summary>
/// "Send all logging to Modbot Cloud as backup": the presence events this client reports, for every
/// instance, sent to Modbot Cloud (cloud event backup spec).
/// </summary>
/// <remarks>
/// <para><strong>What this sends.</strong> The client's parsed presence events — joins, "already
/// here", leaves, avatar changes and a stopped log — in exactly the shape a paired Modbot server gets
/// them (<see cref="ClientEvent"/>): an event id, the type, a time, the VRChat user id, their display
/// name, the avatar name for an avatar change, the world id, the instance id and the group id when
/// there is one. The difference from a server is the one the moderator is told about: this covers
/// <strong>every instance</strong> the moderator is in, public, friends-only and private ones included,
/// not only their group's. It never sends a raw log line, and never an instance's <c>nonce</c>, which
/// is thrown away when a location is read.</para>
/// <para><strong>Where.</strong> To one Modbot Cloud: <c>https://cloud.modbot.co</c>, or the one a
/// paired server names. If any paired server's operator turned it off, nothing is sent and nothing
/// is queued (<see cref="CloudDestination"/>). What each Modbot server is sent is unchanged.</para>
/// <para><strong>On by default, and off means off.</strong> Turning it off stops sending at once — a
/// batch in flight is cancelled — and deletes everything queued. Turning it back on sends from that
/// moment; nothing observed while it was off is ever sent.</para>
/// <para><strong>What is written to your disk.</strong> The outbox (<see cref="CloudOutbox"/>), capped
/// at 20 MB, and this client's install id and encrypted secret (<see cref="DpapiCloudInstallStore"/>).</para>
/// <para><strong>It never slows the log reader.</strong> <see cref="Offer"/> puts observations on an
/// in-memory queue and returns. Building the events, writing the outbox, compressing, registering
/// and sending all happen in <see cref="RunAsync"/>, on its own task.</para>
/// <para><strong>Backoff.</strong> No network, a <c>5xx</c> or a <c>429</c> waits out an exponential
/// backoff, or Cloud's <c>Retry-After</c>. This is Cloud's own limit and has nothing to do with
/// VRChat's cold stop.</para>
/// </remarks>
public sealed class CloudEventBackup : IObservationSink
{
    /// <summary>Observations held in memory between the reader and the outbox before the oldest are dropped.</summary>
    public const int QueueLimit = 10_000;

    /// <summary>How often the offset to Cloud's clock is re-measured, as for a Modbot server.</summary>
    public static readonly TimeSpan ClockCheckInterval = TimeSpan.FromHours(2);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CloudOutbox _outbox;
    private readonly ICloudLogClient _client;
    private readonly ICloudInstallStore _installs;
    private readonly IModbotClock _clock;
    private readonly BackoffPolicy _backoff;
    private readonly LogTimestampConverter _timestamps;
    private readonly IClientEventIdSource? _ids;
    private readonly string _clientVersion;

    private readonly Lock _queueGate = new();
    private readonly Lock _outboxGate = new();
    private readonly Queue<ObservedPresence> _queue = new();

    private volatile bool _enabled;
    private volatile CloudDestination _destination = CloudDestination.Default;
    private volatile CancellationTokenSource _sendStop = new();

    /// <summary>When the oldest observation now queued was offered. A batch's age counts from here.</summary>
    private DateTimeOffset? _queuedSince;

    /// <summary>Bumped by every clear, so observations taken before one are never written after it.</summary>
    private int _generation;

    private long _queueDropped;
    private long _rejectedEvents;
    private long _queuedOnDisk;
    private int _failures;
    private DateTimeOffset? _notBefore;

    private ServerClock _cloudClock;
    private PresenceEventMapper _mapper;
    private Uri? _clockFor;
    private DateTimeOffset? _lastClockCheck;

    public CloudEventBackup(CloudBackupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _outbox = new CloudOutbox(options.Directory, options.OutboxCap);
        _client = options.Client;
        _installs = options.Installs;
        _clock = options.Clock;
        _backoff = options.Backoff ?? new BackoffPolicy();
        _timestamps = new LogTimestampConverter(options.TimeZone);
        _ids = options.Ids;
        _clientVersion = options.ClientVersion;
        _enabled = options.Enabled;
        _queuedOnDisk = _outbox.QueuedEvents;
        _cloudClock = new ServerClock(_clock);
        _mapper = new PresenceEventMapper(_timestamps, _cloudClock, _ids);

        // Turned off while the client was closed -- settings edited by hand -- leaves nothing behind.
        if (!_enabled)
            Clear();
    }

    /// <summary>The moderator's switch.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;

            _enabled = value;
            if (!value)
                Clear();
        }
    }

    /// <summary>
    /// Where to send, worked out by the caller from the paired servers' answers each turn. A server
    /// turning the backup off clears the queue, exactly as the moderator turning it off does.
    /// </summary>
    public CloudDestination Destination
    {
        get => _destination;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            var previous = _destination;
            _destination = value;

            if (value.Kind is CloudDestinationKind.TurnedOffByServer && previous.Kind is not CloudDestinationKind.TurnedOffByServer)
                Clear();

            if (value.Endpoint != previous.Endpoint && value.Endpoint is not null)
            {
                _notBefore = null;
                _failures = 0;
            }
        }
    }

    public bool WantsEvents => _enabled && _destination.Kind is not CloudDestinationKind.TurnedOffByServer;

    public CloudBackupStatus Status
    {
        get
        {
            long inMemory;
            lock (_queueGate)
                inMemory = _queue.Count;

            var state = !_enabled ? CloudBackupState.Off
                : _destination.Kind switch
                {
                    CloudDestinationKind.TurnedOffByServer => CloudBackupState.TurnedOffByServer,
                    CloudDestinationKind.Wait => CloudBackupState.WaitingForServer,
                    _ => _failures > 0 ? CloudBackupState.Retrying : CloudBackupState.Sending,
                };

            return new CloudBackupStatus(
                state,
                inMemory + Interlocked.Read(ref _queuedOnDisk),
                Interlocked.Read(ref _queueDropped) + Interlocked.Read(ref _rejectedEvents) + _outbox.DroppedEvents);
        }
    }

    /// <summary>Takes the reader's observations. Returns at once: it only adds to an in-memory queue.</summary>
    public void Offer(IReadOnlyList<ObservedPresence> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        if (!WantsEvents || observations.Count == 0)
            return;

        var now = _clock.UtcNow;

        lock (_queueGate)
        {
            if (_queue.Count == 0)
                _queuedSince = now;

            foreach (var observation in observations)
                _queue.Enqueue(observation);

            while (_queue.Count > QueueLimit)
            {
                _queue.Dequeue();
                _queueDropped++;
            }
        }
    }

    /// <summary>Runs the backup until <paramref name="cancellationToken"/> fires.</summary>
    /// <param name="onError">Told about anything unexpected; the loop carries on.</param>
    public async Task RunAsync(CancellationToken cancellationToken, Action<Exception>? onError = null)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var more = false;
            try
            {
                more = await PumpAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }

            if (more)
                continue;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One turn: turn queued observations into events on disk, close a batch that is due, and send the
    /// oldest batch if sending is allowed. Returns true when a batch was accepted.
    /// </summary>
    public async Task<bool> PumpAsync(CancellationToken cancellationToken)
    {
        if (!WantsEvents)
            return false;

        var destination = _destination;
        // Only asks Cloud the time when there is something to send, so an idle client is silent.
        if (destination is { Kind: CloudDestinationKind.Send, Endpoint: { } clockEndpoint } && Status.Queued > 0)
        {
            using var measuring = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sendStop.Token);
            try
            {
                await MeasureClockAsync(clockEndpoint, measuring.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        MoveQueueToDisk();

        var now = _clock.UtcNow;
        lock (_outboxGate)
        {
            _outbox.CloseIfDue(now);
            Interlocked.Exchange(ref _queuedOnDisk, _outbox.QueuedEvents);
        }

        if (destination is not { Kind: CloudDestinationKind.Send, Endpoint: { } endpoint })
            return false;

        if (_notBefore is { } waitUntil && now < waitUntil)
            return false;

        OutboxBatch? batch;
        IReadOnlyList<string> events;
        int generation;
        lock (_outboxGate)
        {
            batch = _outbox.Oldest();
            if (batch is null)
                return false;

            events = _outbox.ReadEvents(batch);
            generation = Volatile.Read(ref _generation);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sendStop.Token);

        try
        {
            if (await InstallAsync(endpoint, linked.Token).ConfigureAwait(false) is not { } install)
                return false;

            byte[] body;
            try
            {
                body = Body(batch, events, destination);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                // An event on disk that is not the JSON this client wrote. Resending will not fix it.
                Finish(batch, generation, new IngestResult(IngestOutcome.Malformed), endpoint);
                return false;
            }

            var result = await _client.SendAsync(install, body, linked.Token).ConfigureAwait(false);
            Finish(batch, generation, result, endpoint);
            return result.Outcome is IngestOutcome.Accepted;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Turned off mid-request. The batch is gone with the rest of the outbox.
            return false;
        }
    }

    private void MoveQueueToDisk()
    {
        List<ObservedPresence> taken;
        int generation;
        DateTimeOffset since;

        lock (_queueGate)
        {
            if (_queue.Count == 0)
                return;

            taken = [.. _queue];
            _queue.Clear();
            since = _queuedSince ?? _clock.UtcNow;
            _queuedSince = null;
            generation = Volatile.Read(ref _generation);
        }

        // Every instance, group or not: that is what the backup is. Times are corrected to Cloud's
        // clock as far as it has been measured, exactly as a server's events are to that server's.
        var events = taken
            .Select(o => JsonSerializer.Serialize(_mapper.MapAnyInstance(o), Json))
            .ToList();

        lock (_outboxGate)
        {
            if (generation != Volatile.Read(ref _generation))
                return;

            _outbox.Append(events, since);
        }
    }

    private async Task MeasureClockAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        if (_clockFor != endpoint)
        {
            _cloudClock = new ServerClock(_clock);
            _mapper = new PresenceEventMapper(_timestamps, _cloudClock, _ids);
            _clockFor = endpoint;
            _lastClockCheck = null;
        }

        if (_lastClockCheck is { } last && _clock.UtcNow - last < ClockCheckInterval)
            return;

        _lastClockCheck = _clock.UtcNow;

        if (await _client.MeasureAsync(endpoint, cancellationToken).ConfigureAwait(false) is { } sample)
            _cloudClock.Add(sample);
    }

    /// <summary>This client's install with the Cloud, registering on first send.</summary>
    private async Task<CloudInstall?> InstallAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        if (_installs.Find(endpoint) is { } existing)
            return existing;

        var registration = await _client.RegisterAsync(endpoint, _clientVersion, cancellationToken).ConfigureAwait(false);
        if (registration is { Outcome: IngestOutcome.Accepted, Secret: { } secret })
        {
            var install = new CloudInstall(endpoint, registration.InstallId, secret);
            _installs.Save(install);
            return install;
        }

        Failed(registration.RetryAfter);
        return null;
    }

    private byte[] Body(OutboxBatch batch, IReadOnlyList<string> events, CloudDestination destination)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new Utf8JsonWriter(gzip))
        {
            writer.WriteStartObject();
            writer.WriteString("batchId", batch.Name);
            writer.WriteString("clientVersion", _clientVersion);

            // The PC's own clock, uncorrected: Cloud compares it with its own to measure this PC.
            writer.WriteString("sentAt", _clock.UtcNow);
            writer.WriteNumber("clockOffsetMs", (long)_cloudClock.Offset.TotalMilliseconds);
            writer.WriteString("clockConfidence", _cloudClock.Confidence.ToWire());

            if (destination.ModbotServerId is { } serverId)
                writer.WriteString("modbotServerId", serverId);
            else
                writer.WriteNull("modbotServerId");

            writer.WriteStartArray("events");
            foreach (var json in events)
                writer.WriteRawValue(json);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private void Finish(OutboxBatch batch, int generation, IngestResult result, Uri endpoint)
    {
        switch (result.Outcome)
        {
            case IngestOutcome.Accepted:
                Remove(batch, generation);
                _failures = 0;
                _notBefore = null;
                break;

            case IngestOutcome.Malformed:
            case IngestOutcome.TooLarge:
                // Permanent for this batch. Retrying it forever is how an outbox fills and stops
                // everything behind it.
                if (Remove(batch, generation))
                    Interlocked.Add(ref _rejectedEvents, batch.Events);
                _failures = 0;
                _notBefore = null;
                break;

            case IngestOutcome.Unauthorised:
                // Cloud no longer knows this install. Register again, after a pause.
                _installs.Forget(endpoint);
                Failed(null);
                break;

            case IngestOutcome.RateLimited:
                Failed(result.RetryAfter);
                break;

            default:
                Failed(null);
                break;
        }
    }

    private bool Remove(OutboxBatch batch, int generation)
    {
        lock (_outboxGate)
        {
            if (generation != Volatile.Read(ref _generation))
                return false;

            _outbox.Delete(batch);
            Interlocked.Exchange(ref _queuedOnDisk, _outbox.QueuedEvents);
            return true;
        }
    }

    private void Failed(TimeSpan? retryAfter)
    {
        _failures++;
        _notBefore = _clock.UtcNow + (retryAfter ?? _backoff.Delay(_failures));
    }

    /// <summary>Forgets everything queued and cancels a batch in flight.</summary>
    private void Clear()
    {
        lock (_queueGate)
        {
            _queue.Clear();
            _queuedSince = null;
            Interlocked.Increment(ref _generation);
        }

        var stop = _sendStop;
        _sendStop = new CancellationTokenSource();
        stop.Cancel();

        lock (_outboxGate)
        {
            _outbox.Clear();
            Interlocked.Exchange(ref _queuedOnDisk, 0);
        }

        _failures = 0;
        _notBefore = null;
    }
}
