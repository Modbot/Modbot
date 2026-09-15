using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Security;
using OpenAI;
using OpenAI.Chat;

namespace Modbot.AI;

/// <summary>The one place an <see cref="OpenAIClient"/> is built.</summary>
public sealed class AiClients : IAiClients
{
    /// <summary>
    /// The named <see cref="HttpClient"/> every AI request goes through.
    /// </summary>
    /// <remarks>
    /// Its own client, never the VRChat one. The optional SOCKS5 egress proxy (spec 2.3.1) exists
    /// to get past VRChat's firewall and is attached only to the clients <c>IVRChatGate</c>
    /// builds; sending AI traffic through it would put members' profile text and the API key
    /// through somebody's proxy for no reason.
    /// </remarks>
    public const string HttpClientName = "Modbot.AI";

    /// <summary>
    /// Sent as the key when none is stored. The SDK requires one, and a local server that wants no
    /// key ignores whatever arrives.
    /// </summary>
    public const string NoKey = "none";

    private const string TestPrompt = "Reply with the single word OK.";
    private const int TestMaxOutputTokens = 256;
    private const int MaxModels = 5000;
    private const int MaxMessageLength = 500;

    private static readonly TimeSpan TestTimeout = Calls.AiTimeouts.Test;

    private readonly ModbotContext _db;
    private readonly ISecretProtector _protector;
    private readonly IHttpClientFactory _http;

    public AiClients(ModbotContext db, ISecretProtector protector, IHttpClientFactory http)
    {
        _db = db;
        _protector = protector;
        _http = http;
    }

    public async Task<AiChat?> GetChatAsync(CancellationToken ct)
    {
        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.AiEnabled, s.AiProvider, s.AiEndpoint, s.AiModel, s.AiApiKeyEncrypted, s.AiFallbackModel })
            .FirstOrDefaultAsync(ct);

        if (settings is null || !settings.AiEnabled)
            return null;

        // Checked again on read, not only on save: a hand-edited row must not be able to send the
        // key over plain http to a hosted provider either.
        var check = AiSettingsRules.Check(true, settings.AiProvider, settings.AiEndpoint, settings.AiModel, apiKey: null);
        if (!check.Ok)
            return null;

        var connection = new AiConnection(
            check.Provider!.Id, check.Endpoint!, _protector.Unprotect(settings.AiApiKeyEncrypted), check.Model!);

        var client = CreateClient(connection, forTest: false);

        var fallback = string.IsNullOrWhiteSpace(settings.AiFallbackModel) ? null : settings.AiFallbackModel.Trim();
        if (string.Equals(fallback, connection.Model, StringComparison.OrdinalIgnoreCase))
            fallback = null;

        return new AiChat(
            client.GetChatClient(connection.Model), client, connection.Model, connection.Provider, fallback, connection.Endpoint);
    }

    public async Task<AiTestResult> TestAsync(AiConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var chat = CreateClient(connection, forTest: true).GetChatClient(connection.Model);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            var options = new ChatCompletionOptions { MaxOutputTokenCount = TestMaxOutputTokens };
            Usage.AiReportedCost.AskFor(options, connection.Provider);

            ChatCompletion completion = await chat.CompleteChatAsync([new UserChatMessage(TestPrompt)], options, ct);

            var reply = string.Concat(completion.Content
                .Where(p => p.Kind == ChatMessageContentPartKind.Text)
                .Select(p => p.Text)).Trim();

            var model = string.IsNullOrWhiteSpace(completion.Model) ? connection.Model : completion.Model;

            return new AiTestResult(
                true,
                reply.Length == 0 ? $"{model} answered." : $"{model} answered: {Shorten(reply)}",
                completion.Usage,
                TestPrompt,
                Elapsed(started));
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return new AiTestResult(false, Describe(e, connection.Endpoint), null, TestPrompt, Elapsed(started));
        }
    }

    public async Task<AiModelList> ListModelsAsync(AiConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var models = CreateClient(connection, forTest: true).GetOpenAIModelClient();

        try
        {
            // The raw answer rather than the SDK's model type: OpenRouter, xAI, Anthropic and
            // Ollama each add or omit fields around the one that matters, and "data[].id" is the
            // part every one of them agrees on.
            var result = await models.GetModelsAsync(new RequestOptions { CancellationToken = ct });

            using var document = JsonDocument.Parse(result.GetRawResponse().Content);

            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return new AiModelList([], "The endpoint did not return a model list.");

            var ids = data.EnumerateArray()
                .Select(m => m.ValueKind == JsonValueKind.Object
                             && m.TryGetProperty("id", out var id)
                             && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null)
                .OfType<string>()
                .Where(id => id.Length is > 0 and <= AiSettingsRules.MaxModelLength)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(MaxModels)
                .ToList();

            return new AiModelList(ids, null);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return new AiModelList([], Describe(e, connection.Endpoint));
        }
    }

    private OpenAIClient CreateClient(AiConnection connection, bool forTest)
    {
        var http = _http.CreateClient(HttpClientName);

        // The SDK's pipeline owns the timeout; HttpClient's own 100 seconds would cut a long
        // completion off underneath it.
        http.Timeout = Timeout.InfiniteTimeSpan;

        var options = new OpenAIClientOptions
        {
            Endpoint = connection.Endpoint,
            Transport = new HttpClientPipelineTransport(http),
        };

        if (forTest)
        {
            // Somebody is watching a button. One attempt, a short wait, and the provider's own
            // answer -- including a 429, which is itself the useful thing to show.
            options.RetryPolicy = new ClientRetryPolicy(maxRetries: 0);
            options.NetworkTimeout = TestTimeout;
        }

        var key = string.IsNullOrWhiteSpace(connection.ApiKey) ? NoKey : connection.ApiKey.Trim();
        return new OpenAIClient(new ApiKeyCredential(key), options);
    }

    /// <summary>What went wrong, in the provider's words where it gave any.</summary>
    internal static string Describe(Exception e, Uri endpoint)
    {
        switch (e)
        {
            case ClientResultException result:
                var status = result.Status;
                var said = ProviderMessage(result);
                if (status == 0)
                    return $"Could not reach {endpoint.Host}: {Shorten(said ?? result.Message)}";
                return said is null
                    ? $"{endpoint.Host} answered {status}."
                    : $"{endpoint.Host} answered {status}: {Shorten(said)}";

            case OperationCanceledException:
                return $"{endpoint.Host} did not answer in time.";

            case HttpRequestException http:
                return $"Could not reach {endpoint.Host}: {Shorten(http.Message)}";

            case JsonException or FormatException or InvalidOperationException or KeyNotFoundException:
                return $"{endpoint.Host} answered with something that is not an OpenAI-compatible reply.";

            default:
                return $"Could not reach {endpoint.Host}: {Shorten(e.Message)}";
        }
    }

    /// <summary>
    /// The error sentence from the response body. OpenAI, xAI, OpenRouter and Anthropic's
    /// compatibility layer all put it at <c>error.message</c>; some servers put a plain string at
    /// <c>error</c> or <c>message</c> instead.
    /// </summary>
    private static string? ProviderMessage(ClientResultException e)
    {
        var body = e.GetRawResponse()?.Content;
        if (body is null)
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return Text(error.GetString());

                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return Text(message.GetString());
                }
            }

            return root.TryGetProperty("message", out var top) && top.ValueKind == JsonValueKind.String
                ? Text(top.GetString())
                : null;
        }
        catch (JsonException)
        {
            var text = body.ToString();
            return text.TrimStart().StartsWith('<') ? null : Text(text);
        }
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int Elapsed(long started) =>
        (int)Math.Min(int.MaxValue, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    private static string Shorten(string text) =>
        text.Length <= MaxMessageLength ? text : string.Concat(text.AsSpan(0, MaxMessageLength), "…");
}
