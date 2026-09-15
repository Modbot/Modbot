using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Events;

/// <summary>
/// Which events a subscription or a webhook wants (API keys design §4.5): types, and optionally
/// subjects.
/// </summary>
/// <remarks>
/// A type is an exact fact type, a prefix ending in <c>.*</c>, or <c>*</c> for everything. Subject
/// ids are opaque and never checked for shape (foundation §3.1.1) -- only for length, so a filter
/// cannot be made the size of a file. What the caller may see is decided separately, by
/// <see cref="EventVisibility"/>; a filter only ever narrows.
/// </remarks>
public sealed class EventFilter
{
    public const int MaxTypes = 100;
    public const int MaxSubjects = 500;
    public const int MaxSubjectLength = 256;

    private readonly HashSet<string> _exact;
    private readonly string[] _prefixes;
    private readonly bool _everything;
    private readonly HashSet<string> _subjects;

    private EventFilter(IReadOnlyList<string> types, IReadOnlyList<string> subjects)
    {
        Types = types;
        Subjects = subjects;
        _everything = types.Contains("*");
        _exact = types.Where(t => !t.EndsWith(".*", StringComparison.Ordinal) && t != "*").ToHashSet(StringComparer.Ordinal);
        _prefixes = types.Where(t => t.EndsWith(".*", StringComparison.Ordinal)).Select(t => t[..^1]).ToArray();
        _subjects = subjects.ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<string> Types { get; }

    /// <summary>Empty means every subject.</summary>
    public IReadOnlyList<string> Subjects { get; }

    public static EventFilter Everything { get; } = new(["*"], []);

    /// <param name="types">Null or empty means <c>*</c>.</param>
    public static bool TryCreate(
        IEnumerable<string?>? types,
        IEnumerable<string?>? subjects,
        out EventFilter filter,
        out string? error)
    {
        filter = Everything;
        error = null;

        var typeList = (types ?? [])
            .Select(t => t?.Trim().ToLowerInvariant() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (typeList.Count == 0)
            typeList.Add("*");

        if (typeList.Count > MaxTypes)
        {
            error = $"Choose at most {MaxTypes} event types.";
            return false;
        }

        foreach (var type in typeList)
        {
            if (!IsTypePattern(type))
            {
                error = $"'{type}' is not an event type.";
                return false;
            }
        }

        var subjectList = (subjects ?? [])
            .Select(s => s?.Trim() ?? string.Empty)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (subjectList.Count > MaxSubjects)
        {
            error = $"Choose at most {MaxSubjects} subjects.";
            return false;
        }

        if (subjectList.Any(s => s.Length > MaxSubjectLength))
        {
            error = $"A subject id is longer than {MaxSubjectLength} characters.";
            return false;
        }

        filter = new EventFilter(typeList, subjectList);
        return true;
    }

    public static bool IsTypePattern(string? type)
    {
        if (string.IsNullOrEmpty(type))
            return false;

        if (type == "*")
            return true;

        // "vrchat.*" names a one-segment prefix, which is not itself a well-formed type; checking
        // it with a stand-in last segment accepts exactly the prefixes of well-formed types.
        if (type.EndsWith(".*", StringComparison.Ordinal))
            return FactType.IsWellFormed(type[..^2] + ".x");

        return FactType.IsWellFormed(type);
    }

    public bool Matches(string type, string subjectId)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (_subjects.Count > 0 && !_subjects.Contains(subjectId))
            return false;

        return _everything
            || _exact.Contains(type)
            || _prefixes.Any(p => type.StartsWith(p, StringComparison.Ordinal));
    }

    public bool Matches(ModbotEvent fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return Matches(fact.Type, fact.SubjectId);
    }
}
