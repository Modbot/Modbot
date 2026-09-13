using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Core.Time;

namespace Modbot.Client.Ingest;

/// <summary>How much the buffer is allowed to hold before it starts forgetting.</summary>
/// <param name="MaxEvents">
/// The size bound. Events are small and fixed in shape, so a count bounds the bytes too.
/// </param>
/// <param name="MaxAge">
/// The age bound. A week-old observation of somebody standing in an instance is no longer worth
/// the disk it sits on, and an unbounded buffer on a volunteer's PC is its own kind of rude.
/// </param>
public readonly record struct EventBufferLimits(int MaxEvents, TimeSpan MaxAge)
{
    public static EventBufferLimits Default { get; } = new(50_000, TimeSpan.FromDays(3));
}

/// <summary>
/// The offline buffer: one file per paired server, holding what has not been accepted yet.
/// </summary>
/// <remarks>
/// <para><strong>What is written to your disk.</strong> One file, in Modbot's own folder, holding
/// exactly the events queued to be sent — the same fields listed on <see cref="ClientEvent"/> and
/// nothing else. No log lines, no copies of VRChat's log, nothing about instances outside the
/// group this file belongs to. Each server gets its own file, so one group's operator cannot be
/// handed the other's data by accident.</para>
/// <para><strong>Why it exists at all.</strong> VRChat sessions outlive network blips and a
/// moderator's home connection is not a datacentre. A dropped connection has to cost latency, not
/// data — presence history is the one thing in Modbot that cannot be backfilled.</para>
/// <para><strong>It forgets on purpose.</strong> Bounded by count and by age, dropping the oldest
/// first, so a client left offline for a fortnight does not fill a disk. Drops are counted rather
/// than silent, because "we quietly lost some" is exactly the failure this subsystem must not
/// have.</para>
/// <para>The <c>clientEventId</c> is stored with each event, so a retry after a restart carries
/// the same idempotency key it would have carried before.</para>
/// </remarks>
public sealed class FileEventBuffer
{
    private sealed record Entry(
        [property: JsonPropertyName("enqueuedAt")] DateTimeOffset EnqueuedAt,
        [property: JsonPropertyName("event")] ClientEvent Event);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly IModbotClock _clock;
    private readonly EventBufferLimits _limits;
    private readonly List<Entry> _entries = [];
    private readonly Lock _gate = new();

    public FileEventBuffer(string path, IModbotClock clock, EventBufferLimits? limits = null)
    {
        _path = path;
        _clock = clock;
        _limits = limits ?? EventBufferLimits.Default;

        Load();
        lock (_gate)
        {
            if (Prune())
                Rewrite();
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    /// <summary>How many events were dropped because the buffer was full or they had aged out.</summary>
    public int Dropped { get; private set; }

    /// <summary>Queues one event for sending and puts it on disk before returning.</summary>
    public void Add(ClientEvent clientEvent)
    {
        lock (_gate)
        {
            _entries.Add(new Entry(_clock.UtcNow, clientEvent));

            if (Prune())
            {
                Rewrite();
                return;
            }

            Append(_entries[^1]);
        }
    }

    /// <summary>
    /// The next batch's worth, oldest first, left in the buffer until the server accepts them.
    /// Nothing is removed by reading: a send that never completes must not lose its events.
    /// </summary>
    public IReadOnlyList<ClientEvent> Peek(int max)
    {
        lock (_gate)
        {
            if (Prune())
                Rewrite();

            return [.. _entries.Take(max).Select(e => e.Event)];
        }
    }

    /// <summary>Drops events the server has accepted, or permanently rejected.</summary>
    public void Remove(IEnumerable<string> clientEventIds)
    {
        var ids = clientEventIds.ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0)
            return;

        lock (_gate)
        {
            _entries.RemoveAll(e => ids.Contains(e.Event.ClientEventId));
            Prune();
            Rewrite();
        }
    }

    /// <summary>Drops everything. Used when a pairing is removed, so its data does not linger.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Rewrite();
        }
    }

    /// <summary>
    /// Enforces both bounds, oldest first. Returns whether anything was dropped, which is also
    /// whether the file needs rewriting.
    /// </summary>
    private bool Prune()
    {
        var before = _entries.Count;
        var cutoff = _clock.UtcNow - _limits.MaxAge;

        _entries.RemoveAll(e => e.EnqueuedAt < cutoff);

        if (_entries.Count > _limits.MaxEvents)
            _entries.RemoveRange(0, _entries.Count - _limits.MaxEvents);

        var dropped = before - _entries.Count;
        Dropped += dropped;
        return dropped > 0;
    }

    private void Load()
    {
        if (!File.Exists(_path))
            return;

        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                if (JsonSerializer.Deserialize<Entry>(line, Json) is { } entry)
                    _entries.Add(entry);
            }
            catch (JsonException)
            {
                // A half-written last line after a power cut. Skipping it loses one observation;
                // refusing to start would lose every observation from here on.
            }
        }
    }

    private void Append(Entry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.WriteLine(JsonSerializer.Serialize(entry, Json));
    }

    private void Rewrite()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var lines = _entries.Select(e => JsonSerializer.Serialize(e, Json));
        File.WriteAllLines(_path, lines, new UTF8Encoding(false));
    }
}
