using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Security;

namespace Modbot.AI.Tests;

/// <summary>
/// The Test button and the model list, against a scripted HTTP handler. Nothing here reaches a
/// real provider.
/// </summary>
public class AiClientsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("https://openrouter.ai/api/v1", "https://openrouter.ai/api/v1/chat/completions")]
    [InlineData("https://api.anthropic.com/v1/", "https://api.anthropic.com/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1/chat/completions")]
    public async Task ATestSendsOneChatCompletionToTheEndpoint(string endpoint, string expectedUrl)
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.OK, Completion("gpt-test", "OK")));
        var clients = Clients(handler);

        var result = await clients.TestAsync(new AiConnection("custom", new Uri(endpoint), "sk-test", "gpt-test"), Ct);

        Assert.True(result.Worked, result.Message);
        Assert.Equal("gpt-test answered: OK", result.Message);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(expectedUrl, request.Url);
        Assert.Equal("Bearer sk-test", request.Authorization);

        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("gpt-test", body.RootElement.GetProperty("model").GetString());
        Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public async Task TheProvidersOwnErrorMessageIsShown_AndNotRetried()
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.Unauthorized,
            """{"error":{"message":"Incorrect API key provided.","type":"invalid_request_error","code":"invalid_api_key"}}"""));

        var result = await Clients(handler).TestAsync(
            new AiConnection("openai", new Uri("https://api.openai.com/v1"), "wrong", "gpt-test"), Ct);

        Assert.False(result.Worked);
        Assert.Equal("api.openai.com answered 401: Incorrect API key provided.", result.Message);
        Assert.Single(handler.Requests);
    }

    /// <summary>A 429 is the answer somebody pressing Test needs to see, not something to wait out.</summary>
    [Fact]
    public async Task ARateLimitIsReportedAfterOneAttempt()
    {
        var handler = new ScriptedHandler(_ => Json((HttpStatusCode)429, """{"error":{"message":"Rate limit exceeded"}}"""));

        var result = await Clients(handler).TestAsync(
            new AiConnection("openrouter", new Uri("https://openrouter.ai/api/v1"), "k", "m"), Ct);

        Assert.False(result.Worked);
        Assert.Equal("openrouter.ai answered 429: Rate limit exceeded", result.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AServerThatCannotBeReachedSaysSo()
    {
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("Connection refused"));

        var result = await Clients(handler).TestAsync(
            new AiConnection("custom", new Uri("http://localhost:11434/v1"), null, "llama3.2"), Ct);

        Assert.False(result.Worked);
        Assert.StartsWith("Could not reach localhost", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerThatIsNotAChatCompletionIsNotCalledSuccess()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>login page</html>", Encoding.UTF8, "text/html"),
        });

        var result = await Clients(handler).TestAsync(
            new AiConnection("custom", new Uri("https://llm.example.org/v1"), "k", "m"), Ct);

        Assert.False(result.Worked);
    }

    [Fact]
    public async Task WithNoKey_ALocalServerStillGetsARequest()
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.OK, Completion("llama3.2", "OK")));

        var result = await Clients(handler).TestAsync(
            new AiConnection("custom", new Uri("http://localhost:11434/v1"), null, "llama3.2"), Ct);

        Assert.True(result.Worked, result.Message);
        Assert.Equal("Bearer " + AiClients.NoKey, Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task TheModelListIsReadFromDataIds_SortedAndWithoutDuplicates()
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.OK,
            """{"object":"list","data":[{"id":"b-model","object":"model"},{"id":"A-model"},{"id":"b-model"},{"name":"no id"}]}"""));

        var list = await Clients(handler).ListModelsAsync(
            new AiConnection("openrouter", new Uri("https://openrouter.ai/api/v1"), "k", ""), Ct);

        Assert.Null(list.Error);
        Assert.Equal(["A-model", "b-model"], list.Models);
        Assert.Equal("https://openrouter.ai/api/v1/models", Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task AModelListThatFailsGivesTheReason()
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.NotFound, """{"error":"not found"}"""));

        var list = await Clients(handler).ListModelsAsync(
            new AiConnection("custom", new Uri("https://llm.example.org/v1"), "k", ""), Ct);

        Assert.Empty(list.Models);
        Assert.Equal("llm.example.org answered 404: not found", list.Error);
    }

    private static AiClients Clients(HttpMessageHandler handler)
    {
        // The test and the model list never read the settings row, so the context is never opened.
        var db = new ModbotContext(new DbContextOptionsBuilder<ModbotContext>().UseNpgsql("Host=unused").Options);
        return new AiClients(db, AesGcmSecretProtector.ForTesting(), new SingleClientFactory(handler));
    }

    private static string Completion(string model, string text) => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-1",
        @object = "chat.completion",
        created = 1_700_000_000,
        model,
        choices = new[]
        {
            new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = text } },
        },
    });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

/// <summary>Answers every request from a script and remembers what was asked.</summary>
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
