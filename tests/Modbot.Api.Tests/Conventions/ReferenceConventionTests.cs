using System.Text.Json.Nodes;
using Modbot.Api.Conventions;

namespace Modbot.Api.Tests.Conventions;

/// <summary>
/// The committed API reference (docs/openapi/modbot.json) follows the conventions the server
/// keeps (API conventions design): every error has a body, numbers are numbers, parameters have
/// their common names, and a route kept for older clients says what replaced it.
/// </summary>
public class ReferenceConventionTests
{
    private static readonly Lazy<JsonNode> Document = new(() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "openapi", "modbot.json")))!);

    private static IEnumerable<(string Path, string Method, JsonObject Operation)> Operations()
    {
        foreach (var (path, item) in Document.Value["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                if (operation is JsonObject o && o.ContainsKey("responses"))
                    yield return (path, method, o);
            }
        }
    }

    [Fact]
    public void EveryErrorResponse_HasABody()
    {
        var bare = Operations()
            .SelectMany(op => op.Operation["responses"]!.AsObject()
                .Where(r => r.Key[0] is '4' or '5' && r.Value?["content"] is null)
                .Select(r => $"{op.Method.ToUpperInvariant()} {op.Path} {r.Key}"))
            .ToList();

        Assert.Empty(bare);
    }

    [Fact]
    public void NoNumber_IsAlsoText()
    {
        var mixed = new List<string>();

        void Walk(JsonNode? node, string where)
        {
            switch (node)
            {
                case JsonObject o:
                    if (o["type"] is JsonArray types)
                    {
                        var names = types.Select(t => t?.GetValue<string>()).ToList();
                        if (names.Contains("string") && (names.Contains("integer") || names.Contains("number")))
                            mixed.Add(where);
                    }

                    foreach (var (key, child) in o)
                        Walk(child, where + "/" + key);
                    break;

                case JsonArray a:
                    for (var i = 0; i < a.Count; i++)
                        Walk(a[i], where + "/" + i);
                    break;
            }
        }

        Walk(Document.Value, "#");

        Assert.Empty(mixed);
    }

    [Fact]
    public void AliasedParameters_GoByTheirCommonName()
    {
        foreach (var (path, _, operation) in Operations())
        {
            var names = operation["parameters"]?.AsArray().Select(p => p!["name"]!.GetValue<string>()).ToList() ?? [];

            foreach (var (_, own) in QueryAliases.For(path))
                Assert.DoesNotContain(own, names);
        }
    }

    [Fact]
    public void ARouteKeptForOlderClients_NamesItsReplacement()
    {
        var deprecated = Operations().Where(op => op.Operation["deprecated"]?.GetValue<bool>() == true).ToList();

        Assert.NotEmpty(deprecated);
        Assert.All(deprecated, op => Assert.Contains("use `", op.Operation["description"]!.GetValue<string>(), StringComparison.Ordinal));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Modbot.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
