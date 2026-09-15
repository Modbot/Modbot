using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Modbot.Client.CloudBackup;

/// <summary>A closed batch waiting to be sent.</summary>
public sealed record OutboxBatch(string Name, long Sequence, int Lines);

/// <summary>
/// The log backup's queue on disk: lines waiting to go to Modbot Cloud, kept across restarts and
/// any length of time offline.
/// </summary>
/// <remarks>
/// <para><strong>What is written to your disk.</strong> One folder, <c>%APPDATA%\Modbot\cloud</c>,
/// holding VRChat log lines not yet sent — the fields listed on <see cref="BackupLine"/> and nothing
/// else. <c>open.jsonl</c> is the batch being filled, one line per row. Closed batches are
/// <c>batch-&lt;number&gt;-&lt;lines&gt;.json.gz</c>, gzipped, sent oldest first and deleted the moment
/// Cloud accepts them. <c>sent-through.json</c> records, per log file, the last offset queued, so a
/// restart knows where it left off.</para>
/// <para><strong>It forgets on purpose.</strong> The whole folder is capped at <see cref="DefaultCap"/>.
/// When closing a batch passes the cap, the oldest batches are deleted first and their lines
/// counted in <see cref="DroppedLines"/>. Turning the backup off deletes everything here.</para>
/// <para><strong>Nothing in here is sent except by <see cref="CloudLogBackup"/></strong>, to the one
/// Cloud address it has settled on.</para>
/// <para>Not thread-safe: <see cref="CloudLogBackup"/> is its only user and holds a lock around it.</para>
/// </remarks>
public sealed partial class CloudOutbox
{
    /// <summary>
    /// 100 MB. At about ten to one compression that is roughly 500 hours of VRChat, which is weeks
    /// offline before anything is dropped, and small enough not to matter on anyone's disk.
    /// </summary>
    public const long DefaultCap = 100L * 1024 * 1024;

    /// <summary>A batch closes at this many lines.</summary>
    public const int MaxLinesPerBatch = 1_000;

    /// <summary>A batch closes once its lines reach this many bytes of JSON.</summary>
    public const int MaxBytesPerBatch = 512 * 1024;

    /// <summary>A batch closes this long after its first line, however few it holds.</summary>
    public static readonly TimeSpan MaxBatchAge = TimeSpan.FromSeconds(60);

    private const string OpenFileName = "open.jsonl";
    private const string SentThroughFileName = "sent-through.json";

    /// <summary>How many log files' positions are remembered. Only the newest few can be replayed.</summary>
    private const int FilesRemembered = 8;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _directory;
    private readonly long _cap;
    private readonly Dictionary<string, long> _sentThrough;
    private readonly List<string> _sentThroughOrder = [];

    private int _openLines;
    private long _openBytes;
    private DateTimeOffset? _openSince;
    private long _nextSequence;

    public CloudOutbox(string directory, long cap = DefaultCap)
    {
        _directory = directory;
        _cap = Math.Max(MaxBytesPerBatch, cap);
        _sentThrough = LoadSentThrough();

        Directory.CreateDirectory(_directory);
        _nextSequence = Batches().Select(b => b.Sequence).DefaultIfEmpty(0).Max() + 1;

        // A batch left open by the last run is closed now: its lines are already late.
        if (File.Exists(OpenPath))
        {
            _openLines = File.ReadLines(OpenPath).Count(l => l.Length > 0);
            Close();
        }
    }

    /// <summary>Lines dropped because the folder passed its cap, since this outbox was made.</summary>
    public long DroppedLines { get; private set; }

    /// <summary>Lines waiting on disk, open and closed.</summary>
    public long QueuedLines => _openLines + Batches().Sum(b => (long)b.Lines);

    /// <summary>
    /// Where each log file had got to when the last run stopped queuing, as this outbox was opened.
    /// </summary>
    public IReadOnlyDictionary<string, long> SentThrough => _sentThrough;

    private string OpenPath => Path.Combine(_directory, OpenFileName);

    private string SentThroughPath => Path.Combine(_directory, SentThroughFileName);

    /// <summary>Adds lines to the open batch, closing it as often as it fills.</summary>
    /// <param name="now">When these lines were read, which is when a batch they start begins to age.</param>
    public void Append(IReadOnlyList<BackupLine> lines, DateTimeOffset now)
    {
        if (lines.Count == 0)
            return;

        Directory.CreateDirectory(_directory);

        var writer = new StreamWriter(new FileStream(OpenPath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        try
        {
            foreach (var line in lines)
            {
                var json = JsonSerializer.Serialize(line, Json);
                writer.WriteLine(json);

                _openSince ??= now;
                _openLines++;
                _openBytes += Encoding.UTF8.GetByteCount(json) + 1;
                Remember(line.File, line.Offset);

                if (_openLines >= MaxLinesPerBatch || _openBytes >= MaxBytesPerBatch)
                {
                    writer.Dispose();
                    Close();
                    writer = new StreamWriter(new FileStream(OpenPath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                }
            }
        }
        finally
        {
            writer.Dispose();
        }

        if (_openLines == 0 && File.Exists(OpenPath))
            File.Delete(OpenPath);

        SaveSentThrough();
    }

    /// <summary>Closes the open batch once its first line is <see cref="MaxBatchAge"/> old.</summary>
    public void CloseIfDue(DateTimeOffset now)
    {
        if (_openLines > 0 && _openSince is { } since && now - since >= MaxBatchAge)
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

    /// <summary>The batch's lines, each as the JSON it will be sent as.</summary>
    public IReadOnlyList<string> ReadLines(OutboxBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        using var file = new FileStream(Path.Combine(_directory, batch.Name), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        var lines = new List<string>(batch.Lines);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
                lines.Add(line);
        }

        return lines;
    }

    /// <summary>Deletes a batch Cloud has accepted, or refused for good.</summary>
    public void Delete(OutboxBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var path = Path.Combine(_directory, batch.Name);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>Deletes everything queued, and where each file had got to.</summary>
    public void Clear()
    {
        if (Directory.Exists(_directory))
        {
            foreach (var path in Directory.EnumerateFiles(_directory))
                File.Delete(path);
        }

        _openLines = 0;
        _openBytes = 0;
        _openSince = null;
        _sentThrough.Clear();
        _sentThroughOrder.Clear();
    }

    /// <summary>Gzips the open batch into a numbered file, then enforces the cap.</summary>
    private void Close()
    {
        if (_openLines == 0 || !File.Exists(OpenPath))
        {
            _openLines = 0;
            _openBytes = 0;
            _openSince = null;
            return;
        }

        var name = $"batch-{_nextSequence.ToString("D10", CultureInfo.InvariantCulture)}-{_openLines.ToString(CultureInfo.InvariantCulture)}.json.gz";
        var final = Path.Combine(_directory, name);
        var temporary = final + ".tmp";

        using (var source = new FileStream(OpenPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var gzip = new GZipStream(target, CompressionLevel.Optimal))
        {
            source.CopyTo(gzip);
        }

        // Renamed into place before the open file goes, so a crash in between leaves the lines
        // twice rather than not at all. Cloud keeps one copy of a line it is sent twice.
        File.Move(temporary, final, overwrite: true);
        File.Delete(OpenPath);

        _nextSequence++;
        _openLines = 0;
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
            DroppedLines += oldest.Lines;
            Delete(oldest);
        }
    }

    private void Remember(string file, long offset)
    {
        if (_sentThrough.TryGetValue(file, out var existing) && existing >= offset)
            return;

        _sentThrough[file] = offset;
        _sentThroughOrder.Remove(file);
        _sentThroughOrder.Add(file);

        while (_sentThroughOrder.Count > FilesRemembered)
        {
            _sentThrough.Remove(_sentThroughOrder[0]);
            _sentThroughOrder.RemoveAt(0);
        }
    }

    private Dictionary<string, long> LoadSentThrough()
    {
        var path = Path.Combine(_directory, SentThroughFileName);
        if (!File.Exists(path))
            return new Dictionary<string, long>(StringComparer.Ordinal);

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path), Json)
                ?? new Dictionary<string, long>();

            foreach (var file in loaded.Keys)
                _sentThroughOrder.Add(file);

            return new Dictionary<string, long>(loaded, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A half-written file after a power cut. Starting from nothing costs at most a replay
            // that is not sent; refusing to start would cost the whole backup.
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }
    }

    private void SaveSentThrough()
    {
        var temporary = SentThroughPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_sentThrough, Json), new UTF8Encoding(false));
        File.Move(temporary, SentThroughPath, overwrite: true);
    }

    [GeneratedRegex(@"^batch-(\d{10})-(\d+)\.json\.gz$")]
    private static partial Regex BatchName();
}
