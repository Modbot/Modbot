using System.Net;
using System.Text;
using Modbot.Moderation;
using Modbot.Core.Moderation;

namespace Modbot.Moderation.Tests;

/// <summary>
/// The curated term lists, read from the copies in this repository (<c>src/Modbot.Cloud/termlists</c>) and
/// through a scripted HTTP handler. Nothing reaches cloud.modbot.co.
/// </summary>
public class HubTermListsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ListsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Modbot.Cloud", "termlists")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "src", "Modbot.Cloud", "termlists");
    }

    public static TheoryData<string> Lists()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(ListsDirectory(), "modbot_*.json").Order())
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(Lists))]
    public void EveryPublishedListConverts_AndEveryPatternInItCompiles(string file)
    {
        var fetched = HubTermLists.Convert(File.ReadAllText(Path.Combine(ListsDirectory(), file)));

        Assert.Null(fetched.Error);
        Assert.False(string.IsNullOrEmpty(fetched.Version));
        Assert.Equal(fetched.Terms.Count, fetched.Terms.Select(t => t.Id).Distinct().Count());

        foreach (var term in fetched.Terms.Where(t => t.Kind == TermKind.Regex))
            Assert.True(TermMatcher.CompilePattern(term.Pattern!, out var error) is not null, $"{term.Id}: {error}");

        Assert.Equal(fetched.Terms.Count, TermMatcher.Compile(fetched.Terms).Count);
    }

    [Fact]
    public void TheAiTopicsListHasNoTerms()
    {
        var fetched = HubTermLists.Convert(File.ReadAllText(Path.Combine(ListsDirectory(), "modbot_ai_topics.json")));

        Assert.Empty(fetched.Terms);
        Assert.True(fetched.SkippedTopics > 0);
    }

    [Fact]
    public void APublishedRuleMatchesWhatItIsFor()
    {
        var fetched = HubTermLists.Convert(File.ReadAllText(Path.Combine(ListsDirectory(), "modbot_harassment_terms.json")));
        var list = TermMatcher.Compile(fetched.Terms);

        var hits = TermMatcher.Check(list, "just kys already", ModerationTargets.DiscordMessage);

        Assert.Contains(hits, h => h.TermKey == "harass_kys");
    }

    [Fact]
    public void ComparingVersionsCountsAddedRemovedAndChangedTerms()
    {
        StoredTerm[] before = [new("a", TermKind.Word, Text: "one"), new("b", TermKind.Word, Text: "two")];
        StoredTerm[] after = [new("a", TermKind.Word, Text: "uno"), new("c", TermKind.Word, Text: "three")];

        Assert.Equal(new HubListChanges(1, 1, 1), HubTermLists.Compare(before, after));
        Assert.False(HubTermLists.Compare(before, before).Any);
    }

    [Fact]
    public async Task AListIsFetchedFromTheHubByItsId_AndAnIdThatIsNotOneNeverReachesAUrl()
    {
        var handler = new ScriptedHandler(r => r.RequestUri!.AbsolutePath == "/termlists/sample_list.json"
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"sample_list","name":"Sample","version":"2026.9.1","rules":[{"id":"r1","type":"term","category":"x","match":"spam"}]}""",
                    Encoding.UTF8, "application/json"),
            }
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        var hub = new HubTermLists(new SingleClientFactory(handler), new TermListHubOptions { Address = new Uri("https://hub.example/") });

        var fetched = await hub.FetchAsync("sample_list", Ct);
        Assert.Null(fetched.Error);
        Assert.Equal("2026.9.1", fetched.Version);
        Assert.Equal("r1", Assert.Single(fetched.Terms).Id);

        var refused = await hub.FetchAsync("../settings", Ct);
        Assert.NotNull(refused.Error);
        Assert.Single(handler.Requests);
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

/// <summary>An HTTP handler that answers from a script and remembers what it was asked.</summary>
public sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    public sealed record Seen(HttpMethod Method, string Url, string? Authorization, string? Body);

    public List<Seen> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add(new Seen(
            request.Method,
            request.RequestUri!.ToString(),
            request.Headers.Authorization?.ToString(),
            body));

        return answer(request);
    }
}
