using System.Text.Json.Nodes;
using Modbot.Companion.Journal;

namespace Modbot.Companion.Presentation;

/// <summary>How a chip's values are matched.</summary>
public enum EventFilterOperator
{
    /// <summary>Any of the values.</summary>
    Is,

    /// <summary>None of the values.</summary>
    IsNot,

    /// <summary>Text: the row's sentence contains the value.</summary>
    Contains,
}

/// <summary>A property the Events list can be filtered on.</summary>
/// <param name="Id">The word written into settings: <c>kind</c>, <c>state</c>, <c>group</c>, <c>text</c>.</param>
/// <param name="Label">The word on the chip.</param>
/// <param name="IsText">Typed rather than picked from a list.</param>
public sealed record EventFilterProperty(string Id, string Label, bool IsText)
{
    /// <summary>The operators this property takes, in the order a chip cycles through them.</summary>
    public IReadOnlyList<EventFilterOperator> Operators
        => IsText ? [EventFilterOperator.Contains] : [EventFilterOperator.Is, EventFilterOperator.IsNot];
}

/// <summary>One value a property can take, with how many rows would show it.</summary>
public sealed record EventFilterOption(string Value, string Label, int Count);

/// <summary>
/// One chip on the filter bar: a property, an operator and the values, which are "any of".
/// </summary>
/// <remarks>
/// Written as <c>property:operator:value,value</c>, each value percent-encoded on its own before
/// the join, so a comma inside a group name is <c>%2C</c> and never a separator. The same text
/// goes into <c>settings.json</c>, so a hand-edited file reads the way the bar does.
/// </remarks>
public sealed class EventFilterChip : IEquatable<EventFilterChip>
{
    public EventFilterChip(string property, EventFilterOperator @operator, IEnumerable<string> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        ArgumentNullException.ThrowIfNull(values);

        Property = property;
        Operator = @operator;
        Values = [.. values];
    }

    public string Property { get; }

    public EventFilterOperator Operator { get; }

    public IReadOnlyList<string> Values { get; }

    public EventFilterChip With(EventFilterOperator @operator) => new(Property, @operator, Values);

    public EventFilterChip With(IEnumerable<string> values) => new(Property, Operator, values);

    public string Encode()
        => $"{Property}:{OperatorWord(Operator)}:{string.Join(',', Values.Select(Uri.EscapeDataString))}";

    /// <summary>Reads a chip back, or null for text that is not one.</summary>
    public static EventFilterChip? Decode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var first = text.IndexOf(':');
        var second = first < 0 ? -1 : text.IndexOf(':', first + 1);
        if (first <= 0 || second < 0)
            return null;

        var property = text[..first];
        if (!TryOperator(text[(first + 1)..second], out var @operator))
            return null;

        var rest = text[(second + 1)..];
        var values = rest.Length == 0 ? [] : rest.Split(',').Select(Uri.UnescapeDataString);

        return new EventFilterChip(property, @operator, values);
    }

    /// <summary>The operator as it is written and as it is read: <c>is</c>, <c>is-not</c>, <c>contains</c>.</summary>
    public static string OperatorWord(EventFilterOperator @operator) => @operator switch
    {
        EventFilterOperator.IsNot => "is-not",
        EventFilterOperator.Contains => "contains",
        _ => "is",
    };

    /// <summary>The operator as a person reads it on a chip, given how many values follow.</summary>
    public static string OperatorWords(EventFilterOperator @operator, int valueCount) => @operator switch
    {
        EventFilterOperator.IsNot => valueCount > 1 ? "is none of" : "is not",
        EventFilterOperator.Contains => "contains",
        _ => valueCount > 1 ? "is any of" : "is",
    };

    private static bool TryOperator(string word, out EventFilterOperator @operator)
    {
        switch (word)
        {
            case "is":
                @operator = EventFilterOperator.Is;
                return true;
            case "is-not":
                @operator = EventFilterOperator.IsNot;
                return true;
            case "contains":
                @operator = EventFilterOperator.Contains;
                return true;
            default:
                @operator = default;
                return false;
        }
    }

    public bool Equals(EventFilterChip? other)
        => other is not null && string.Equals(Encode(), other.Encode(), StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as EventFilterChip);

    public override int GetHashCode() => Encode().GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Encode();
}

/// <summary>
/// The chips on the Events page, as one value: what is shown, and what is remembered in
/// <c>settings.json</c> under <c>eventsFilters</c>.
/// </summary>
/// <remarks>
/// One chip per property; chips combine with AND, the values inside one are "any of". Equal when
/// the same chips are held in the same order, so a settings record that carries one compares by
/// what it says rather than by which list it was given.
/// </remarks>
public sealed class EventFilterSet : IEquatable<EventFilterSet>
{
    public static EventFilterSet Empty { get; } = new([]);

    public EventFilterSet(IEnumerable<EventFilterChip> chips)
    {
        ArgumentNullException.ThrowIfNull(chips);
        Chips = [.. chips];
    }

    public IReadOnlyList<EventFilterChip> Chips { get; }

    public bool IsEmpty => Chips.Count == 0;

    /// <summary>The chip for one property, or null.</summary>
    public EventFilterChip? For(string property)
        => Chips.FirstOrDefault(c => string.Equals(c.Property, property, StringComparison.Ordinal));

    /// <summary>The set with this chip in place of any on the same property, or added at the end.</summary>
    public EventFilterSet Replace(EventFilterChip chip)
    {
        ArgumentNullException.ThrowIfNull(chip);

        return For(chip.Property) is null
            ? new EventFilterSet([.. Chips, chip])
            : new EventFilterSet(Chips.Select(c => c.Property == chip.Property ? chip : c));
    }

    public EventFilterSet Without(string property)
        => new(Chips.Where(c => !string.Equals(c.Property, property, StringComparison.Ordinal)));

    /// <summary>The set with its last chip taken away.</summary>
    public EventFilterSet WithoutLast() => new(Chips.Take(Math.Max(0, Chips.Count - 1)));

    /// <summary>The chips as the lines written to settings.</summary>
    public IReadOnlyList<string> Encode() => [.. Chips.Select(c => c.Encode())];

    /// <summary>
    /// Reads chips back, skipping lines that are not chips, chips on a property this version has
    /// no filter for, and later chips on a property already seen.
    /// </summary>
    /// <remarks>
    /// A property that has gone away — <c>destination</c> did, when the Events page stopped saying
    /// which places an event went to — leaves a line in somebody's <c>settings.json</c>. Dropping
    /// it here means the page opens with one filter fewer rather than falling over on a chip it can
    /// no longer draw.
    /// </remarks>
    public static EventFilterSet Parse(IEnumerable<string?>? lines)
    {
        if (lines is null)
            return Empty;

        var chips = new List<EventFilterChip>();
        foreach (var line in lines)
        {
            if (EventFilterChip.Decode(line) is { } chip
                && EventFilters.Knows(chip.Property)
                && chips.All(c => c.Property != chip.Property))
            {
                chips.Add(chip);
            }
        }

        return new EventFilterSet(chips);
    }

    /// <summary>The <c>eventsFilters</c> value: an array of chip lines, or null when there are none.</summary>
    public JsonArray? ToJson()
        => IsEmpty ? null : new JsonArray([.. Chips.Select(c => (JsonNode?)JsonValue.Create(c.Encode()))]);

    public static EventFilterSet FromJson(JsonArray? array)
        => array is null ? Empty : Parse(array.Select(node => node?.GetValue<string>()));

    public bool Equals(EventFilterSet? other)
        => other is not null && Chips.SequenceEqual(other.Chips);

    public override bool Equals(object? obj) => Equals(obj as EventFilterSet);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var chip in Chips)
            hash.Add(chip);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Filtering the Events list, the way the web app's lists filter (research 2026-09-16): add a
/// filter, pick a property, pick values; one chip per property, AND across chips, "any of" within
/// one. Done on the rows the window already holds, so nothing is read for it.
/// </summary>
/// <remarks>
/// <para>A row's kind is read from its sentence, because the journal writes the sentence and not
/// the kind, and the sentence is the one thing every line carries. <c>SentJournal.Sentence</c> is
/// the only place those words are written, and the test suite holds the two in step.</para>
/// <para>There is no filter for where an event went. There was one until 2026-09-19, and it named
/// the hidden backup as one of its two values, which is the one thing the backup must never do on
/// a screen. A row now has a single state — <see cref="JournalRow.State"/> — and the Group chip is
/// what answers "which of my groups is this about".</para>
/// </remarks>
public static class EventFilters
{
    public const string Kind = "kind";

    public const string State = "state";

    public const string Group = "group";

    public const string Text = "text";

    public static IReadOnlyList<EventFilterProperty> Properties { get; } =
    [
        new(Kind, "Kind", false),
        new(State, "State", false),
        new(Group, "Group", false),
        new(Text, "Text", true),
    ];

    /// <summary>Whether this version has a filter for that property.</summary>
    public static bool Knows(string property)
        => Properties.Any(p => string.Equals(p.Id, property, StringComparison.Ordinal));

    /// <summary>The kinds a row can be, in the order the picker lists them.</summary>
    public static IReadOnlyList<string> Kinds { get; } =
        ["joined", "already there", "left", "changed avatar", "log stopped", "seen", "note"];

    public static IReadOnlyList<string> States { get; } = ["sent", "waiting", "withheld", "failed"];

    public static EventFilterProperty PropertyFor(string id)
        => Properties.FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException($"No filter property is called \"{id}\".", nameof(id));

    /// <summary>
    /// What kinds a row is. A row is one thing that happened, and also <c>seen</c> when it went
    /// to no server, so "kind is seen" and "kind is not seen" both mean what they say.
    /// </summary>
    public static IReadOnlyList<string> KindsOf(JournalRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.IsNote)
            return ["note"];

        var kinds = new List<string>(2);
        var sentence = row.Summary;

        if (sentence.EndsWith(" joined your world", StringComparison.Ordinal))
            kinds.Add("joined");
        else if (sentence.EndsWith(" was already in your world when you arrived", StringComparison.Ordinal))
            kinds.Add("already there");
        else if (sentence.EndsWith(" left your world", StringComparison.Ordinal))
            kinds.Add("left");
        else if (sentence.EndsWith(" changed avatar", StringComparison.Ordinal)
                 || sentence.Contains(" switched to the avatar ", StringComparison.Ordinal))
            kinds.Add("changed avatar");
        else if (sentence.StartsWith("VRChat's log stopped", StringComparison.Ordinal))
            kinds.Add("log stopped");

        if (row.Seen)
            kinds.Add("seen");

        return kinds;
    }

    /// <summary>How far a row got: the one word the page shows for it, or nothing.</summary>
    public static IReadOnlyList<string> StatesOf(JournalRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return row.State is { } state and not (JournalEntryKind.Note or JournalEntryKind.Seen)
            ? [state.ToString().ToLowerInvariant()]
            : [];
    }

    /// <summary>The group a row's server manages, or its server id until the group is known, or null.</summary>
    public static string? GroupOf(JournalRow row, IReadOnlyDictionary<string, string> groups)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(groups);

        return row.ServerId is { Length: > 0 } id
            ? groups.TryGetValue(id, out var group) ? group : id
            : null;
    }

    /// <summary>The rows the chips let through, in the order given.</summary>
    public static IReadOnlyList<JournalRow> Apply(
        IEnumerable<JournalRow> rows,
        EventFilterSet filters,
        IReadOnlyDictionary<string, string> groups)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(groups);

        return [.. rows.Where(row => filters.Chips.All(chip => Matches(row, chip, groups)))];
    }

    /// <summary>Whether one row passes one chip.</summary>
    public static bool Matches(JournalRow row, EventFilterChip chip, IReadOnlyDictionary<string, string> groups)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(chip);
        ArgumentNullException.ThrowIfNull(groups);

        if (chip.Property == Text)
        {
            return chip.Values.Count == 0
                || chip.Values.Any(v => row.Summary.Contains(v, StringComparison.OrdinalIgnoreCase));
        }

        var had = ValuesOf(row, chip.Property, groups);
        var any = had.Any(v => chip.Values.Contains(v, StringComparer.Ordinal));

        return chip.Operator switch
        {
            EventFilterOperator.IsNot => !any,
            _ => chip.Values.Count == 0 || any,
        };
    }

    /// <summary>
    /// The values a property offers, with how many of the given rows carry each. Pass the rows
    /// the other chips let through, so the counts say what picking a value would leave.
    /// </summary>
    public static IReadOnlyList<EventFilterOption> Options(
        string property,
        IEnumerable<JournalRow> rows,
        IReadOnlyDictionary<string, string> groups)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(groups);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var value in ValuesOf(row, property, groups))
                counts[value] = counts.GetValueOrDefault(value) + 1;
        }

        IEnumerable<string> order = property switch
        {
            Kind => Kinds,
            State => States,
            Group => counts.Keys.Order(StringComparer.OrdinalIgnoreCase),
            _ => [],
        };

        return [.. order.Select(v => new EventFilterOption(v, v, counts.GetValueOrDefault(v)))];
    }

    private static IReadOnlyList<string> ValuesOf(JournalRow row, string property, IReadOnlyDictionary<string, string> groups)
        => property switch
        {
            Kind => KindsOf(row),
            State => StatesOf(row),
            Group => GroupOf(row, groups) is { } group ? [group] : [],
            _ => [],
        };
}
