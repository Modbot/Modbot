using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.AI;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Settings → AI → Base: saved and read back, the key never returned, and nothing reachable
/// without the settings permission.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AiSettingsTests
{
    private const string Path = "/api/settings/ai";

    private readonly PostgresFixture _db;

    public AiSettingsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ANewDeploymentStartsWithAiOffOnTheDefaultPreset()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var body = await GetAsync(host, cookie);

        Assert.False(body.GetProperty("enabled").GetBoolean());
        Assert.Equal("openrouter", body.GetProperty("provider").GetString());
        Assert.False(body.GetProperty("apiKeyStored").GetBoolean());
        Assert.Equal(
            ["openrouter", "xai", "anthropic", "openai", "custom"],
            body.GetProperty("providers").EnumerateArray().Select(p => p.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task SavedSettingsReadBack_AndTheKeyIsNeverReturned()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        const string key = "sk-or-very-secret-key";

        await AcknowledgeAsync(host, cookie);

        var saved = await host.SendJsonAsync(HttpMethod.Put, Path, new
        {
            enabled = true,
            provider = "openrouter",
            endpoint = "https://openrouter.ai/api/v1",
            model = "openai/gpt-4o-mini",
            apiKey = key,
        }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.DoesNotContain(key, await saved.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var raw = await (await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct)).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(key, raw, StringComparison.Ordinal);

        var body = JsonDocument.Parse(raw).RootElement;
        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.Equal("openrouter", body.GetProperty("provider").GetString());
        Assert.Equal("https://openrouter.ai/api/v1", body.GetProperty("endpoint").GetString());
        Assert.Equal("openai/gpt-4o-mini", body.GetProperty("model").GetString());
        Assert.True(body.GetProperty("apiKeyStored").GetBoolean());

        // Stored encrypted, and the stored value decrypts to what was typed.
        await using var context = _db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        Assert.NotEqual(key, settings.AiApiKeyEncrypted);
        Assert.Equal(key, host.Services.GetRequiredService<ISecretProtector>().Unprotect(settings.AiApiKeyEncrypted));

        // And a feature asking for a client gets one for exactly these settings, without a restart.
        using var scope = host.Services.CreateScope();
        var chat = await scope.ServiceProvider.GetRequiredService<IAiClients>().GetChatAsync(Ct);
        Assert.NotNull(chat);
        Assert.Equal("openai/gpt-4o-mini", chat.Model);
        Assert.Equal("openrouter", chat.Provider);
    }

    [Fact]
    public async Task SavingWithoutAKeyKeepsTheStoredOne()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await AcknowledgeAsync(host, cookie);
        await PutOkAsync(host, cookie, new { enabled = true, provider = "openai", endpoint = "https://api.openai.com/v1", model = "gpt-a", apiKey = "sk-1" });
        var body = await PutOkAsync(host, cookie, new { enabled = true, provider = "openai", endpoint = "https://api.openai.com/v1/", model = "gpt-b" });

        Assert.True(body.GetProperty("apiKeyStored").GetBoolean());
        Assert.Equal("gpt-b", body.GetProperty("model").GetString());
    }

    /// <summary>A stored key only ever goes to the address it was typed in for.</summary>
    [Fact]
    public async Task ChangingTheEndpointWithoutANewKeyForgetsTheKey()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await PutOkAsync(host, cookie, new { enabled = false, provider = "openai", endpoint = "https://api.openai.com/v1", model = "gpt-a", apiKey = "sk-1" });
        var body = await PutOkAsync(host, cookie, new { enabled = false, provider = "custom", endpoint = "https://collector.example/v1", model = "gpt-a" });

        Assert.False(body.GetProperty("apiKeyStored").GetBoolean());
    }

    [Fact]
    public async Task TheKeyCanBeRemoved()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await PutOkAsync(host, cookie, new { enabled = false, provider = "xai", endpoint = "https://api.x.ai/v1", model = "grok", apiKey = "xai-1" });
        var body = await PutOkAsync(host, cookie, new { enabled = false, provider = "xai", endpoint = "https://api.x.ai/v1", model = "grok", removeApiKey = true });

        Assert.False(body.GetProperty("apiKeyStored").GetBoolean());
    }

    [Theory]
    [InlineData("openai", "http://api.openai.com/v1", "m", "k", "Use an https:// address, or choose Custom for a local server.")]
    [InlineData("openai", "https://api.openai.com/v1", "", "k", "Enter a model.")]
    [InlineData("openai", "https://api.openai.com/v1", "m", "", "Enter the API key.")]
    [InlineData("gemini", "https://example.com/v1", "m", "k", "Choose a provider.")]
    public async Task InvalidSettingsAreRefusedWithTheReason(string provider, string endpoint, string model, string apiKey, string error)
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await AcknowledgeAsync(host, cookie);

        var response = await host.SendJsonAsync(HttpMethod.Put, Path,
            new { enabled = true, provider, endpoint, model, apiKey }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
    }

    /// <summary>M8 4.2: a local model with no key is a complete setup.</summary>
    [Fact]
    public async Task ALocalModelWithNoKeyCanBeSwitchedOn()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await AcknowledgeAsync(host, cookie);
        var body = await PutOkAsync(host, cookie, new { enabled = true, provider = "custom", endpoint = "http://localhost:11434/v1", model = "llama3.2" });

        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.False(body.GetProperty("apiKeyStored").GetBoolean());
    }

    [Fact]
    public async Task WithAiOff_NoFeatureGetsAClient()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await PutOkAsync(host, cookie, new { enabled = false, provider = "openai", endpoint = "https://api.openai.com/v1", model = "gpt-a", apiKey = "sk-1" });

        using var scope = host.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IAiClients>().GetChatAsync(Ct));
    }

    [Theory]
    [InlineData("GET", "")]
    [InlineData("PUT", "")]
    [InlineData("POST", "/test")]
    [InlineData("POST", "/models")]
    public async Task WithoutTheSettingsPermission_EveryEndpointIsRefused(string method, string suffix)
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);

        var body = new { enabled = false, provider = "openai", endpoint = "https://api.openai.com/v1", model = "m" };

        var signedIn = await host.SendJsonAsync(new HttpMethod(method), Path + suffix, method == "GET" ? null : body, cookie, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, signedIn.StatusCode);

        var anonymous = await host.SendJsonAsync(new HttpMethod(method), Path + suffix, method == "GET" ? null : body, null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task TheTestButtonUsesTheStoredKeyForTheSameEndpoint_AndReportsTheReply()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var handler = new RecordingHandler(HttpStatusCode.OK,
            """{"id":"c1","object":"chat.completion","created":1700000000,"model":"gpt-a","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"OK"}}]}""");
        await using var host = await StartWithFakeProviderAsync(handler);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await PutOkAsync(host, cookie, new { enabled = false, provider = "openai", endpoint = "https://api.openai.com/v1", model = "gpt-a", apiKey = "sk-stored" });

        var response = await host.SendJsonAsync(HttpMethod.Post, Path + "/test",
            new { provider = "openai", endpoint = "https://api.openai.com/v1", model = "gpt-a" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiTestHost.BodyOf(response, Ct);
        Assert.True(body.GetProperty("worked").GetBoolean(), body.GetProperty("message").GetString());
        Assert.Equal("gpt-a answered: OK", body.GetProperty("message").GetString());

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.openai.com/v1/chat/completions", request.Url);
        Assert.Equal("Bearer sk-stored", request.Authorization);
    }

    /// <summary>Pointing the form at another address and pressing Test must not send the stored key there.</summary>
    [Fact]
    public async Task TheTestButtonNeverSendsTheStoredKeyToADifferentEndpoint()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized, """{"error":{"message":"No key"}}""");
        await using var host = await StartWithFakeProviderAsync(handler);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await PutOkAsync(host, cookie, new { enabled = false, provider = "openai", endpoint = "https://api.openai.com/v1", model = "gpt-a", apiKey = "sk-stored" });

        var response = await host.SendJsonAsync(HttpMethod.Post, Path + "/test",
            new { provider = "custom", endpoint = "https://collector.example/v1", model = "gpt-a" }, cookie, Ct);

        var body = await ApiTestHost.BodyOf(response, Ct);
        Assert.False(body.GetProperty("worked").GetBoolean());
        Assert.Equal("collector.example answered 401: No key", body.GetProperty("message").GetString());
        Assert.DoesNotContain("sk-stored", Assert.Single(handler.Requests).Authorization ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheModelListComesFromTheEndpoint()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"object":"list","data":[{"id":"grok-b"},{"id":"grok-a"}]}""");
        await using var host = await StartWithFakeProviderAsync(handler);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, Path + "/models",
            new { provider = "xai", endpoint = "https://api.x.ai/v1", apiKey = "xai-typed" }, cookie, Ct);

        var body = await ApiTestHost.BodyOf(response, Ct);
        Assert.Equal(["grok-a", "grok-b"], body.GetProperty("models").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal("https://api.x.ai/v1/models", Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task ATestWithAnInvalidEndpointIsRefusedWithoutARequest()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        await using var host = await StartWithFakeProviderAsync(handler);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, Path + "/test",
            new { provider = "anthropic", endpoint = "http://api.anthropic.com/v1/", model = "claude", apiKey = "k" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(handler.Requests);
    }

    /// <summary>The real host, with the AI HTTP client's handler swapped for the fake provider.</summary>
    private Task<ApiTestHost> StartWithFakeProviderAsync(RecordingHandler handler) =>
        ApiTestHost.StartAsync(_db, configure: services =>
            services.AddHttpClient(AiClients.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler));

    private async Task<JsonElement> GetAsync(ApiTestHost host, string cookie)
    {
        var response = await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.BodyOf(response, Ct);
    }

    /// <summary>
    /// M8 §4.5: the one-time confirmation of what member text goes to the provider, and the gate
    /// that stops AI being switched on before it.
    /// </summary>
    [Fact]
    public async Task AiCannotBeSwitchedOnUntilSomebodyConfirmsWhatIsSent_AndTheConfirmationIsRecordedOnce()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var before = await GetAsync(host, cookie);
        Assert.False(before.GetProperty("acknowledgement").GetProperty("confirmed").GetBoolean());
        Assert.NotEmpty(before.GetProperty("acknowledgement").GetProperty("sends").EnumerateArray());
        Assert.Contains(
            before.GetProperty("acknowledgement").GetProperty("sends").EnumerateArray(),
            line => line.GetProperty("feature").GetString() == "Moderation rules");

        var refused = await host.SendJsonAsync(HttpMethod.Put, Path,
            new { enabled = true, provider = "openai", endpoint = "https://api.openai.com/v1", model = "gpt-a", apiKey = "sk-1" },
            cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("Confirm what is sent to the provider first.",
            (await ApiTestHost.BodyOf(refused, Ct)).GetProperty("error").GetString());

        // AI off does not need it: nothing is sent.
        await PutOkAsync(host, cookie, new { enabled = false, provider = "openai", endpoint = "https://api.openai.com/v1", model = "gpt-a", apiKey = "sk-1" });

        var confirmed = await AcknowledgeAsync(host, cookie);
        Assert.True(confirmed.GetProperty("acknowledgement").GetProperty("confirmed").GetBoolean());
        Assert.Equal(user.Username, confirmed.GetProperty("acknowledgement").GetProperty("by").GetString());

        var on = await PutOkAsync(host, cookie, new { enabled = true, provider = "openai", endpoint = "https://api.openai.com/v1", model = "gpt-a", apiKey = "sk-1" });
        Assert.True(on.GetProperty("enabled").GetBoolean());

        // Confirming again is not an error and does not write a second fact: one confirmation, not
        // a recurring prompt.
        await AcknowledgeAsync(host, cookie);

        var fact = ApiTestHost.DataOf(Assert.Single(await host.FactsAsync(FactType.AiAcknowledged, user.Id.ToString(), Ct)));
        Assert.Equal(user.Username, fact.GetProperty("username").GetString());
        Assert.Equal("https://api.openai.com/v1", fact.GetProperty("endpoint").GetString());
        Assert.NotEmpty(fact.GetProperty("sends").EnumerateArray());
    }

    [Fact]
    public async Task ConfirmingNeedsTheSettingsPermission()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, reader) = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/acknowledge",
            new { endpoint = "https://api.openai.com/v1" }, reader, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<JsonElement> AcknowledgeAsync(ApiTestHost host, string cookie)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/acknowledge",
            new { endpoint = "https://api.openai.com/v1" }, cookie, Ct);

        var parsed = await ApiTestHost.BodyOf(response, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, parsed.ToString());
        return parsed;
    }

    private static async Task<JsonElement> PutOkAsync(ApiTestHost host, string cookie, object body)
    {
        var response = await host.SendJsonAsync(HttpMethod.Put, Path, body, cookie, Ct);
        var parsed = await ApiTestHost.BodyOf(response, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, parsed.ToString());
        return parsed;
    }
}

/// <summary>Stands in for the AI provider: one fixed answer, and a record of what was sent.</summary>
public sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public sealed record Seen(string Url, string? Authorization);

    public List<Seen> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new Seen(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString()));

        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}
