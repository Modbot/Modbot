using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Client.Ingest;
using Modbot.Core.Time;

namespace Modbot.Client.Journal;

/// <summary>What a journal line is about.</summary>
public enum JournalEntryKind
{
    /// <summary>One observation that was transmitted to one server.</summary>
    Sent,

    /// <summary>
    /// One observation that was <em>not</em> transmitted, and why. Silence and refusal look the
    /// same from outside, so the journal says which it was.
    /// </summary>
    Withheld,

    /// <summary>A change of state worth seeing in the same list: paused, resumed, token rejected.</summary>
    Note,
}

/// <param name="At">Server-corrected time, the same clock the fact was reported on.</param>
/// <param name="ServerId">The moderator's own label for the server. Never sent anywhere.</param>
/// <param name="Summary">
/// One line of plain English. This is the whole point of the type: a moderator reading it should
/// need no explanation of what was disclosed about them.
/// </param>
public sealed record JournalEntry(
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("kind")] JournalEntryKind Kind,
    [property: JsonPropertyName("serverId")] string ServerId,
    [property: JsonPropertyName("summary")] string Summary);

/// <summary>
/// The "what I have sent" screen's backing store: the last few hundred things this client
/// disclosed, in plain language, per server.
/// </summary>
/// <remarks>
/// <para><strong>Why this exists.</strong> A volunteer moderator is being asked to run background
/// software on a personal machine that watches what they do in VRChat. "Trust the source comments"
/// is a fine answer for somebody who reads C#, and no answer at all for everybody else. This is
/// the answer for everybody else: a screen, on demand, showing exactly what left the machine.</para>
/// <para><strong>What is written to your disk.</strong> One small file per paired server in
/// Modbot's own folder, holding these one-line summaries and nothing more. It is a record of what
/// was disclosed, not a second copy of it: there are no raw log lines here, and nothing about
/// instances outside the group the file belongs to.</para>
/// <para><strong>Nothing here is ever transmitted.</strong> The journal is written for the
/// moderator and read by the moderator. No server, including the one the events went to, can ask
/// for it — there is no command channel for it to ask down.</para>
/// <para><strong>It survives a restart</strong>, because "what did this thing send yesterday" is a
/// question people ask after they get suspicious, not before.</para>
/// </remarks>
public sealed class SentJournal
{
    /// <summary>How many lines are kept. Enough to cover an evening's session, and bounded.</summary>
    public const int DefaultCapacity = 500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly IModbotClock _clock;
    private readonly int _capacity;
    private readonly List<JournalEntry> _entries = [];
    private readonly Lock _gate = new();

    public SentJournal(string path, IModbotClock clock, int capacity = DefaultCapacity)
    {
        _path = path;
        _clock = clock;
        _capacity = Math.Max(1, capacity);

        Load();
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    /// <summary>Newest first, which is the order the screen shows them in.</summary>
    public IReadOnlyList<JournalEntry> Recent(int max = DefaultCapacity)
    {
        lock (_gate)
            return [.. _entries.AsEnumerable().Reverse().Take(max)];
    }

    /// <summary>Records a batch the server accepted — one line per observation disclosed.</summary>
    public void RecordSent(string serverId, IEnumerable<ClientEvent> events)
    {
        var now = _clock.UtcNow;
        Append(events.Select(e => new JournalEntry(now, JournalEntryKind.Sent, serverId, Describe(e))));
    }

    /// <summary>Records observations that were deliberately not disclosed, and why.</summary>
    public void RecordWithheld(string serverId, string reason, int count = 1)
    {
        var now = _clock.UtcNow;
        var line = count == 1 ? reason : $"{reason} (×{count})";
        Append([new JournalEntry(now, JournalEntryKind.Withheld, serverId, line)]);
    }

    /// <summary>Records a state change worth seeing beside the disclosures.</summary>
    public void RecordNote(string serverId, string note)
        => Append([new JournalEntry(_clock.UtcNow, JournalEntryKind.Note, serverId, note)]);

    /// <summary>Forgets everything. Used when a pairing is removed.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Rewrite();
        }
    }

    /// <summary>
    /// One observation, as a sentence.
    /// </summary>
    /// <remarks>
    /// The display name is used when the log gave one, because "Rin joined" is a sentence a
    /// moderator can check against their memory of the evening and "usr_8f2c… joined" is not. The
    /// id is still what was sent as identity; the name is history.
    /// </remarks>
    public static string Describe(ClientEvent clientEvent)
    {
        var who = clientEvent.Data.TryGetValue("displayName", out var name) && name.Length > 0
            ? $"{name} ({clientEvent.SubjectId})"
            : clientEvent.SubjectId;

        var where = $"{clientEvent.WorldId}:{clientEvent.InstanceId}";
        var when = clientEvent.OccurredAt.ToString("HH:mm:ss");

        return clientEvent.Type switch
        {
            ClientEventType.InstanceJoined => $"{when} — told them {who} joined {where}",
            ClientEventType.InstancePresenceObserved =>
                $"{when} — told them {who} was already in {where} when you arrived",
            ClientEventType.InstanceLeft => $"{when} — told them {who} left {where}",
            ClientEventType.LogStopped => $"{when} — told them VRChat's log stopped while you were in {where}",
            ClientEventType.AvatarChanged =>
                clientEvent.Data.TryGetValue("avatarName", out var avatar) && avatar.Length > 0
                    ? $"{when} — told them {who} switched to the avatar “{avatar}”"
                    : $"{when} — told them {who} changed avatar",
            _ => $"{when} — told them about {who} in {where}",
        };
    }

    private void Append(IEnumerable<JournalEntry> entries)
    {
        lock (_gate)
        {
            var added = 0;
            foreach (var entry in entries)
            {
                _entries.Add(entry);
                added++;
            }

            if (added == 0)
                return;

            if (_entries.Count > _capacity)
            {
                _entries.RemoveRange(0, _entries.Count - _capacity);
                Rewrite();
                return;
            }

            AppendToFile(_entries.TakeLast(added));
        }
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
}
