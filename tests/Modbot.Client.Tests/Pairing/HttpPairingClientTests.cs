using System.Net;
using System.Text;
using Modbot.Client.Pairing;

namespace Modbot.Client.Tests.Pairing;

/// <summary>
/// Pairing is the moment a moderator decides whether to trust this program, so every way it can
/// fail has to arrive as something they can read and act on — never as a retry in the background.
/// </summary>
public class HttpPairingClientTests
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));

            return _respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string GoodVersion = """{"version":"2026.9.0","apiVersion":1,"apiVersionMinimum":1}""";

    private const string GoodPair =
        """{"deviceToken":"dev_abcdef","managedGroupId":"grp_cats","serverTime":"2026-09-12T20:14:07.412+00:00"}""";

    private static async Task<(PairingResult Result, ScriptedHandler Handler)> PairAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string address = "https://modbot.example",
        ApiVersionRange? supported = null)
    {
        var handler = new ScriptedHandler(respond);
        using var http = new HttpClient(handler);
        var client = new HttpPairingClient(http, supported);

        var result = await client.PairAsync(
            new PairingAttempt(new Uri(address), "AB12-CD34", "cats"),
            TestContext.Current.CancellationToken);

        return (result, handler);
    }

    private static HttpResponseMessage Route(HttpRequestMessage request, HttpResponseMessage pairResponse)
        => request.RequestUri!.AbsolutePath == "/api/version"
            ? Json(HttpStatusCode.OK, GoodVersion)
            : pairResponse;

    [Fact]
    public async Task ExchangesACodeForATokenAndTheManagedGroup()
    {
        var (result, handler) = await PairAsync(r => Route(r, Json(HttpStatusCode.OK, GoodPair)));

        Assert.Equal(PairingOutcome.Paired, result.Outcome);
        Assert.Equal("dev_abcdef", result.Pairing!.DeviceToken);

        // The group comes back at pairing precisely so routing can be decided on this machine.
        // Asking a server "do you own this instance?" is itself the leak routing exists to stop.
        Assert.Equal("grp_cats", result.Pairing.ManagedGroupId);
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 20, 14, 7, 412, TimeSpan.Zero), result.ServerTime);
        Assert.Equal("https://modbot.example/api/v1/client/pair", handler.Requests[^1].RequestUri!.ToString());
    }

    [Fact]
    public async Task SendsTheCodeTheVersionAndThePlatformAndNothingElse()
    {
        // The whole outbound disclosure of pairing, asserted field by field. Anything added here
        // later is a change to what the client tells a server about the machine it runs on. There
        // is no device name: a label for the moderator's own machine told the operator nothing
        // they could act on, and was one more thing about a person to hold.
        var (_, handler) = await PairAsync(r => Route(r, Json(HttpStatusCode.OK, GoodPair)));

        using var document = System.Text.Json.JsonDocument.Parse(handler.Bodies[^1]);
        var fields = document.RootElement.EnumerateObject().Select(p => p.Name).Order().ToList();

        Assert.Equal(["clientVersion", "code", "platform"], fields);
        Assert.Equal("AB12-CD34", document.RootElement.GetProperty("code").GetString());
        Assert.Equal("windows", document.RootElement.GetProperty("platform").GetString());
    }

    [Fact]
    public async Task RefusesPlainHttpWithoutMakingARequestAtAll()
    {
        var (result, handler) = await PairAsync(
            _ => Json(HttpStatusCode.OK, GoodVersion),
            address: "http://modbot.example");

        Assert.Equal(PairingOutcome.NotAModbotServer, result.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PlainHttpToThisMachineIsAllowedSoATesterCanPairLocally()
    {
        // Loopback never crosses a network. The rule is "no clear text over a network", not "no
        // clear text", and a developer's http://localhost is the one place the two differ.
        var (result, handler) = await PairAsync(
            r => Route(r, Json(HttpStatusCode.OK, GoodPair)),
            address: "http://localhost:8080");

        Assert.Equal(PairingOutcome.Paired, result.Outcome);
        Assert.Equal("http://localhost:8080/api/v1/client/pair", handler.Requests[^1].RequestUri!.ToString());
    }

    [Fact]
    public async Task A401IsTerminalAndIsNotRetried()
    {
        // A revoked moderator's client must stop, and be seen to stop. One attempt, one answer,
        // and the outcome is carried back for the UI to show rather than swallowed.
        var (result, handler) = await PairAsync(
            r => Route(r, Json(HttpStatusCode.Unauthorized, """{"code":"token_revoked"}""")));

        Assert.Equal(PairingOutcome.Unauthorised, result.Outcome);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("token_revoked", result.Detail);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task ARefusedCodeIsItsOwnOutcome(HttpStatusCode status)
    {
        var (result, _) = await PairAsync(r => Route(r, Json(status, """{"code":"pairing_code_invalid"}""")));

        Assert.Equal(PairingOutcome.CodeRejected, result.Outcome);
    }

    [Fact]
    public async Task NamesBothVersionsWhenTheRangesDoNotOverlap()
    {
        // A version mismatch must never present as a parse error or as reporting quietly stopping:
        // those are indistinguishable from the log parser breaking, and equally unrecoverable.
        var (result, handler) = await PairAsync(
            _ => Json(HttpStatusCode.OK, """{"version":"2027.4.0","apiVersion":6,"apiVersionMinimum":4}"""),
            supported: new ApiVersionRange(1, 2));

        Assert.Equal(PairingOutcome.VersionUnsupported, result.Outcome);
        Assert.Contains("v1–v2", result.Detail);
        Assert.Contains("v4–v6", result.Detail);
        Assert.Contains("Update the client", result.Detail);

        // Negotiation failed, so the code was never offered to a server that could not use it.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SaysWhoHasToUpdateWhenTheServerIsTheOldOne()
    {
        var (result, _) = await PairAsync(
            _ => Json(HttpStatusCode.OK, """{"version":"2025.1.0","apiVersion":1,"apiVersionMinimum":1}"""),
            supported: new ApiVersionRange(4, 6));

        Assert.Equal(PairingOutcome.VersionUnsupported, result.Outcome);
        Assert.Contains("operator must update Modbot", result.Detail);
    }

    [Fact]
    public async Task PairsAtTheHighestVersionBothSpeakRatherThanTheNewestThatExists()
    {
        // The server speaks v2..v9 and this client speaks v1..v4, so v4 is the answer -- "newest
        // compatible", never "newest".
        var (result, handler) = await PairAsync(
            r => r.RequestUri!.AbsolutePath == "/api/version"
                ? Json(HttpStatusCode.OK, """{"version":"2026.9.0","apiVersion":9,"apiVersionMinimum":2}""")
                : Json(HttpStatusCode.OK, GoodPair),
            supported: new ApiVersionRange(1, 4));

        Assert.Equal(PairingOutcome.Paired, result.Outcome);
        Assert.Equal(4, result.Pairing!.ApiVersion);
        Assert.Equal("https://modbot.example/api/v4/client/pair", handler.Requests[^1].RequestUri!.ToString());
    }

    [Fact]
    public async Task AnAddressThatIsNotAModbotServerSaysSoRatherThanLookingLikeAnOutage()
    {
        var (result, _) = await PairAsync(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>hello</html>", Encoding.UTF8, "text/html"),
        });

        Assert.Equal(PairingOutcome.NotAModbotServer, result.Outcome);
    }

    [Fact]
    public async Task ASuccessfulCodeWithNoTokenIsARefusalRatherThanAHalfPairing()
    {
        var (result, _) = await PairAsync(r => Route(r, Json(HttpStatusCode.OK, """{"managedGroupId":"grp_cats"}""")));

        Assert.Equal(PairingOutcome.NotAModbotServer, result.Outcome);
        Assert.Null(result.Pairing);
    }

    [Fact]
    public async Task AnUnreachableServerIsANetworkFailureAndNotARejection()
    {
        var (result, _) = await PairAsync(_ => throw new HttpRequestException("no route to host"));

        Assert.Equal(PairingOutcome.NetworkFailure, result.Outcome);
    }
}
