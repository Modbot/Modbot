using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Modbot.AI.Chat;
using Modbot.Core.Data.Entities;
using OpenAI;
using OpenAI.Chat;

namespace Modbot.AI.Tests.Chat;

/// <summary>A tool that answers from a script and remembers what it was asked.</summary>
public sealed class FakeTool(string name, ModbotPermissions needs, bool onlyReads = true) : IChatTool
{
    public List<string> Calls { get; } = [];

    public Func<JsonElement, ChatToolResult> Answer { get; set; } =
        _ => ChatToolResult.Json(new { people = new[] { new { userId = "usr_1", displayName = "Gunner24" } } },
            [new ChatReference(ChatReference.Person, "usr_1", "Gunner24")]);

    public string Name => name;

    public string Label => name;

    public string Description => $"The {name} tool.";

    public BinaryData Parameters => BinaryData.FromString("""{"type":"object","properties":{"query":{"type":"string"}}}""");

    public ModbotPermissions Needs => needs;

    public bool OnlyReads => onlyReads;

    public Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        Calls.Add(arguments.GetRawText());
        return Task.FromResult(Answer(arguments));
    }
}

/// <summary>Answers each request with the next scripted streamed completion.</summary>
public sealed class StreamingProvider : HttpMessageHandler
{
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _answers = new();

    public List<JsonElement> Requests { get; } = [];

    public StreamingProvider Then(string sse)
    {
        _answers.Enqueue(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        }));
        return this;
    }

    public StreamingProvider ThenStatus(HttpStatusCode status, string body)
    {
        _answers.Enqueue(_ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));
        return this;
    }

    public StreamingProvider ThenNeverAnswer()
    {
        _answers.Enqueue(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(JsonDocument.Parse(body).RootElement.Clone());

        if (_answers.Count == 0)
            throw new InvalidOperationException("The script ran out of answers.");

        return await _answers.Dequeue()(cancellationToken);
    }
}

/// <summary>Streamed chat completion chunks, as an OpenAI-compatible server sends them.</summary>
public static class Sse
{
    public static string Of(params object[] chunks) =>
        string.Concat(chunks.Select(c => "data: " + JsonSerializer.Serialize(c) + "\n\n")) + "data: [DONE]\n\n";

    public static object Text(string text, string? finish = null) => Chunk(new { role = "assistant", content = text }, finish);

    public static object Finish(string reason) => Chunk(new { }, reason);

    /// <summary>The first piece of a tool call: its id, its name and the start of its arguments.</summary>
    public static object ToolCall(int index, string id, string name, string arguments) => Chunk(new
    {
        role = "assistant",
        tool_calls = new[] { new { index, id, type = "function", function = new { name, arguments } } },
    });

    /// <summary>More of a tool call's arguments, with no id or name, the way they arrive.</summary>
    public static object MoreArguments(int index, string arguments) => Chunk(new
    {
        tool_calls = new[] { new { index, function = new { arguments } } },
    });

    private static object Chunk(object delta, string? finish = null) => new
    {
        id = "chatcmpl-1",
        @object = "chat.completion.chunk",
        created = 1_700_000_000,
        model = "test-model",
        choices = new[] { new { index = 0, delta, finish_reason = finish } },
    };
}

/// <summary>Keeps every log line, formatted, so a test can say what was never written.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var line = formatter(state, exception);
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
            line += " | " + string.Join(", ", values.Select(v => $"{v.Key}={v.Value}"));
        Lines.Add(line);
    }
}

public static class ChatTestKit
{
    public static readonly Uri Endpoint = new("https://llm.example.org/v1");

    public static ChatClient Client(HttpMessageHandler handler)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = Endpoint,
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };

        return new OpenAIClient(new ApiKeyCredential("sk-test"), options).GetChatClient("test-model");
    }

    public static ChatRequest Request(
        HttpMessageHandler handler,
        IReadOnlyList<IChatTool> tools,
        string question = "Has Gunner24 been banned?",
        ChatLimits? limits = null,
        IReadOnlyList<ChatTurn>? history = null) => new(
        Client(handler),
        "You are a test.",
        history ?? [ChatTurn.User(question)],
        tools,
        limits ?? new ChatLimits(8, 1000, TimeSpan.FromSeconds(30)),
        new ChatToolContext(Guid.Parse("11111111-1111-1111-1111-111111111111"), ModbotPermissions.Administrator, EmptyServices.Instance),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Endpoint);
}

public sealed class EmptyServices : IServiceProvider
{
    public static readonly EmptyServices Instance = new();

    public object? GetService(Type serviceType) => null;
}
