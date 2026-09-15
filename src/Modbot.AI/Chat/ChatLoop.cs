using System.ClientModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace Modbot.AI.Chat;

/// <summary>The limits on one reply (AI chat design §2).</summary>
/// <param name="MaxToolCalls">Tool calls the reply may make. Zero offers no tools at all.</param>
/// <param name="MaxReplyTokens">Sent as the output token limit on every round.</param>
/// <param name="TimeLimit">For the whole reply, tool calls included.</param>
public sealed record ChatLimits(int MaxToolCalls, int MaxReplyTokens, TimeSpan TimeLimit);

public enum ChatRole
{
    User,
    Assistant,
    Tool,
}

/// <param name="Arguments">The JSON the model sent, as it sent it.</param>
public sealed record ChatToolCallRecord(string Id, string Name, string Arguments);

/// <summary>
/// One message of a conversation, in the shape it is stored in rather than the SDK's, so the
/// endpoint that stores it never handles SDK types.
/// </summary>
public sealed record ChatTurn(
    ChatRole Role,
    string Content,
    IReadOnlyList<ChatToolCallRecord> ToolCalls,
    string? ToolCallId = null,
    string? ToolName = null,
    IReadOnlyList<ChatReference>? References = null,
    bool? Worked = null,
    int? DurationMs = null)
{
    public static ChatTurn User(string text) => new(ChatRole.User, text, []);
}

public enum ChatOutcome
{
    /// <summary>The model answered.</summary>
    Answered,

    /// <summary>The model kept asking for tools after it was told to stop.</summary>
    LimitReached,

    TimedOut,

    /// <summary>The provider refused or could not be reached.</summary>
    Failed,

    /// <summary>The person went away; nothing more is sent.</summary>
    Cancelled,
}

/// <summary>Something that happened while a reply was written, in the order it happened.</summary>
public abstract record ChatEvent;

/// <summary>More of the reply's text.</summary>
public sealed record ChatTextEvent(string Text) : ChatEvent;

/// <summary>A tool is about to run.</summary>
public sealed record ChatToolStartedEvent(string CallId, string Tool) : ChatEvent;

/// <summary>A message to store: the model's reply or tool request, or a tool's result.</summary>
public sealed record ChatTurnEvent(ChatTurn Turn) : ChatEvent;

/// <param name="Error">For <see cref="ChatOutcome.Failed"/> and <see cref="ChatOutcome.TimedOut"/>: what to show.</param>
public sealed record ChatFinishedEvent(ChatOutcome Outcome, string? Error, int ToolCalls) : ChatEvent;

/// <summary>Everything one reply needs.</summary>
/// <param name="History">The conversation so far, ending with the person's new message.</param>
/// <param name="Tools">What this person is offered. Nothing else will run.</param>
/// <param name="ConversationId">For the log line only.</param>
/// <param name="ProviderAddress">For error messages: which host refused.</param>
public sealed record ChatRequest(
    ChatClient Chat,
    string SystemPrompt,
    IReadOnlyList<ChatTurn> History,
    IReadOnlyList<IChatTool> Tools,
    ChatLimits Limits,
    ChatToolContext Context,
    Guid? ConversationId = null,
    Uri? ProviderAddress = null);

/// <summary>
/// Writes one reply: asks the model, runs the tools it asks for, and asks again until it answers
/// (AI chat design §2).
/// </summary>
/// <remarks>
/// <para>
/// Events go to a callback rather than out of an async iterator, because C# cannot yield from
/// inside the try blocks that turn a timeout or a provider error into an outcome.
/// </para>
/// <para>
/// Logs the person's id, tool names, outcomes and durations. Never the message text or the tool
/// arguments (design §8).
/// </para>
/// </remarks>
public sealed class ChatLoop
{
    private const string LimitReachedResult = """{"error":"Tool call limit reached. Answer with what you have."}""";
    private const string NoSuchToolResult = """{"error":"No such tool."}""";
    private const string BadArgumentsResult = """{"error":"The arguments were not valid JSON."}""";
    private const string ToolFailedResult = """{"error":"The tool failed."}""";
    private const string NoResult = """{"error":"No result."}""";

    private readonly ILogger<ChatLoop> _logger;

    public ChatLoop(ILogger<ChatLoop> logger) => _logger = logger;

    public async Task<ChatOutcome> RunAsync(ChatRequest request, Func<ChatEvent, Task> onEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onEvent);

        var started = Stopwatch.GetTimestamp();
        var userId = request.Context.UserId;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(request.Limits.TimeLimit);
        var token = deadline.Token;

        var messages = new List<ChatMessage> { new SystemChatMessage(request.SystemPrompt) };
        messages.AddRange(ToSdk(request.History));

        var offered = request.Tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var toolCalls = 0;

        // Every round but the last may call tools; the rounds beyond the tool limit exist only to
        // let the model answer. A model that asks for tools it was not offered, round after round,
        // still stops here.
        var maxRounds = request.Limits.MaxToolCalls + 2;

        ChatOutcome outcome;
        string? error = null;

        try
        {
            outcome = ChatOutcome.LimitReached;

            for (var round = 0; round < maxRounds; round++)
            {
                var toolsAllowed = offered.Count > 0 && toolCalls < request.Limits.MaxToolCalls;
                var options = new ChatCompletionOptions { MaxOutputTokenCount = request.Limits.MaxReplyTokens };

                if (toolsAllowed)
                {
                    foreach (var tool in request.Tools)
                        options.Tools.Add(ChatTool.CreateFunctionTool(tool.Name, tool.Description, tool.Parameters));
                }

                var (text, calls) = await StreamRoundAsync(request.Chat, messages, options, onEvent, token);

                if (calls.Count == 0)
                {
                    await onEvent(new ChatTurnEvent(new ChatTurn(ChatRole.Assistant, text, [])));
                    outcome = ChatOutcome.Answered;
                    break;
                }

                var assistant = new AssistantChatMessage(
                    calls.Select(c => ChatToolCall.CreateFunctionToolCall(c.Id, c.Name, BinaryData.FromString(c.Arguments))));
                if (text.Length > 0)
                    assistant.Content.Add(ChatMessageContentPart.CreateTextPart(text));

                messages.Add(assistant);
                await onEvent(new ChatTurnEvent(new ChatTurn(ChatRole.Assistant, text, calls)));

                foreach (var call in calls)
                {
                    var turn = await RunToolAsync(call, offered, toolCalls, request, onEvent, token);
                    toolCalls++;

                    messages.Add(new ToolChatMessage(call.Id, turn.Content));
                    await onEvent(new ChatTurnEvent(turn));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = ChatOutcome.Cancelled;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            outcome = ChatOutcome.TimedOut;
            error = "The reply took too long.";
        }
        catch (Exception e) when (e is ClientResultException or HttpRequestException or JsonException or InvalidOperationException)
        {
            outcome = ChatOutcome.Failed;
            error = request.ProviderAddress is { } address
                ? AiClients.Describe(e, address)
                : "The AI provider could not answer.";

            _logger.LogWarning(
                "Chat reply for user {UserId} failed at the provider: {ErrorType}", userId, e.GetType().Name);
        }

        _logger.LogInformation(
            "Chat reply for user {UserId} in conversation {ConversationId}: {Outcome}, {ToolCalls} tool calls, {DurationMs} ms",
            userId, request.ConversationId, outcome, toolCalls, Milliseconds(started));

        if (outcome != ChatOutcome.Cancelled)
        {
            if (outcome == ChatOutcome.LimitReached)
                error = "The reply used every tool call it was allowed.";

            await onEvent(new ChatFinishedEvent(outcome, error, toolCalls));
        }

        return outcome;
    }

    private async Task<ChatTurn> RunToolAsync(
        ChatToolCallRecord call,
        IReadOnlyDictionary<string, IChatTool> offered,
        int callsSoFar,
        ChatRequest request,
        Func<ChatEvent, Task> onEvent,
        CancellationToken ct)
    {
        var userId = request.Context.UserId;

        if (callsSoFar >= request.Limits.MaxToolCalls)
            return Refused(call, LimitReachedResult);

        // Offered means offered to this person. A tool the model names that is not in that list
        // -- whether it exists or not -- is never run (design §3.1).
        if (!offered.TryGetValue(call.Name, out var tool))
        {
            _logger.LogWarning("Chat tool {Tool} for user {UserId} was not offered and did not run", Shorten(call.Name), userId);
            return Refused(call, NoSuchToolResult);
        }

        JsonElement arguments;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
            arguments = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Refused(call, BadArgumentsResult);
        }

        await onEvent(new ChatToolStartedEvent(call.Id, tool.Name));

        var started = Stopwatch.GetTimestamp();
        ChatToolResult result;

        try
        {
            result = await tool.RunAsync(request.Context, arguments, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Chat tool {Tool} for user {UserId} threw {ErrorType}", tool.Name, userId, e.GetType().Name);
            result = new ChatToolResult(ToolFailedResult, [], Worked: false);
        }

        var duration = Milliseconds(started);

        _logger.LogInformation(
            "Chat tool {Tool} for user {UserId}: {Result} in {DurationMs} ms",
            tool.Name, userId, result.Worked ? "worked" : "did not work", duration);

        var content = result.Content.Length <= ChatSettingsRules.MaxToolResultLength
            ? result.Content
            : string.Concat(result.Content.AsSpan(0, ChatSettingsRules.MaxToolResultLength), " [cut: too long]");

        return new ChatTurn(ChatRole.Tool, content, [], call.Id, tool.Name, result.References, result.Worked, duration);
    }

    private static ChatTurn Refused(ChatToolCallRecord call, string content) =>
        new(ChatRole.Tool, content, [], call.Id, Shorten(call.Name), [], false, 0);

    private static async Task<(string Text, IReadOnlyList<ChatToolCallRecord> Calls)> StreamRoundAsync(
        ChatClient chat,
        List<ChatMessage> messages,
        ChatCompletionOptions options,
        Func<ChatEvent, Task> onEvent,
        CancellationToken ct)
    {
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Arguments)>();

        await foreach (var update in chat.CompleteChatStreamingAsync(messages, options, ct))
        {
            foreach (var part in update.ContentUpdate)
            {
                if (part.Kind != ChatMessageContentPartKind.Text || string.IsNullOrEmpty(part.Text))
                    continue;

                text.Append(part.Text);
                await onEvent(new ChatTextEvent(part.Text));
            }

            foreach (var piece in update.ToolCallUpdates)
            {
                var call = calls.TryGetValue(piece.Index, out var existing) ? existing : (null, null, new StringBuilder());

                // The id and name arrive once, on a call's first piece; the arguments arrive in
                // fragments that only make JSON once they are all joined.
                if (call.Id is null && !string.IsNullOrEmpty(piece.ToolCallId))
                    call.Id = piece.ToolCallId;
                if (call.Name is null && !string.IsNullOrEmpty(piece.FunctionName))
                    call.Name = piece.FunctionName;
                if (piece.FunctionArgumentsUpdate is { } fragment)
                    call.Arguments.Append(fragment.ToString());

                calls[piece.Index] = call;
            }
        }

        var finished = calls
            .Where(c => c.Value.Name is not null)
            // Some local servers leave the id out. The result has to name a call, so one is made up.
            .Select(c => new ChatToolCallRecord(c.Value.Id ?? $"call_{c.Key}", c.Value.Name!, c.Value.Arguments.ToString()))
            .ToList();

        return (text.ToString(), finished);
    }

    /// <summary>
    /// The stored conversation, as the SDK's messages.
    /// </summary>
    /// <remarks>
    /// A reply cut off between asking for a tool and storing its result leaves a call with no
    /// answer, and providers refuse a conversation shaped like that. Such a call is given an empty
    /// result here rather than making the whole conversation unusable.
    /// </remarks>
    internal static IEnumerable<ChatMessage> ToSdk(IReadOnlyList<ChatTurn> history)
    {
        var answered = history
            .Where(t => t.Role == ChatRole.Tool && t.ToolCallId is not null)
            .Select(t => t.ToolCallId!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var turn in history)
        {
            switch (turn.Role)
            {
                case ChatRole.User:
                    yield return new UserChatMessage(turn.Content);
                    break;

                case ChatRole.Assistant when turn.ToolCalls.Count > 0:
                    var assistant = new AssistantChatMessage(turn.ToolCalls.Select(c =>
                        ChatToolCall.CreateFunctionToolCall(c.Id, c.Name, BinaryData.FromString(string.IsNullOrEmpty(c.Arguments) ? "{}" : c.Arguments))));
                    if (turn.Content.Length > 0)
                        assistant.Content.Add(ChatMessageContentPart.CreateTextPart(turn.Content));
                    yield return assistant;

                    foreach (var missing in turn.ToolCalls.Where(c => !answered.Contains(c.Id)))
                        yield return new ToolChatMessage(missing.Id, NoResult);
                    break;

                case ChatRole.Assistant:
                    yield return new AssistantChatMessage(turn.Content);
                    break;

                case ChatRole.Tool when turn.ToolCallId is not null:
                    yield return new ToolChatMessage(turn.ToolCallId, turn.Content);
                    break;
            }
        }
    }

    private static int Milliseconds(long started) =>
        (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    private static string Shorten(string name) => name.Length <= 64 ? name : name[..64];
}
