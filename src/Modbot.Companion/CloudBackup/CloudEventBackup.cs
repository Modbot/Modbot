using System.IO.Compression;
using System.Text.Json;
using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Journal;
using Modbot.Companion.Time;
using Modbot.Core.Time;

namespace Modbot.Companion.CloudBackup;

/// <summary>What the backup is doing, for the client's own log.</summary>
public enum CloudBackupState
{
    /// <summary>Turned off in <c>settings.json</c> or with <c>MODBOT_CLOUD_DISABLED</c>.</summary>
    Off,

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
/// <param name="Endpoint">The Modbot Cloud to send to, from <see cref="CloudSettings"/>. Null is the default.</param>
/// <param name="Enabled">False when <see cref="CloudSettings.Disabled"/>.</param>
/// <param name="TimeZone">This PC's time zone, for reading VRChat's offset-less timestamps.</param>
/// <param name="Journal">
/// The Events screen's record, told what was queued for Cloud and what Cloud took. Null in tests
/// and anywhere the screen is not running.
/// </param>
public sealed record CloudBackupOptions(
    string Directory,
    IModbotClock Clock,
    ICloudLogClient Client,
    ICloudInstallStore Installs,
    string ClientVersion,
    Uri? Endpoint = null,
    bool Enabled = true,
    TimeZoneInfo? TimeZone = null,
    BackoffPolicy? Backoff = null,
    long OutboxCap = CloudOutbox.DefaultCap,
    IClientEventIdSource? Ids = null,
    SentJournal? Journal = null);

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
/// The event backup: the presence events this client reports, for every instance, sent to Modbot
/// Cloud (cloud event backup spec).
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
/// <para><strong>Where.</strong> To one Modbot Cloud: <c>https://cloud.modbot.co</c>, or the one named
/// in <c>settings.json</c> or <c>MODBOT_CLOUD_ENDPOINT</c> on this PC (<see cref="CloudSettings"/>).
/// Paired Modbot servers have no say in it, and pairing or unpairing changes nothing here. What each
/// Modbot server is sent is separate and unchanged: only its own group's events, to its own
/// address.</para>
/// <para><strong>On by default, and off means off.</strong> A client started with the backup off
/// sends nothing, queues nothing, and deletes anything a previous run left queued. Nothing observed
/// while it was off is ever sent.</para>
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
    private readonly SentJournal? _journal;
    private readonly string _clientVersion;

    private readonly Lock _queueGate = new();
    private readonly Lock _outboxGate = new();
    private readonly Queue<ObservedPresence> _queue = new();

    private readonly bool _enabled;
    private readonly Uri _endpoint;

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
        _journal = options.Journal;
        _clientVersion = options.ClientVersion;
        _endpoint = options.Endpoint ?? CloudSettings.DefaultEndpoint;
        _enabled = options.Enabled;
        _queuedOnDisk = _outbox.QueuedEvents;
        _cloudClock = new ServerClock(_clock);
        _mapper = new PresenceEventMapper(_timestamps, _cloudClock, _ids);

        // Turned off while the client was closed leaves nothing behind.
        if (!_enabled)
            Clear();
    }

    /// <summary>False when <c>settings.json</c> or <c>MODBOT_CLOUD_DISABLED</c> turned the backup off.</summary>
    public bool Enabled => _enabled;

    /// <summary>The Modbot Cloud this client sends to.</summary>
    public Uri Endpoint => _endpoint;

    public CloudBackupStatus Status
    {
        get
        {
            long inMemory;
            lock (_queueGate)
                inMemory = _queue.Count;

            var state = !_enabled ? CloudBackupState.Off
                : _failures > 0 ? CloudBackupState.Retrying
                : CloudBackupState.Sending;

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

        if (!_enabled || observations.Count == 0)
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
    /// oldest batch unless a backoff is being waited out. Returns true when a batch was accepted.
    /// </summary>
    public async Task<bool> PumpAsync(CancellationToken cancellationToken)
    {
        if (!_enabled)
            return false;

        var endpoint = _endpoint;

        // Only asks Cloud the time when there is something to send, so an idle client is silent.
        if (Status.Queued > 0)
            await MeasureClockAsync(endpoint, cancellationToken).ConfigureAwait(false);

        MoveQueueToDisk();

        var now = _clock.UtcNow;
        lock (_outboxGate)
        {
            _outbox.CloseIfDue(now);
            Interlocked.Exchange(ref _queuedOnDisk, _outbox.QueuedEvents);
        }

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

        if (await InstallAsync(endpoint, cancellationToken).ConfigureAwait(false) is not { } install)
            return false;

        byte[] body;
        try
        {
            body = Body(batch, events);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // An event on disk that is not the JSON this client wrote. Resending will not fix it.
            var refused = new IngestResult(IngestOutcome.Malformed);
            Finish(batch, generation, refused, endpoint);
            Record(refused, events);
            return false;
        }

        var result = await _client.SendAsync(install, body, cancellationToken).ConfigureAwait(false);
        Finish(batch, generation, result, endpoint);
        Record(result, events);
        return result.Outcome is IngestOutcome.Accepted;
    }

    /// <summary>
    /// Tells the Events screen what became of a batch: taken by Cloud, or refused for good and
    /// dropped. Anything else leaves the events waiting, which is what they are.
    /// </summary>
    private void Record(IngestResult result, IReadOnlyList<string> events)
    {
        if (_journal is null)
            return;

        if (result.Outcome is not (IngestOutcome.Accepted or IngestOutcome.Malformed or IngestOutcome.TooLarge))
            return;

        var sent = new List<ClientEvent>(events.Count);
        foreach (var json in events)
        {
            try
            {
                if (JsonSerializer.Deserialize<ClientEvent>(json, Json) is { } clientEvent)
                    sent.Add(clientEvent);
            }
            catch (JsonException)
            {
                // A line the outbox holds that this client cannot read back. The batch's fate is
                // still recorded for the events that could be read.
            }
        }

        if (result.Outcome is IngestOutcome.Accepted)
            _journal.RecordSent(SentJournal.CloudName, sent, JournalDestination.Cloud);
        else
            _journal.RecordFailed(SentJournal.CloudName, sent, JournalDestination.Cloud);
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
        var mapped = taken.Select(o => (Observation: o, Event: _mapper.MapAnyInstance(o))).ToList();
        var events = mapped.Select(m => JsonSerializer.Serialize(m.Event, Json)).ToList();

        lock (_outboxGate)
        {
            if (generation != Volatile.Read(ref _generation))
                return;

            _outbox.Append(events, since);
        }

        // Written once the events are on disk, so the screen never shows an event queued for Cloud
        // that a crash a moment later would have lost. The key is worked out from the observation,
        // which is how this line and the paired server's line about the same event become one row.
        foreach (var (observation, clientEvent) in mapped)
        {
            _journal?.RecordQueued(
                SentJournal.CloudName,
                JournalDestination.Cloud,
                SentJournal.KeyFor(observation),
                clientEvent);
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

    private byte[] Body(OutboxBatch batch, IReadOnlyList<string> events)
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

            // No modbotServerId. Cloud still accepts one, but nothing about the backup comes from,
            // or is tied to, a paired Modbot server (cloud event backup spec 3.3).

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

    /// <summary>Forgets everything queued.</summary>
    private void Clear()
    {
        lock (_queueGate)
        {
            _queue.Clear();
            _queuedSince = null;
            Interlocked.Increment(ref _generation);
        }

        lock (_outboxGate)
        {
            _outbox.Clear();
            Interlocked.Exchange(ref _queuedOnDisk, 0);
        }

        _failures = 0;
        _notBefore = null;
    }
}
