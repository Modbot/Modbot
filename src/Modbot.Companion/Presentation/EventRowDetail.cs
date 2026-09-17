using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Companion.Journal;

namespace Modbot.Companion.Presentation;

/// <summary>
/// Everything one Events row holds, for the panel that opens under it: each field with a label,
/// and the row as JSON.
/// </summary>
/// <remarks>
/// The sentence in the row says what happened; this says everything the journal folded into it.
/// The JSON is the row as the window received it, not reworded, so what is on screen and what is
/// in <c>sent.jsonl</c> can be checked against each other.
/// </remarks>
public static class EventRowDetail
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The fields, in the order the panel lists them.</summary>
    public static IReadOnlyList<(string Label, string Value)> Fields(JournalRow row, IReadOnlyDictionary<string, string> groups)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(groups);

        var fields = new List<(string, string)>
        {
            ("When", row.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
            ("Server time", row.At.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'")),
            ("Kind", string.Join(", ", EventFilters.KindsOf(row))),
            ("Summary", row.Summary),
        };

        if (EventFilters.GroupOf(row, groups) is { } group)
            fields.Add(("Group", group));

        if (row.ServerId is { Length: > 0 } serverId)
            fields.Add(("Server", serverId));

        fields.Add(("Server state", row.ServerState is { } serverState ? Word(serverState) : "—"));
        fields.Add((SentJournal.CloudName, row.CloudState is { } cloudState ? Word(cloudState) : "—"));
        fields.Add(("Seen only", row.Seen ? "yes" : "no"));
        fields.Add(("Note", row.IsNote ? "yes" : "no"));

        return fields;
    }

    public static string ToJson(JournalRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return JsonSerializer.Serialize(row, Json);
    }

    private static string Word(JournalEntryKind kind) => kind.ToString().ToLowerInvariant();
}
