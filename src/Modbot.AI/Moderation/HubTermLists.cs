using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Modbot.AI.Moderation;

/// <summary>Where Modbot Hub is. my.modbot.co unless a test or a private hub says otherwise.</summary>
public sealed class TermListHubOptions
{
    public const string DefaultAddress = "https://my.modbot.co/";

    public Uri Address { get; set; } = new(DefaultAddress);
}

/// <summary>One list on Modbot Hub's index.</summary>
public sealed record HubListSummary(
    string Id,
    string Name,
    string? Description,
    string? Version,
    int RuleCount,
    IReadOnlyList<string> SuitableFor);

/// <summary>A list as fetched and converted to terms, or why it could not be.</summary>
/// <param name="SkippedTopics">How many of its rules were AI topics, which a term list does not carry.</param>
public sealed record HubListFetch(
    string? Name,
    string? Version,
    IReadOnlyList<StoredTerm> Terms,
    int SkippedTopics,
    string? Error);

/// <summary>What a newer version of a list changes, counted by term id.</summary>
public sealed record HubListChanges(int Added, int Removed, int Changed)
{
    public bool Any => Added + Removed + Changed > 0;
}

/// <summary>
/// Reads term lists from Modbot Hub (<c>/termlists/index.json</c> and <c>/termlists/{id}.json</c>,
/// served by <c>Modbot.My</c>) and turns their rules into terms (AI moderation design §2, §9).
/// </summary>
public sealed partial class HubTermLists
{
    public const string HttpClientName = "Modbot.Hub";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly IHttpClientFactory _http;
    private readonly TermListHubOptions _options;

    public HubTermLists(IHttpClientFactory http, TermListHubOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        _http = http;
        _options = options;
    }

    /// <summary>The Hub's list ids are lower-case letters, digits and underscores. Anything else never reaches a URL.</summary>
    public static bool IsHubId(string? id) => id is { Length: > 0 and <= 100 } && HubIdPattern().IsMatch(id);

    public async Task<(IReadOnlyList<HubListSummary> Lists, string? Error)> IndexAsync(CancellationToken ct)
    {
        try
        {
            using var client = Client();
            var index = await client.GetFromJsonAsync<List<JsonObject>>("termlists/index.json", StoredTerm.Json, ct)
                .ConfigureAwait(false);

            var lists = (index ?? [])
                .Select(o => new HubListSummary(
                    Str(o, "id") ?? string.Empty,
                    Str(o, "name") ?? Str(o, "id") ?? string.Empty,
                    Str(o, "description"),
                    Str(o, "version"),
                    o["ruleCount"] is JsonValue count && count.TryGetValue(out int n) ? n : 0,
                    o["suitableFor"] is JsonArray suitable
                        ? suitable.Select(s => s?.GetValue<string>()).OfType<string>().ToList()
                        : []))
                .Where(l => IsHubId(l.Id))
                .ToList();

            return (lists, null);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return ([], Describe(e));
        }
    }

    public async Task<HubListFetch> FetchAsync(string hubId, CancellationToken ct)
    {
        if (!IsHubId(hubId))
            return new HubListFetch(null, null, [], 0, "That is not a Modbot Hub list id.");

        try
        {
            using var client = Client();
            using var response = await client.GetAsync($"termlists/{hubId}.json", ct).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new HubListFetch(null, null, [], 0, "Modbot Hub has no list with that id.");

            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Convert(body);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return new HubListFetch(null, null, [], 0, Describe(e));
        }
    }

    /// <summary>A Hub list's JSON as terms. Public so it can be tested against the lists in the repository.</summary>
    public static HubListFetch Convert(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return new HubListFetch(null, null, [], 0, "Modbot Hub answered with something that is not a term list.");
        }

        if (root?["rules"] is not JsonArray rules)
            return new HubListFetch(null, null, [], 0, "Modbot Hub answered with something that is not a term list.");

        var terms = new List<StoredTerm>();
        var topics = 0;
        var index = 0;

        foreach (var node in rules)
        {
            index++;

            if (node is not JsonObject rule)
                continue;

            var id = Str(rule, "id") is { Length: > 0 and <= 200 } given ? given : $"rule-{index}";
            var fields = Strings(rule, "fields");
            var note = Str(rule, "note");
            var category = Str(rule, "category");
            var severity = Str(rule, "severity");

            StoredTerm? term = Str(rule, "type") switch
            {
                "term" when Str(rule, "match") is { Length: > 0 } match => new StoredTerm(
                    id,
                    rule["wholeWord"] is JsonValue w && w.TryGetValue(out bool whole) && !whole ? TermKind.Contains : TermKind.Word,
                    Text: match),
                "pattern" when Str(rule, "regex") is { Length: > 0 } regex => new StoredTerm(id, TermKind.Regex, Pattern: regex),
                "combination" => new StoredTerm(
                    id,
                    TermKind.Combination,
                    AllOf: Strings(rule, "allOf"),
                    AnyOf: Strings(rule, "anyOf"),
                    NoneOf: Strings(rule, "noneOf"),
                    WithinWords: rule["withinWords"] is JsonValue within && within.TryGetValue(out int words) ? words : null),
                _ => null,
            };

            if (Str(rule, "type") == "topic")
                topics++;

            if (term is null)
                continue;

            terms.Add(term with { Fields = fields, Note = note, Category = category, Severity = severity });
        }

        return new HubListFetch(Str(root, "name"), Str(root, "version"), terms, topics, null);
    }

    public static HubListChanges Compare(IReadOnlyList<StoredTerm> before, IReadOnlyList<StoredTerm> after)
    {
        var old = before.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => JsonSerializer.Serialize(g.First(), StoredTerm.Json));
        var next = after.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => JsonSerializer.Serialize(g.First(), StoredTerm.Json));

        return new HubListChanges(
            next.Keys.Count(k => !old.ContainsKey(k)),
            old.Keys.Count(k => !next.ContainsKey(k)),
            next.Count(p => old.TryGetValue(p.Key, out var was) && was != p.Value));
    }

    private HttpClient Client()
    {
        var client = _http.CreateClient(HttpClientName);
        client.BaseAddress = _options.Address;
        client.Timeout = Timeout;
        return client;
    }

    private string Describe(Exception e) => e switch
    {
        HttpRequestException { StatusCode: { } status } => $"{_options.Address.Host} answered {(int)status}.",
        HttpRequestException => $"Could not reach {_options.Address.Host}.",
        TaskCanceledException => $"{_options.Address.Host} did not answer in time.",
        JsonException or NotSupportedException => $"{_options.Address.Host} answered with something that is not a term list.",
        _ => $"Could not read from {_options.Address.Host}: {e.Message}",
    };

    private static string? Str(JsonObject o, string name)
        => o[name] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static IReadOnlyList<string>? Strings(JsonObject o, string name)
        => o[name] is JsonArray a
            ? a.Select(x => x is JsonValue v && v.TryGetValue(out string? s) ? s : null).OfType<string>().ToList()
            : null;

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex HubIdPattern();
}
