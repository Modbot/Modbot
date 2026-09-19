using System.Text.Json;
using Modbot.Companion.Journal;

namespace Modbot.Companion.Presentation;

/// <summary>
/// Everything one Events row shows, for the panel that opens under it: each field with a label,
/// and the same thing as JSON.
/// </summary>
/// <remarks>
/// The sentence in the row says what happened; this says everything else the row shows, field by
/// field and then as one object, so a person can copy it. It is the row as the page has it, which
/// is one state per event rather than one per place — the whole append-only record, every line the
/// backup wrote included, is in <c>sent.jsonl</c> in Modbot's own folder.
/// </remarks>
public static class EventRowDetail
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
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

        fields.Add(("State", State(row)));
        fields.Add(("Seen only", row.Seen ? "yes" : "no"));
        fields.Add(("Note", row.IsNote ? "yes" : "no"));

        return fields;
    }

    public static string ToJson(JournalRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return JsonSerializer.Serialize(
            new Shown(row.At, row.Summary, row.ServerId, row.State?.ToString().ToLowerInvariant(), row.Seen, row.IsNote),
            Json);
    }

    private static string State(JournalRow row) => row.State is { } state ? state.ToString().ToLowerInvariant() : "—";

    /// <summary>The row as the page has it, which is what the JSON box shows.</summary>
    private sealed record Shown(
        DateTimeOffset At,
        string Summary,
        string? ServerId,
        string? State,
        bool Seen,
        bool IsNote);
}
