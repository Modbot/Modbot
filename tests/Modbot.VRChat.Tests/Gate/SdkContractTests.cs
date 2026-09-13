using System.Net;
using Modbot.Core;
using Modbot.VRChat.Session;
using Modbot.VRChat.Tests.Fakes;
using VRChat.API.Client;

namespace Modbot.VRChat.Tests.Gate;

/// <summary>
/// Pins the upstream behaviour this whole design rests on.
/// </summary>
/// <remarks>
/// <para>
/// Spec 4.1 states that <c>VRChat.API</c>'s <c>...WithHttpInfoAsync</c> methods catch
/// <c>ApiException</c> internally and return a non-success <c>ApiResponse</c> instead of throwing,
/// and the gate is written on that assumption: no exception translation anywhere, a status for
/// every outcome. If a future SDK version reverts it, every error path in the gate silently
/// becomes an unhandled exception — so it is asserted against a real client and a real socket
/// rather than trusted.
/// </para>
/// <para>
/// <strong>The client is built by hand here, and that is load-bearing.</strong>
/// <c>Configuration.BasePath</c> does not redirect a client: <c>ApiClient</c> captures its base
/// URL at construction, from <c>GlobalConfiguration.Instance</c>, and ignores the configuration
/// passed to each call. Setting <c>BasePath</c> on a built client looks like it works and quietly
/// sends the request to the real api.vrchat.cloud — which a test suite must never do. The only
/// way to point a client somewhere else is to construct <c>ApiClient</c> with the base path,
/// which is what happens below.
/// </para>
/// </remarks>
public class SdkContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A429ComesBackAsAResponseRatherThanAnException()
    {
        using var server = new StubHttpServer(_ => (429, """{"error":{"message":"slow down"}}"""));
        var client = NewClient(server);

        var response = await client.Groups.GetGroupWithHttpInfoAsync("grp_anything", cancellationToken: Ct);

        Assert.Equal(429, (int)response.StatusCode);
        Assert.Contains("slow down", response.RawContent, StringComparison.Ordinal);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task A403ComesBackAsAResponseRatherThanAnException()
    {
        using var server = new StubHttpServer(_ => (403, """{"error":{"message":"nope"}}"""));
        var client = NewClient(server);

        var response = await client.Groups.GetGroupWithHttpInfoAsync("grp_anything", cancellationToken: Ct);

        Assert.Equal(403, (int)response.StatusCode);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task AuthenticationReportsItsStatusInsteadOfCollapsingToNull()
    {
        using var server = new StubHttpServer(
            _ => (401, """{"error":{"message":"Invalid Username/Email or Password"}}"""));

        var client = NewClient(server);

        // The reason the gate drives authentication itself (spec 4.1.1): LoginAsync returns a
        // bare null here, which cannot distinguish this from a 429 or a WAF block, and the gate
        // has to answer all three differently.
        var response = await client.Authentication.GetCurrentUserWithHttpInfoAsync(Ct);

        Assert.Equal(401, (int)response.StatusCode);
    }

    [Fact]
    public async Task HeadersSurviveOnTheOrdinaryResponsePath()
    {
        using var server = new StubHttpServer(_ => (429, "{}"));
        var client = NewClient(server);

        var response = await client.Groups.GetGroupWithHttpInfoAsync("grp_anything", cancellationToken: Ct);

        // Spec 4.1 says headers are lost on error responses. That is true of the SDK's *catch*
        // path, which rebuilds the response with an empty collection; a 429 that arrives as an
        // ordinary HTTP response keeps them. Recorded because the difference decides where the
        // WAF classifier has to look -- which is why it reads both ErrorText and RawContent.
        Assert.Contains(response.Headers, header => header.Key == "X-Stub-Server");
    }

    [Fact]
    public void TheUserAgentNamesTheOperatorAndTheHeadersNameTheDeveloper()
    {
        var client = new VRChatClientFactory(
                new VRChatClientOptions(),
                new FixedOperatorContact("admin@example.com"))
            .Create(new VRChatConnection("user", "password"));

        // Being a legible API citizen is a design goal (spec 4.1). The operator -- whoever runs
        // this Modbot -- is the User-Agent contact; the project itself is named in two fixed
        // headers so VRChat can reach someone even when the operator cannot be. No request is
        // issued -- this client points at the real API, and a test must not send anything there.
        Assert.StartsWith(
            $"Modbot/{ModbotVersion.Release} (admin@example.com)",
            client.Configuration.UserAgent,
            StringComparison.Ordinal);
        Assert.Equal("me@bin.moe", client.Configuration.DefaultHeaders["X-Modbot-Developer-Contact-Email"]);
        Assert.Equal("https://github.com/binn/Modbot", client.Configuration.DefaultHeaders["X-Modbot-Developer-Contact-URL"]);
        Assert.DoesNotContain("X-Modbot-Contact-Email", client.Configuration.DefaultHeaders.Keys);
    }

    [Fact]
    public void WithoutAnOperatorAddressTheDeveloperIsTheUserAgentContact()
    {
        // Before onboarding has produced an administrator there is no operator email. The
        // User-Agent still has to name somebody: a request with nobody to contact is the kind
        // VRChat blocks first.
        var client = new VRChatClientFactory().Create(new VRChatConnection("user", "password"));

        Assert.StartsWith(
            $"Modbot/{ModbotVersion.Release} (me@bin.moe)",
            client.Configuration.UserAgent,
            StringComparison.Ordinal);
    }

    private sealed class FixedOperatorContact(string? email) : IOperatorContact
    {
        public string? Email => email;
    }

    /// <summary>
    /// A real SDK client aimed at the loopback stub. See the note on the class: the base path has
    /// to be given to <see cref="ApiClient"/> itself, because that is the only place it is read.
    /// </summary>
    private static IVRChat NewClient(StubHttpServer server)
    {
        var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
        var http = new HttpClient(handler);
        var apiClient = new ApiClient(http, server.BaseUrl, handler);

        var configuration = new Configuration
        {
            BasePath = server.BaseUrl,
            UserAgent = "ModbotTests/0.0 (tests), VRChat.API/tests",
        };

        return VRChatClient.Create(configuration, twoFactorSecret: string.Empty, apiClient, http, handler);
    }
}
