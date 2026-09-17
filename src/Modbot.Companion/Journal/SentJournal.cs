using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Core.Time;

namespace Modbot.Companion.Journal;

/// <summary>What a journal line is about.</summary>
/// <remarks>
/// Written as words rather than numbers, because this file is meant to be opened in a text editor
/// by somebody checking up on the program. Numbers written by older versions still read.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<JournalEntryKind>))]
public enum JournalEntryKind
{
    /// <summary>One event a destination has taken.</summary>
    Sent,

    /// <summary>
    /// One event that was <em>not</em> sent, and why. Silence and refusal look the same from
    /// outside, so the journal says which it was.
    /// </summary>
    Withheld,

    /// <summary>A change of state worth seeing in the same list: paused, resumed, token rejected.</summary>
    Note,

    /// <summary>One event this client processed and queued, which nothing has taken yet.</summary>
    Waiting,

    /// <summary>One event a destination refused for good. It was dropped, not retried.</summary>
    Failed,
}

/// <summary>Where an event this client processed was headed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JournalDestination>))]
public enum JournalDestination
{
    /// <summary>Nowhere. Notes, and lines written by versions that only reported to servers.</summary>
    None,

    /// <summary>The paired group's own server.</summary>
    Server,

    /// <summary>Modbot Cloud.</summary>
    Cloud,
}

/// <param name="At">Server-corrected time, the same clock the fact was reported on.</param>
/// <param name="ServerId">
/// Who this line is about: the moderator's own label for a paired server, its address when the
/// client has no label for it, or "Modbot Cloud". Never sent anywhere.
/// </param>
/// <param name="Summary">
/// One line of plain English. This is the whole point of the type: a moderator reading it should
/// need no explanation of what was disclosed about them.
/// </param>
/// <param name="EventKey">
/// Ties every line about one event together, so the screen can show the event once with what each
/// destination did with it. Worked out from what was observed, so both destinations arrive at the
/// same key without either having to be told it. Null on a note, and on lines written by older
/// versions.
/// </param>
/// <param name="SendId">
/// The <c>companionEventId</c> this destination was given. One event has a different one per
/// destination, which is why it cannot be the key.
/// </param>
public sealed record JournalEntry(
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("kind")] JournalEntryKind Kind,
    [property: JsonPropertyName("serverId")] string ServerId,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("eventKey")] string? EventKey = null,
    [property: JsonPropertyName("sendId")] string? SendId = null,
    [property: JsonPropertyName("destination")] JournalDestination Destination = JournalDestination.None);

/// <summary>
/// One event this client processed, with what happened to it at each destination — which is one
/// line on the Events screen.
/// </summary>
/// <param name="ServerId">
/// The paired server this event was for, named when the client has a name for it and addressed
/// when it does not. Null when no paired server was involved.
/// </param>
/// <param name="ServerState">What the paired server did with it, or null when it was never for one.</param>
/// <param name="CloudState">What Modbot Cloud did with it, or null when the backup is off.</param>
public sealed record JournalRow(
    DateTimeOffset At,
    string Summary,
    string? ServerId,
    JournalEntryKind? ServerState,
    JournalEntryKind? CloudState)
{
    /// <summary>A line that explains a gap rather than describing an event: paused, resumed, stopped.</summary>
    public bool IsNote => ServerState is null && CloudState is null;
}

/// <summary>
/// The Events screen's backing store: the last few hundred things this client did with what it
/// saw, in plain language, and where each of them went.
/// </summary>
/// <remarks>
/// <para><strong>Why this exists.</strong> A volunteer moderator is being asked to run background
/// software on a personal machine that watches what they do in VRChat. "Trust the source comments"
/// is a fine answer for somebody who reads C#, and no answer at all for everybody else. This is
/// the answer for everybody else: a screen, on demand, showing exactly what left the machine and
/// where it went.</para>
/// <para><strong>One row per event, not per send.</strong> An event goes to two places — the
/// paired group's own server, and Modbot Cloud — and a moderator counting rows should be counting
/// the things that happened, not the number of times each was handed over. The file is still
/// append-only: a line is written when the event is processed and another when a destination takes
/// it, and lines about the same event are folded into one row when the screen is drawn. Appending
/// is what makes the record survive a crash between disclosing something and recording it, which
/// rewriting an earlier line would not.</para>
/// <para><strong>What is written to your disk.</strong> One small file in Modbot's own folder,
/// holding these one-line summaries and nothing more. It is a record of what was disclosed, not a
/// second copy of it: there are no raw log lines here.</para>
/// <para><strong>Nothing here is ever transmitted.</strong> The journal is written for the
/// moderator and read by the moderator. No server, including the ones the events went to, can ask
/// for it — there is no command channel for it to ask down.</para>
/// <para><strong>It survives a restart</strong>, because "what did this thing send yesterday" is a
/// question people ask after they get suspicious, not before.</para>
/// </remarks>
public sealed class SentJournal
{
    /// <summary>
    /// How many lines are kept. An event takes a line when it is processed and another as each
    /// destination takes it, so this is several hundred events: enough to cover an evening's
    /// session, and bounded.
    /// </summary>
    public const int DefaultCapacity = 1_500;

    /// <summary>What the journal calls Modbot Cloud.</summary>
    public const string CloudName = "Modbot Cloud";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly IModbotClock _clock;
    private readonly int _capacity;
    private readonly List<JournalEntry> _entries = [];

    /// <summary>
    /// Which event each destination's <c>companionEventId</c> belongs to, so the line written when a
    /// batch is taken can be folded into the row written when the event was processed.
    /// </summary>
    private readonly Dictionary<string, string> _keysBySendId = new(StringComparer.Ordinal);

    private readonly Lock _gate = new();

    public SentJournal(string path, IModbotClock clock, int capacity = DefaultCapacity)
    {
        _path = path;
        _clock = clock;
        _capacity = Math.Max(1, capacity);

        Load();
    }

    /// <summary>How many lines are held. The screen counts events, which is fewer.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    /// <summary>The raw lines, newest first.</summary>
    public IReadOnlyList<JournalEntry> Recent(int max = DefaultCapacity)
    {
        lock (_gate)
            return [.. _entries.AsEnumerable().Reverse().Take(max)];
    }

    /// <summary>
    /// The events this client processed, newest first, one row each however many places they went.
    /// </summary>
    public IReadOnlyList<JournalRow> Events(int max = DefaultCapacity)
    {
        List<JournalEntry> entries;
        lock (_gate)
            entries = [.. _entries];

        var order = new List<string>();
        var building = new Dictionary<string, Row>(StringComparer.Ordinal);

        // A line written when a batch was taken carries its own key, but one written by a version
        // that did not, or whose first line has since aged out, is matched up by send id instead.
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is { EventKey: { Length: > 0 } key, SendId: { Length: > 0 } sendId })
                keys[sendId] = key;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];

            var rowKey = entry.Kind is JournalEntryKind.Note
                ? $"note:{i}"
                : entry.EventKey is { Length: > 0 } key ? key
                : entry.SendId is { Length: > 0 } sendId && keys.TryGetValue(sendId, out var known) ? known
                : entry.SendId is { Length: > 0 } ownId ? ownId
                : $"line:{i}";

            if (!building.TryGetValue(rowKey, out var row))
            {
                row = new Row(entry.At);
                building[rowKey] = row;
                order.Add(rowKey);
            }

            row.Add(entry);
        }

        return [.. order.Select(k => building[k].ToRow()).Reverse().Take(max)];
    }

    /// <summary>
    /// The key that ties every line about one event together.
    /// </summary>
    /// <remarks>
    /// Worked out from what was observed rather than handed out, so the paired server's half of the
    /// client and the Modbot Cloud backup's half arrive at the same key for the same event without
    /// either telling the other. A short hash, so the file stays small and gains no detail it did
    /// not already carry in the summary.
    /// </remarks>
    public static string KeyFor(ObservedPresence observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var text = string.Join(
            '',
            observation.Kind,
            observation.OccurredAtLocal.Ticks,
            observation.SubjectId,
            observation.Instance.WorldId,
            observation.Instance.InstanceId,
            observation.AvatarName ?? string.Empty);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    /// <summary>Records one event this client processed and queued for a destination.</summary>
    public void RecordQueued(
        string serverId,
        JournalDestination destination,
        string eventKey,
        CompanionEvent companionEvent)
    {
        ArgumentNullException.ThrowIfNull(companionEvent);

        Append([new JournalEntry(
            _clock.UtcNow,
            JournalEntryKind.Waiting,
            serverId,
            Describe(companionEvent),
            eventKey,
            companionEvent.CompanionEventId,
            destination)]);
    }

    /// <summary>Records a batch a destination accepted — one line per event disclosed.</summary>
    public void RecordSent(
        string serverId,
        IEnumerable<CompanionEvent> events,
        JournalDestination destination = JournalDestination.Server)
        => RecordOutcome(serverId, events, destination, JournalEntryKind.Sent);

    /// <summary>Records events a destination refused for good, which are dropped rather than retried.</summary>
    public void RecordFailed(
        string serverId,
        IEnumerable<CompanionEvent> events,
        JournalDestination destination = JournalDestination.Server)
        => RecordOutcome(serverId, events, destination, JournalEntryKind.Failed);

    /// <summary>Records observations that were deliberately not disclosed, and why.</summary>
    /// <remarks>
    /// The reason is all this line holds. A paused client captures nothing, so there is nothing to
    /// describe — and writing a description would be capturing it. When another destination did
    /// take the same event, its line supplies the description and the two fold into one row.
    /// </remarks>
    public void RecordWithheld(string serverId, string reason, int count = 1, string? eventKey = null)
    {
        var line = count == 1 ? reason : $"{reason} (×{count})";
        Append([new JournalEntry(
            _clock.UtcNow,
            JournalEntryKind.Withheld,
            serverId,
            line,
            eventKey,
            null,
            JournalDestination.Server)]);
    }

    /// <summary>Records a state change worth seeing beside the events.</summary>
    public void RecordNote(string serverId, string note)
        => Append([new JournalEntry(_clock.UtcNow, JournalEntryKind.Note, serverId, note)]);

    /// <summary>Forgets everything. Used when a pairing is removed.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _keysBySendId.Clear();
            Rewrite();
        }
    }

    /// <summary>
    /// One event, as a sentence.
    /// </summary>
    /// <remarks>
    /// The display name is used when the log gave one, because "Rin joined" is a sentence a
    /// moderator can check against their memory of the evening and "usr_8f2c… joined" is not. The
    /// id is still what was sent as identity; the name is history.
    /// </remarks>
    public static string Describe(CompanionEvent companionEvent)
    {
        ArgumentNullException.ThrowIfNull(companionEvent);

        var who = companionEvent.Data.TryGetValue("displayName", out var name) && name.Length > 0
            ? $"{name} ({companionEvent.SubjectId})"
            : companionEvent.SubjectId;

        var where = $"{companionEvent.WorldId}:{companionEvent.InstanceId}";
        var when = companionEvent.OccurredAt.ToString("HH:mm:ss");

        return companionEvent.Type switch
        {
            CompanionEventType.InstanceJoined => $"{when} — told them {who} joined {where}",
            CompanionEventType.InstancePresenceObserved =>
                $"{when} — told them {who} was already in {where} when you arrived",
            CompanionEventType.InstanceLeft => $"{when} — told them {who} left {where}",
            CompanionEventType.LogStopped => $"{when} — told them VRChat's log stopped while you were in {where}",
            CompanionEventType.AvatarChanged =>
                companionEvent.Data.TryGetValue("avatarName", out var avatar) && avatar.Length > 0
                    ? $"{when} — told them {who} switched to the avatar “{avatar}”"
                    : $"{when} — told them {who} changed avatar",
            _ => $"{when} — told them about {who} in {where}",
        };
    }

    private void RecordOutcome(
        string serverId,
        IEnumerable<CompanionEvent> events,
        JournalDestination destination,
        JournalEntryKind kind)
    {
        ArgumentNullException.ThrowIfNull(events);

        var now = _clock.UtcNow;

        lock (_gate)
        {
            var lines = new List<JournalEntry>();
            foreach (var companionEvent in events)
            {
                // The line the event was processed on holds its key. When that line has aged out,
                // the send id becomes the key and the event stands as its own row rather than
                // going unrecorded: a disclosure must never be missing from this file.
                var key = _keysBySendId.TryGetValue(companionEvent.CompanionEventId, out var known)
                    ? known
                    : companionEvent.CompanionEventId;

                lines.Add(new JournalEntry(
                    now,
                    kind,
                    serverId,
                    Describe(companionEvent),
                    key,
                    companionEvent.CompanionEventId,
                    destination));
            }

            AppendLocked(lines);
        }
    }

    private void Append(IEnumerable<JournalEntry> entries)
    {
        lock (_gate)
            AppendLocked(entries);
    }

    private void AppendLocked(IEnumerable<JournalEntry> entries)
    {
        var added = 0;
        foreach (var entry in entries)
        {
            _entries.Add(entry);
            Remember(entry);
            added++;
        }

        if (added == 0)
            return;

        if (_entries.Count > _capacity)
        {
            _entries.RemoveRange(0, _entries.Count - _capacity);
            RememberAll();
            Rewrite();
            return;
        }

        AppendToFile(_entries.TakeLast(added));
    }

    private void Remember(JournalEntry entry)
    {
        if (entry is { EventKey: { Length: > 0 } key, SendId: { Length: > 0 } sendId })
            _keysBySendId[sendId] = key;
    }

    private void RememberAll()
    {
        _keysBySendId.Clear();
        foreach (var entry in _entries)
            Remember(entry);
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
                if (JsonSerializer.Deserialize<JournalEntry>(line, Json) is { } entry)
                    _entries.Add(entry);
            }
            catch (JsonException)
            {
                // A half-written last line. One missing line of the record is better than
                // refusing to show the moderator any of it.
            }
        }

        if (_entries.Count > _capacity)
            _entries.RemoveRange(0, _entries.Count - _capacity);

        RememberAll();
    }

    private void EnsureDirectory()
    {
        if (Path.GetDirectoryName(_path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
    }

    private void AppendToFile(IEnumerable<JournalEntry> entries)
    {
        EnsureDirectory();

        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        foreach (var entry in entries)
            writer.WriteLine(JsonSerializer.Serialize(entry, Json));
    }

    private void Rewrite()
    {
        EnsureDirectory();
        File.WriteAllLines(_path, _entries.Select(e => JsonSerializer.Serialize(e, Json)), new UTF8Encoding(false));
    }

    /// <summary>One row of the screen, while the lines about it are still being read.</summary>
    private sealed class Row(DateTimeOffset at)
    {
        private string _summary = string.Empty;
        private bool _summaryFromWithheld;
        private string? _serverId;
        private JournalEntryKind? _serverState;
        private JournalEntryKind? _cloudState;

        public void Add(JournalEntry entry)
        {
            if (entry.Kind is JournalEntryKind.Note)
            {
                _summary = entry.Summary;
                _serverId = entry.ServerId;
                return;
            }

            // Lines written before the Cloud backup existed named no destination, and every one of
            // them was about a paired server.
            var destination = entry.Destination is JournalDestination.None
                ? JournalDestination.Server
                : entry.Destination;

            if (destination is JournalDestination.Cloud)
            {
                _cloudState = entry.Kind;
            }
            else
            {
                _serverState = entry.Kind;
                _serverId = entry.ServerId;
            }

            // A withheld line carries a reason rather than a description, so it names the event
            // only until a destination that did take it says what the event was.
            var withheld = entry.Kind is JournalEntryKind.Withheld;
            if (_summary.Length == 0 || (_summaryFromWithheld && !withheld))
            {
                _summary = entry.Summary;
                _summaryFromWithheld = withheld;
            }
        }

        public JournalRow ToRow() => new(at, _summary, _serverId, _serverState, _cloudState);
    }
}
