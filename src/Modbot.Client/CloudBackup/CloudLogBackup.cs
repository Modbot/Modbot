using System.IO.Compression;
using System.Text.Json;
using Modbot.Client.Ingest;
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

/// <param name="Queued">Lines waiting, in memory and on disk.</param>
/// <param name="Dropped">Lines deleted unsent this run: the queue or the folder was full, or Cloud refused a batch for good.</param>
public sealed record CloudBackupStatus(CloudBackupState State, long Queued, long Dropped);

/// <summary>Everything the backup needs, in one place.</summary>
/// <param name="Directory">Its outbox folder, <c>%APPDATA%\Modbot\cloud</c>.</param>
/// <param name="UserProfile">The folder to hide from lines, e.g. <c>C:\Users\rin</c>.</param>
/// <param name="TimeZone">This PC's time zone, for reading VRChat's offset-less timestamps.</param>
public sealed record CloudBackupOptions(
    string Directory,
    IModbotClock Clock,
    ICloudLogClient Client,
    ICloudInstallStore Installs,
    string ClientVersion,
    bool Enabled = true,
    string? UserProfile = null,
    TimeZoneInfo? TimeZone = null,
    BackoffPolicy? Backoff = null,
    long OutboxCap = CloudOutbox.DefaultCap);

/// <summary>
/// "Send all logging to Modbot Cloud as backup": every VRChat log line this client reads, from every
/// instance, sent to Modbot Cloud (cloud log backup spec).
/// </summary>
/// <remarks>
/// <para><strong>What this sends.</strong> Every line of VRChat's output log the client reads, whatever
/// its tag and whichever instance you are in — including private and friends-only ones — with the
/// fields on <see cref="BackupLine"/>: the log file's name, the line's offset, its text with your user
/// folder hidden, its timestamp and your UTC offset, and the client's own parse of it. VRChat's log
/// names the other players around you and the instances you are in, so this is personal data about
/// you and about them.</para>
/// <para><strong>Where.</strong> To one Modbot Cloud: <c>https://cloud.modbot.co</c>, or the one a
/// paired server names. If any paired server's operator turned it off, nothing is sent and nothing
/// is queued (<see cref="CloudDestination"/>). Never to a Modbot server: those get only the presence
/// events they always have.</para>
/// <para><strong>On by default, and off means off.</strong> Turning it off stops sending at once —
/// a batch in flight is cancelled — and deletes everything queued. Turning it back on sends from that
/// moment; nothing written while it was off is ever sent.</para>
/// <para><strong>What is written to your disk.</strong> The outbox (<see cref="CloudOutbox"/>),
/// capped at 100 MB, and this client's install id and encrypted secret
/// (<see cref="DpapiCloudInstallStore"/>).</para>
/// <para><strong>It never slows the log reader.</strong> <see cref="Offer"/> puts lines on an
/// in-memory queue and returns; that queue is the only thing it shares with the reader, and it is
/// held only long enough to add to it. Writing the outbox, compressing, registering and sending all
/// happen in <see cref="RunAsync"/>, on its own task. A Cloud that hangs costs the reader nothing, and
/// presence reporting to Modbot servers carries on regardless.</para>
/// <para><strong>Backoff.</strong> No network, a <c>5xx</c> or a <c>429</c> waits out an exponential
/// backoff, or Cloud's <c>Retry-After</c>. This is Cloud's own limit, ordinary software under the
/// project's control, and has nothing to do with VRChat's cold stop.</para>
/// </remarks>
public sealed class CloudLogBackup : ILogLineSink
{
    /// <summary>Lines held in memory between the reader and the outbox before the oldest are dropped.</summary>
    public const int QueueLimit = 50_000;

    /// <summary>How often the offset to Cloud's clock is re-measured, as for a Modbot server.</summary>
    public static readonly TimeSpan ClockCheckInterval = TimeSpan.FromHours(2);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CloudOutbox _outbox;
    private readonly ICloudLogClient _client;
    private readonly ICloudInstallStore _installs;
    private readonly IModbotClock _clock;
    private readonly BackoffPolicy _backoff;
    private readonly LogTimestampConverter _timestamps;
    private readonly string _clientVersion;
    private readonly string? _userProfile;

    /// <summary>Held by the reader, briefly, to add lines; and by the pump, briefly, to take them.</summary>
    private readonly Lock _queueGate = new();

    /// <summary>Held around every outbox operation. Never by the reader, never across a request.</summary>
    private readonly Lock _outboxGate = new();

    private readonly Queue<ReadLogLine> _queue = new();

    /// <summary>When the oldest line now in <see cref="_queue"/> was offered. A batch's age counts from here.</summary>
    private DateTimeOffset? _queuedSince;

    private volatile bool _enabled;
    private volatile CloudDestination _destination = CloudDestination.Default;
    private volatile IReadOnlyDictionary<string, long> _replayFrom;
    private volatile CancellationTokenSource _sendStop = new();

    /// <summary>Bumped by every clear, so lines taken before one are never written after it.</summary>
    private int _generation;

    private long _queueDropped;
    private long _rejectedLines;
    private long _queuedOnDisk;
    private int _failures;
    private DateTimeOffset? _notBefore;

    private ServerClock? _cloudClock;
    private Uri? _clockFor;
    private DateTimeOffset? _lastClockCheck;

    public CloudLogBackup(CloudBackupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _outbox = new CloudOutbox(options.Directory, options.OutboxCap);
        _client = options.Client;
        _installs = options.Installs;
        _clock = options.Clock;
        _backoff = options.Backoff ?? new BackoffPolicy();
        _timestamps = new LogTimestampConverter(options.TimeZone);
        _clientVersion = options.ClientVersion;
        _userProfile = options.UserProfile;
        _enabled = options.Enabled;
        _replayFrom = new Dictionary<string, long>(_outbox.SentThrough, StringComparer.Ordinal);
        _queuedOnDisk = _outbox.QueuedLines;

        // A backup that was turned off while the client was closed -- settings edited by hand --
        // leaves nothing behind.
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

    public bool WantsLines => _enabled && _destination.Kind is not CloudDestinationKind.TurnedOffByServer;

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
                Interlocked.Read(ref _queueDropped) + Interlocked.Read(ref _rejectedLines) + _outbox.DroppedLines);
        }
    }

    /// <summary>
    /// Takes the reader's lines. Returns at once: it only adds to an in-memory queue.
    /// </summary>
    /// <remarks>
    /// Lines that were already in a file when the client started are kept only past where the last
    /// run had queued that file to, so a restart backs up what was written while the client was
    /// closed, and a first start does not upload hours of old log.
    /// </remarks>
    public void Offer(IReadOnlyList<ReadLogLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (!WantsLines)
            return;

        var replayFrom = _replayFrom;

        var now = _clock.UtcNow;

        lock (_queueGate)
        {
            if (_queue.Count == 0)
                _queuedSince = now;

            foreach (var line in lines)
            {
                if (line.IsReplay && !(replayFrom.TryGetValue(line.File, out var through) && line.Offset > through))
                    continue;

                _queue.Enqueue(line);
            }

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
    /// One turn: move queued lines to disk, close a batch that is due, and send the oldest batch if
    /// sending is allowed. Returns true when a batch was accepted, which means another may be ready.
    /// </summary>
    public async Task<bool> PumpAsync(CancellationToken cancellationToken)
    {
        if (!WantsLines)
            return false;

        MoveQueueToDisk();

        var now = _clock.UtcNow;
        lock (_outboxGate)
        {
            _outbox.CloseIfDue(now);
            Interlocked.Exchange(ref _queuedOnDisk, _outbox.QueuedLines);
        }

        var destination = _destination;
        if (destination is not { Kind: CloudDestinationKind.Send, Endpoint: { } endpoint })
            return false;

        if (_notBefore is { } waitUntil && now < waitUntil)
            return false;

        OutboxBatch? batch;
        IReadOnlyList<string> lines;
        int generation;
        lock (_outboxGate)
        {
            batch = _outbox.Oldest();
            if (batch is null)
                return false;

            lines = _outbox.ReadLines(batch);
            generation = Volatile.Read(ref _generation);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sendStop.Token);

        try
        {
            await MeasureClockAsync(endpoint, linked.Token).ConfigureAwait(false);

            if (await InstallAsync(endpoint, linked.Token).ConfigureAwait(false) is not { } install)
                return false;

            byte[] body;
            try
            {
                body = Body(batch, lines, destination);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                // A line on disk that is not the JSON this client wrote. Resending will not fix it.
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
        List<ReadLogLine> taken;
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

        var lines = taken.Select(l => BackupLine.From(l, _timestamps, _userProfile)).ToList();

        lock (_outboxGate)
        {
            if (generation != Volatile.Read(ref _generation))
                return;

            _outbox.Append(lines, since);
        }
    }

    private async Task MeasureClockAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        if (_clockFor != endpoint)
        {
            _cloudClock = new ServerClock(_clock);
            _clockFor = endpoint;
            _lastClockCheck = null;
        }

        if (_lastClockCheck is { } last && _clock.UtcNow - last < ClockCheckInterval)
            return;

        _lastClockCheck = _clock.UtcNow;

        if (await _client.MeasureAsync(endpoint, cancellationToken).ConfigureAwait(false) is { } sample)
            _cloudClock!.Add(sample);
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

    private byte[] Body(OutboxBatch batch, IReadOnlyList<string> lines, CloudDestination destination)
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
            writer.WriteNumber("clockOffsetMs", (long)(_cloudClock?.Offset.TotalMilliseconds ?? 0));
            writer.WriteString("clockConfidence", (_cloudClock?.Confidence ?? ClockConfidence.Unknown).ToWire());

            if (destination.ModbotServerId is { } serverId)
                writer.WriteString("modbotServerId", serverId);
            else
                writer.WriteNull("modbotServerId");

            writer.WriteStartArray("lines");
            foreach (var line in lines)
                writer.WriteRawValue(line);
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
                    Interlocked.Add(ref _rejectedLines, batch.Lines);
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
            Interlocked.Exchange(ref _queuedOnDisk, _outbox.QueuedLines);
            return true;
        }
    }

    private void Failed(TimeSpan? retryAfter)
    {
        _failures++;
        _notBefore = _clock.UtcNow + (retryAfter ?? _backoff.Delay(_failures));
    }

    /// <summary>Forgets everything queued, cancels a batch in flight, and forgets where files had got to.</summary>
    private void Clear()
    {
        lock (_queueGate)
        {
            _queue.Clear();
            Interlocked.Increment(ref _generation);
        }

        var stop = _sendStop;
        _sendStop = new CancellationTokenSource();
        stop.Cancel();

        _replayFrom = new Dictionary<string, long>(StringComparer.Ordinal);

        lock (_outboxGate)
        {
            _outbox.Clear();
            Interlocked.Exchange(ref _queuedOnDisk, 0);
        }

        _failures = 0;
        _notBefore = null;
    }
}
