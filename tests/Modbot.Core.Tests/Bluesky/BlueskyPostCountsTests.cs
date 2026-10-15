using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Modbot.Core.Bluesky;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Bluesky;

/// <summary>
/// The numbers under posts, read from <c>app.bsky.feed.getPosts</c> on Bluesky's public API: the three
/// numbers Modbot shows, a missing number as 0, a post without an address skipped, the call made to the
/// public API with no sign-in and no more than 25 posts, and a 429 coming back as a stop, never retried.
/// </summary>
public class BlueskyPostCountsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static BlueskyClient ClientFor(Handler handler) => new(new OneHandlerClients(handler), new FakeClock());

    [Fact]
    public void TheThreeNumbersAreRead_AndAMissingOneIsZero()
    {
        var body = JsonNode.Parse("""
            {"posts":[
              {"uri":"at://did:plc:a/app.bsky.feed.post/1","likeCount":12,"repostCount":3,"replyCount":5,"quoteCount":9},
              {"uri":"at://did:plc:a/app.bsky.feed.post/2"},
              {"likeCount":4}
            ]}
            """)!.AsObject();

        var counts = BlueskyClient.CountsOf(body);

        Assert.Equal(2, counts.Count);
        Assert.Equal(new BlueskyPostCounts("at://did:plc:a/app.bsky.feed.post/1", 12, 3, 5), counts[0]);
        Assert.Equal(new BlueskyPostCounts("at://did:plc:a/app.bsky.feed.post/2", 0, 0, 0), counts[1]);
    }

    [Fact]
    public void AnAnswerWithNoPostsIsNoCounts()
    {
        Assert.Empty(BlueskyClient.CountsOf(new JsonObject()));
        Assert.Empty(BlueskyClient.CountsOf(null));
    }

    [Fact]
    public async Task TheCallGoesToThePublicApiWithNoSignInAndTheAddressesEscaped()
    {
        var handler = new Handler(HttpStatusCode.OK, """{"posts":[{"uri":"at://did:plc:a/app.bsky.feed.post/1","likeCount":1}]}""");

        var result = await ClientFor(handler).PostCountsAsync(["at://did:plc:a/app.bsky.feed.post/1", "at://did:plc:a/app.bsky.feed.post/2"], Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("public.api.bsky.app", request.RequestUri!.Host);
        Assert.Equal("/xrpc/app.bsky.feed.getPosts", request.RequestUri.AbsolutePath);
        Assert.Null(request.Headers.Authorization);
        Assert.Equal(2, request.RequestUri.Query.Split("uris=").Length - 1);
        Assert.Contains("uris=at%3A%2F%2Fdid%3Aplc%3Aa%2Fapp.bsky.feed.post%2F1", request.RequestUri.Query);
        Assert.Single(result.Value!);
    }

    [Fact]
    public async Task NoPostsAreNoCall()
    {
        var handler = new Handler(HttpStatusCode.OK, "{}");

        var result = await ClientFor(handler).PostCountsAsync([], Ct);

        Assert.Empty(result.Value!);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MoreThanTwentyFivePostsAreRefused()
    {
        var uris = Enumerable.Range(0, BlueskyClient.MostCountedAtOnce + 1).Select(i => $"at://did:plc:a/app.bsky.feed.post/{i}").ToList();

        await Assert.ThrowsAsync<ArgumentException>(() => ClientFor(new Handler(HttpStatusCode.OK, "{}")).PostCountsAsync(uris, Ct));
    }

    [Fact]
    public async Task ARateLimitIsAStopAndIsAskedOnlyOnce()
    {
        var handler = new Handler(HttpStatusCode.TooManyRequests, """{"error":"RateLimitExceeded","message":"Rate Limit Exceeded"}""");

        var result = await ClientFor(handler).PostCountsAsync(["at://did:plc:a/app.bsky.feed.post/1"], Ct);

        Assert.True(result.Failure!.StopsTheLane);
        Assert.Single(handler.Requests);
    }
}
