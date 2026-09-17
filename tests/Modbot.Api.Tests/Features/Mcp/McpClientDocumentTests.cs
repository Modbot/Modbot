using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Mcp;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Mcp;

/// <summary>
/// An app that identifies itself by a published document rather than by registering, which is
/// what Claude.ai does unless told otherwise: the document is read once, checked, kept, and the
/// sign-in goes through with the document's address as the client id.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class McpClientDocumentTests
{
    private const string Address = "https://claude.example.com/.well-known/oauth/client-id.json";
    private const string Redirect = "https://claude.example.com/api/mcp/auth_callback";

    private readonly PostgresFixture _db;

    public McpClientDocumentTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Document(string clientId = Address, string? method = "none", string? name = "Claude", params string[] redirects)
    {
        var uris = redirects.Length == 0 ? [Redirect] : redirects;
        var body = new Dictionary<string, object?>
        {
            ["client_id"] = clientId,
            ["client_name"] = name,
            ["redirect_uris"] = uris,
            ["client_uri"] = "https://claude.example.com",
        };
        if (method is not null)
            body["token_endpoint_auth_method"] = method;
        return JsonSerializer.SerializeToUtf8Bytes(body);
    }

    [Fact]
    public void ADocumentIsOnlyTakenWhenItDescribesTheClientAtItsOwnAddress()
    {
        var good = McpClientDocuments.Parse(Address, Document());
        Assert.NotNull(good);
        Assert.Equal("Claude", good.Name);
        Assert.Equal([Redirect], good.RedirectUris);
        Assert.Equal("https://claude.example.com/", good.ClientUri);

        Assert.Null(McpClientDocuments.Parse(Address, Document(clientId: "https://elsewhere.example.com/client.json")));
        Assert.Null(McpClientDocuments.Parse(Address, Document(method: "client_secret_basic")));
        Assert.Null(McpClientDocuments.Parse(Address, Document(redirects: "not a uri")));
        Assert.Null(McpClientDocuments.Parse(Address, "[]"u8));
        Assert.Null(McpClientDocuments.Parse(Address, "not json"u8));

        // No name: the host stands in. No method: public, as the specification says.
        var bare = McpClientDocuments.Parse(Address, Document(method: null, name: null));
        Assert.NotNull(bare);
        Assert.Equal("claude.example.com", bare.Name);
    }

    [Fact]
    public void OnlyAnHttpsAddressWithAHostCanBeAClientId()
    {
        Assert.True(McpClientDocuments.IsDocumentAddress(Address, out _));
        Assert.False(McpClientDocuments.IsDocumentAddress("http://claude.example.com/client.json", out _));
        Assert.False(McpClientDocuments.IsDocumentAddress(Address + "#part", out _));
        Assert.False(McpClientDocuments.IsDocumentAddress("https://user:pw@claude.example.com/client.json", out _));
        Assert.False(McpClientDocuments.IsDocumentAddress(Guid.NewGuid().ToString(), out _));
    }

    [Fact]
    public async Task AnAppWithAPublishedDocument_SignsIn_WithItsAddressAsTheClientId()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using (var db = _db.NewContext())
        {
            var settings = await db.GetSettingsAsync(Ct);
            settings.McpServerEnabled = true;
            await db.SaveChangesAsync(Ct);
        }

        var fetches = 0;
        await using var host = await ApiTestHost.StartAsync(_db, configure: services =>
            services.AddHttpClient(McpClientDocuments.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new DocumentHandler(Document(), () => fetches++)));
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.ViewLiveInstances, Ct);

        var (verifier, challenge) = Pkce();
        var query = $"?response_type=code&client_id={Uri.EscapeDataString(Address)}&redirect_uri={Uri.EscapeDataString(Redirect)}&code_challenge={challenge}&code_challenge_method=S256&state=xyz&scope=mcp&resource={Uri.EscapeDataString("https://localhost/mcp")}";

        var sent = await host.Client.GetAsync("/mcp/authorize" + query, Ct);
        Assert.Equal(HttpStatusCode.Redirect, sent.StatusCode);

        // The sign-in page shows the document's name, and the document was read once for it.
        var view = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/mcp/authorize" + query, null, cookie, Ct), Ct);
        Assert.Equal("Claude", view.GetProperty("clientName").GetString());

        var approved = await host.SendJsonAsync(HttpMethod.Post, "/api/mcp/authorize", new
        {
            clientId = Address,
            redirectUri = Redirect,
            state = "xyz",
            codeChallenge = challenge,
            codeChallengeMethod = "S256",
            scope = "mcp",
            resource = "https://localhost/mcp",
            approve = true,
        }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var to = new Uri((await ApiTestHost.BodyOf(approved, Ct)).GetProperty("redirectTo").GetString()!);
        Assert.StartsWith(Redirect, to.ToString(), StringComparison.Ordinal);
        var code = QueryHelpers.ParseQuery(to.Query)["code"].ToString();

        var exchanged = await host.Client.PostAsync("/mcp/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = Redirect,
            ["client_id"] = Address,
            ["code_verifier"] = verifier,
            ["resource"] = "https://localhost/mcp",
        }), Ct);
        Assert.Equal(HttpStatusCode.OK, exchanged.StatusCode);
        var tokens = await ApiTestHost.BodyOf(exchanged, Ct);
        Assert.StartsWith(McpSecrets.AccessPrefix, tokens.GetProperty("access_token").GetString(), StringComparison.Ordinal);

        // Read once and kept: the later calls used what was kept.
        Assert.Equal(1, fetches);
        await using (var db = _db.NewContext())
        {
            var client = await db.McpClients.AsNoTracking().SingleAsync(c => c.MetadataUrl == Address, Ct);
            Assert.Equal("Claude", client.Name);
            Assert.Null(client.SecretHash);
        }

        // A redirect the document does not name is refused in place, like a registered app's.
        var elsewhere = await host.Client.GetAsync($"/mcp/authorize?response_type=code&client_id={Uri.EscapeDataString(Address)}&redirect_uri={Uri.EscapeDataString("https://evil.example.com/cb")}&code_challenge={challenge}&code_challenge_method=S256", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);
    }

    [Fact]
    public async Task AnAddressWhoseDocumentCannotBeRead_IsAnUnknownClient()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using (var db = _db.NewContext())
        {
            var settings = await db.GetSettingsAsync(Ct);
            settings.McpServerEnabled = true;
            await db.SaveChangesAsync(Ct);
        }

        await using var host = await ApiTestHost.StartAsync(_db, configure: services =>
            services.AddHttpClient(McpClientDocuments.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new DocumentHandler(null, () => { })));

        // An address never seen before, so there is nothing kept to fall back on.
        const string unknown = "https://unknown.example.com/.well-known/oauth/client-id.json";
        var (_, challenge) = Pkce();
        var sent = await host.Client.GetAsync($"/mcp/authorize?response_type=code&client_id={Uri.EscapeDataString(unknown)}&redirect_uri={Uri.EscapeDataString(Redirect)}&code_challenge={challenge}&code_challenge_method=S256", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, sent.StatusCode);
        Assert.Contains("Unknown client", await sent.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        return (verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Serves the document at the client's address, or nothing at all.</summary>
    private sealed class DocumentHandler(byte[]? document, Action fetched) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            fetched();

            if (document is null || request.RequestUri?.AbsoluteUri != Address)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(document) { Headers = { ContentType = new("application/json") } },
            });
        }
    }
}
