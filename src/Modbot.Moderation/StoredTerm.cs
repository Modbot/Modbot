using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modbot.Moderation;

/// <summary>How a term matches (AI moderation design §4.1).</summary>
public static class TermKind
{
    /// <summary>A word or phrase with a non-letter on both sides.</summary>
    public const string Word = "word";

    /// <summary>A word or phrase anywhere, including inside longer words.</summary>
    public const string Contains = "contains";

    /// <summary>A .NET regular expression, run with a match timeout.</summary>
    public const string Regex = "regex";

    /// <summary>Modbot Hub only: words near each other, unless an excuse phrase is present.</summary>
    public const string Combination = "combination";

    public static bool IsLocal(string? kind) => kind is Word or Contains or Regex;
}

/// <summary>
/// One term, as stored in a list's <c>terms</c> column and as the API shows it.
/// </summary>
/// <param name="Id">Stable within the list. A Hub rule's own id, so switching it off survives an update.</param>
/// <param name="Fields">
/// Hub only: the profile fields the rule is meant for. Null means all. A Discord message counts as
/// <c>bio</c> (design §3).
/// </param>
public sealed record StoredTerm(
    string Id,
    string Kind,
    string? Text = null,
    string? Pattern = null,
    IReadOnlyList<string>? AllOf = null,
    IReadOnlyList<string>? AnyOf = null,
    IReadOnlyList<string>? NoneOf = null,
    int? WithinWords = null,
    IReadOnlyList<string>? Fields = null,
    string? Category = null,
    string? Note = null,
    string? Severity = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>What a moderator sees for the term: the words, the pattern, or the combination.</summary>
    public string Label => Kind switch
    {
        TermKind.Regex => Pattern ?? string.Empty,
        TermKind.Combination => string.Join(" + ", new[]
            {
                AllOf is { Count: > 0 } all ? string.Join(" + ", all) : null,
                AnyOf is { Count: > 0 } any ? "(" + string.Join(" / ", any) + ")" : null,
            }.Where(p => p is not null)),
        _ => Text ?? string.Empty,
    };

    public static IReadOnlyList<StoredTerm> ParseList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<StoredTerm>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(IEnumerable<StoredTerm> terms) => JsonSerializer.Serialize(terms.ToList(), Json);

    public static IReadOnlyList<string> ParseIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
