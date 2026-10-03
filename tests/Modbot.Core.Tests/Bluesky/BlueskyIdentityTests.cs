using System.Net;
using System.Text.Json.Nodes;
using Modbot.Core.Bluesky;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Bluesky;

/// <summary>
/// Finding a Bluesky account from its handle (Bluesky design §3.1, fact 22): the handle is tidied
/// and checked, the DID document must name the account's server as https with no path, and the
/// handle must be named back.
/// </summary>
public class BlueskyIdentityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("ourgroup.bsky.social", "ourgroup.bsky.social")]
    [InlineData("@OurGroup.bsky.social", "ourgroup.bsky.social")]
    [InlineData("  ourgroup.com ", "ourgroup.com")]
    [InlineData("xn--bcher-kva.example.org", "xn--bcher-kva.example.org")]
    public void AHandleIsTidied(string typed, string kept)
    {
        Assert.Equal(kept, BlueskyIdentity.NormaliseHandle(typed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ourgroup")]
    [InlineData("our group.bsky.social")]
    [InlineData("-ourgroup.bsky.social")]
    [InlineData("ourgroup.bsky.social/evil")]
    [InlineData("ourgroup.local")]
    [InlineData("ourgroup.localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("ourgroup.123")]
    public void WhatIsNotAHandleIsRefused(string typed)
    {
        Assert.Null(BlueskyIdentity.NormaliseHandle(typed));
    }

    [Fact]
    public void TheDocumentNamesTheServerAndTheHandles()
    {
        var read = BlueskyIdentity.ReadDocument(FakeBluesky.Document().ToJsonString(), FakeBluesky.Did);

        Assert.NotNull(read);
        Assert.Equal(new Uri(FakeBluesky.Server), read!.Server);
        Assert.Equal([FakeBluesky.Handle], read.Handles);
    }

    [Theory]
    [InlineData("http://morel.us-east.host.bsky.network")]
    [InlineData("https://morel.us-east.host.bsky.network/xrpc")]
    [InlineData("https://morel.us-east.host.bsky.network/?a=b")]
    [InlineData("https://user:pass@morel.us-east.host.bsky.network")]
    [InlineData("https://localhost")]
    [InlineData("https://10.0.0.5")]
    [InlineData("not an address")]
    public void AServerThatIsNotPlainHttpsIsRefused(string endpoint)
    {
        var document = FakeBluesky.Document(server: endpoint);

        Assert.Null(BlueskyIdentity.ReadDocument(document.ToJsonString(), FakeBluesky.Did));
    }

    [Fact]
    public void ADocumentWithNoAccountServerIsRefused()
    {
        var document = FakeBluesky.Document();
        document["service"] = new JsonArray(new JsonObject
        {
            ["id"] = "#atproto_labeler",
            ["type"] = "AtprotoLabeler",
            ["serviceEndpoint"] = "https://labeler.example.com",
        });

        Assert.Null(BlueskyIdentity.ReadDocument(document.ToJsonString(), FakeBluesky.Did));
    }

    [Fact]
    public void ADocumentForAnotherDidIsRefused()
    {
        Assert.Null(BlueskyIdentity.ReadDocument(FakeBluesky.Document().ToJsonString(), "did:plc:someoneelse"));
    }

    [Fact]
    public async Task AHandleThatLeadsToItsAccountIsFound()
    {
        var bluesky = new FakeBluesky(() => DateTimeOffset.UnixEpoch);
        var identity = new BlueskyIdentity(new OneHandlerClients(bluesky));

        var found = await identity.FindAsync("@" + FakeBluesky.Handle, Ct);

        Assert.Null(found.Failure);
        Assert.Equal(FakeBluesky.Did, found.Value!.Did);
        Assert.Equal(new Uri(FakeBluesky.Server), found.Value.Server);
        Assert.Equal(["public.api.bsky.app", "plc.directory"], bluesky.Requests.Select(r => r.Uri.Host));
    }

    [Fact]
    public async Task AHandleTheAccountDoesNotNameBackIsNotTaken()
    {
        // The handle says it is this DID, but the DID's document names another handle.
        var bluesky = new TwoWayCheck();
        var identity = new BlueskyIdentity(new OneHandlerClients(bluesky));

        var found = await identity.FindAsync(FakeBluesky.Handle, Ct);

        Assert.Equal(BlueskyProblem.NotFound, found.Failure!.Problem);
    }

    /// <summary>A handle lookup that points at a DID whose document names someone else.</summary>
    private sealed class TwoWayCheck : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.Host switch
            {
                "public.api.bsky.app" => FakeBluesky.Json(HttpStatusCode.OK, new JsonObject { ["did"] = FakeBluesky.Did }),
                "plc.directory" => FakeBluesky.Json(HttpStatusCode.OK, FakeBluesky.Document(handle: "someone-else.bsky.social")),
                _ => FakeBluesky.Json(HttpStatusCode.NotFound, new JsonObject()),
            });
    }
}
