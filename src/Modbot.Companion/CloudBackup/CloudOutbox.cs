using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Modbot.Companion.CloudBackup;

/// <summary>A closed batch waiting to be sent.</summary>
public sealed record OutboxBatch(string Name, long Sequence, int Events);

/// <summary>
/// The event backup's queue on disk: events waiting to go to Modbot Cloud, kept across restarts and
/// any length of time offline.
/// </summary>
/// <remarks>
/// <para><strong>What is written to your disk.</strong> One folder, <c>%APPDATA%\Modbot\cloud</c>,
/// holding presence events not yet sent — each one the JSON of a <c>CompanionEvent</c>, the same fields a
/// Modbot server is sent, and nothing else. <c>open.jsonl</c> is the batch being filled, one event per
/// row. Closed batches are <c>batch-&lt;number&gt;-&lt;events&gt;.json.gz</c>, gzipped, sent oldest first
/// and deleted the moment Cloud accepts them.</para>
/// <para><strong>It forgets on purpose.</strong> The folder is capped at <see cref="DefaultCap"/>. When
/// closing a batch passes the cap, the oldest batches are deleted first and their events counted in
/// <see cref="DroppedEvents"/>. Starting the client with the backup off deletes everything here.</para>
/// <para><strong>Nothing in here is sent except by <see cref="CloudEventBackup"/></strong>, to the one
/// Cloud address it was started with.</para>
/// <para>Not thread-safe: <see cref="CloudEventBackup"/> is its only user and holds a lock around it.</para>
/// </remarks>
public sealed partial class CloudOutbox
{
    /// <summary>
    /// 20 MB. An event is a few hundred bytes of JSON and compresses well, so this is hundreds of
    /// thousands of events — months offline for anyone — and small enough not to matter on a disk.
    /// </summary>
    public const long DefaultCap = 20L * 1024 * 1024;

    /// <summary>A batch closes at this many events, the protocol's batch size toward a server.</summary>
    public const int MaxEventsPerBatch = 500;

    /// <summary>A batch closes once its events reach this many bytes of JSON.</summary>
    public const int MaxBytesPerBatch = 256 * 1024;

    /// <summary>A batch closes this long after its first event, however few it holds.</summary>
    public static readonly TimeSpan MaxBatchAge = TimeSpan.FromSeconds(60);

    private const string OpenFileName = "open.jsonl";

    private readonly string _directory;
    private readonly long _cap;

    private int _openEvents;
    private long _openBytes;
    private DateTimeOffset? _openSince;
    private long _nextSequence;

    public CloudOutbox(string directory, long cap = DefaultCap)
    {
        _directory = directory;
        _cap = Math.Max(MaxBytesPerBatch, cap);

        Directory.CreateDirectory(_directory);
        _nextSequence = Batches().Select(b => b.Sequence).DefaultIfEmpty(0).Max() + 1;

        // A batch left open by the last run is closed now: its events are already late.
        if (File.Exists(OpenPath))
        {
            _openEvents = File.ReadLines(OpenPath).Count(l => l.Length > 0);
            Close();
        }
    }

    /// <summary>Events dropped because the folder passed its cap, since this outbox was made.</summary>
    public long DroppedEvents { get; private set; }

    /// <summary>Events waiting on disk, open and closed.</summary>
    public long QueuedEvents => _openEvents + Batches().Sum(b => (long)b.Events);

    private string OpenPath => Path.Combine(_directory, OpenFileName);

    /// <summary>Adds events, each already its JSON, to the open batch, closing it as often as it fills.</summary>
    /// <param name="since">When these events were observed, which is when a batch they start begins to age.</param>
    public void Append(IReadOnlyList<string> events, DateTimeOffset since)
    {
        if (events.Count == 0)
            return;

        Directory.CreateDirectory(_directory);

        var writer = OpenWriter();
        try
        {
            foreach (var json in events)
            {
                writer.WriteLine(json);

                _openSince ??= since;
                _openEvents++;
                _openBytes += Encoding.UTF8.GetByteCount(json) + 1;

                if (_openEvents >= MaxEventsPerBatch || _openBytes >= MaxBytesPerBatch)
                {
                    writer.Dispose();
                    Close();
                    writer = OpenWriter();
                }
            }
        }
        finally
        {
            writer.Dispose();
        }

        if (_openEvents == 0 && File.Exists(OpenPath))
            File.Delete(OpenPath);
    }

    /// <summary>Closes the open batch once its first event is <see cref="MaxBatchAge"/> old.</summary>
    public void CloseIfDue(DateTimeOffset now)
    {
        if (_openEvents > 0 && _openSince is { } since && now - since >= MaxBatchAge)
            Close();
    }

    /// <summary>The closed batches, oldest first.</summary>
    public IReadOnlyList<OutboxBatch> Batches()
    {
        if (!Directory.Exists(_directory))
            return [];

        var batches = new List<OutboxBatch>();
        foreach (var path in Directory.EnumerateFiles(_directory, "batch-*.json.gz"))
        {
            var match = BatchName().Match(Path.GetFileName(path));
            if (match.Success)
            {
                batches.Add(new OutboxBatch(
                    Path.GetFileName(path),
                    long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)));
            }
        }

        return [.. batches.OrderBy(b => b.Sequence)];
    }

    /// <summary>The oldest closed batch, or null when there is none.</summary>
    public OutboxBatch? Oldest() => Batches().FirstOrDefault();

    /// <summary>The batch's events, each as the JSON it will be sent as.</summary>
    public IReadOnlyList<string> ReadEvents(OutboxBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        using var file = new FileStream(Path.Combine(_directory, batch.Name), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        var events = new List<string>(batch.Events);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
                events.Add(line);
        }

        return events;
    }

    /// <summary>Deletes a batch Cloud has accepted, or refused for good.</summary>
    public void Delete(OutboxBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var path = Path.Combine(_directory, batch.Name);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>Deletes everything queued.</summary>
    public void Clear()
    {
        if (Directory.Exists(_directory))
        {
            foreach (var path in Directory.EnumerateFiles(_directory))
                File.Delete(path);
        }

        _openEvents = 0;
        _openBytes = 0;
        _openSince = null;
    }

    private StreamWriter OpenWriter() =>
        new(new FileStream(OpenPath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));

    /// <summary>Gzips the open batch into a numbered file, then enforces the cap.</summary>
    private void Close()
    {
        if (_openEvents == 0 || !File.Exists(OpenPath))
        {
            _openEvents = 0;
            _openBytes = 0;
            _openSince = null;
            return;
        }

        var name = $"batch-{_nextSequence.ToString("D10", CultureInfo.InvariantCulture)}-{_openEvents.ToString(CultureInfo.InvariantCulture)}.json.gz";
        var final = Path.Combine(_directory, name);
        var temporary = final + ".tmp";

        using (var source = new FileStream(OpenPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var gzip = new GZipStream(target, CompressionLevel.Optimal))
        {
            source.CopyTo(gzip);
        }

        // Renamed into place before the open file goes, so a crash in between leaves the events
        // twice rather than not at all. Cloud keeps one copy of an event id it is sent twice.
        File.Move(temporary, final, overwrite: true);
        File.Delete(OpenPath);

        _nextSequence++;
        _openEvents = 0;
        _openBytes = 0;
        _openSince = null;

        EnforceCap();
    }

    private void EnforceCap()
    {
        var batches = Batches().ToList();
        var sizes = batches.ToDictionary(b => b.Name, b => new FileInfo(Path.Combine(_directory, b.Name)).Length);
        var total = sizes.Values.Sum();

        while (total > _cap && batches.Count > 1)
        {
            var oldest = batches[0];
            batches.RemoveAt(0);
            total -= sizes[oldest.Name];
            DroppedEvents += oldest.Events;
            Delete(oldest);
        }
    }

    [GeneratedRegex(@"^batch-(\d{10})-(\d+)\.json\.gz$")]
    private static partial Regex BatchName();
}
