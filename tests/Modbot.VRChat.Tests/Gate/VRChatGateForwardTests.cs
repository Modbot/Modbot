using System.Net;
using System.Text;
using Modbot.VRChat.Proxy;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Session;
using Modbot.VRChat.Tests.Fakes;
using NSubstitute;
using VRChat.API.Client;

namespace Modbot.VRChat.Tests.Gate;

/// <summary>
/// The gate's one non-SDK call (VRChat proxy design): a forwarded request goes out on the session
/// client's own HttpClient, paced like everything else, and VRChat's answer comes back whatever
/// its status; a request on a caller's own cookie needs no session and sends nothing of Modbot's.
/// </summary>
public class VRChatGateForwardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VRChatEndpoint Proxy = new(VRChatEndpointClass.Proxy, null, "GET /api/1/users/usr_test");
    private static readonly VRChatEndpoint Passthrough = new(VRChatEndpointClass.ProxyPassthrough, null, "GET /api/1/auth/user");

    private static VRChatProxyRequest Request(params (string Name, string Value)[] headers) => new(
        "GET", "api/1/users/usr_test", "?n=1",
        [.. headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value))],
        null, null);

    [Fact]
    public async Task OnTheServiceAccount_TheRequestGoesOutOnTheSessionClient_AndTheAnswerComesBackAsItWas()
    {
        var handler = new RecordingHandler((HttpStatusCode.NotFound, """{"error":{"message":"no such user"}}"""));
        var vrchat = new FakeVRChat().SignedInAs();
        WireHttp(vrchat, handler);

        var store = new FakeConnectionStore(
            new VRChatConnection("modbot@example.com", "hunter2", AuthCookie: "storedCookie"));
        var gate = NewGate(vrchat, store, out _);

        var result = await gate.ForwardAsync(
            Proxy, Request(("Authorization", "Bearer mbk_secret"), ("Cookie", "auth=mbk_secret"), ("Accept", "application/json")),
            VRChatProxyAccount.Service, ct: Ct);

        // VRChat's 404 is the answer, not a failure.
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(404, result.Value!.StatusCode);
        Assert.Contains("no such user", Encoding.UTF8.GetString(result.Value.Body), StringComparison.Ordinal);

        var sent = Assert.Single(handler.Sent);
        Assert.Equal("https://api.vrchat.cloud/api/1/users/usr_test?n=1", sent.Url);
        Assert.Equal("Modbot/test test@example.com", sent.UserAgent);
        Assert.False(sent.HadAuthorization);
        Assert.False(sent.HadCookieHeader);
        Assert.Equal("application/json", sent.Accept);

        // The session was reused, not renewed, and the sign-in endpoint was never touched.
        Assert.Equal(0, vrchat.GetCurrentUserCalls);
    }

    [Fact]
    public async Task A429OnAForwardedRequestColdStopsTheProxy_AndNothingIsRetried()
    {
        var handler = new RecordingHandler((HttpStatusCode.TooManyRequests, """{"error":"slow down"}"""));
        var vrchat = new FakeVRChat().AlwaysSignedInAs();
        WireHttp(vrchat, handler);

        var store = new FakeConnectionStore(
            new VRChatConnection("modbot@example.com", "hunter2", AuthCookie: "storedCookie"));
        var gate = NewGate(vrchat, store, out var harness);

        var first = await gate.ForwardAsync(Proxy, Request(), VRChatProxyAccount.Service, ct: Ct);
        Assert.True(first.Success);
        Assert.Equal(429, first.Value!.StatusCode);

        var second = await gate.ForwardAsync(Proxy, Request(), VRChatProxyAccount.Service, ct: Ct);
        Assert.False(second.Success);
        Assert.Equal(VRChatFailureKind.RateLimited, second.Kind);
        Assert.True(second.WasNotSent);

        Assert.Single(handler.Sent);
        Assert.True((await harness.HealthAsync(VRChatEndpointClass.Proxy)).IsColdStopped);
    }

    [Fact]
    public async Task OnACallersOwnCookie_NoSessionIsNeeded_AndOnlyTheirCookieGoesOut()
    {
        using var server = new StubHttpServer(_ => (200, """{"id":"usr_theirs"}"""));

        // Nothing configured at all: a pass-through must work on a Modbot with no service account.
        var vrchat = new FakeVRChat();
        var store = new FakeConnectionStore(new VRChatConnection());
        var gate = NewGate(vrchat, store, out _, apiHost: new Uri(server.BaseUrl));

        var request = new VRChatProxyRequest(
            "GET", "api/1/auth/user", "",
            [
                new KeyValuePair<string, string>("Cookie", "auth=authcookie_theirs"),
                new KeyValuePair<string, string>("Authorization", "Bearer mbk_secret"),
            ],
            null, null);

        var result = await gate.ForwardAsync(Passthrough, request, VRChatProxyAccount.Caller, ct: Ct);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(200, result.Value!.StatusCode);
        Assert.Contains("usr_theirs", Encoding.UTF8.GetString(result.Value.Body), StringComparison.Ordinal);

        var head = Assert.Single(server.Heads);
        Assert.StartsWith("GET /api/1/auth/user ", server.Requests.Single(), StringComparison.Ordinal);
        Assert.Contains("Cookie: auth=authcookie_theirs", head, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", head, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("User-Agent: Modbot/", head, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, vrchat.GetCurrentUserCalls);
        Assert.Equal(VRChatSessionState.Unconfigured, gate.State);
    }

    /// <summary>Gives the substitute client a real HttpClient over a recording handler, and a configuration to read the host and User-Agent from.</summary>
    private static void WireHttp(FakeVRChat vrchat, RecordingHandler handler)
    {
        var configuration = new Configuration
        {
            BasePath = "https://api.vrchat.cloud/api/1",
            UserAgent = "Modbot/test test@example.com",
        };

        vrchat.Client.Configuration.Returns(configuration);
        vrchat.Client.HttpClient.Returns(new HttpClient(handler));
    }

    private static VRChatGate NewGate(
        FakeVRChat vrchat, FakeConnectionStore store, out LimiterHarness harness, Uri? apiHost = null)
    {
        harness = new LimiterHarness();

        return new VRChatGate(
            new FakeClientFactory(vrchat.Client), store, harness.Limiter, harness.Clock, new FakeMonotonicClock(),
            apiHost: apiHost);
    }

    /// <summary>Answers every request the same way and remembers what was asked.</summary>
    private sealed class RecordingHandler((HttpStatusCode Status, string Body) answer) : HttpMessageHandler
    {
        public List<(string Url, string? UserAgent, string? Accept, bool HadAuthorization, bool HadCookieHeader)> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add((
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues("User-Agent", out var agents) ? string.Join(" ", agents) : null,
                request.Headers.TryGetValues("Accept", out var accepts) ? string.Join(",", accepts) : null,
                request.Headers.Authorization is not null,
                request.Headers.Contains("Cookie")));

            return Task.FromResult(new HttpResponseMessage(answer.Status)
            {
                Content = new StringContent(answer.Body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
