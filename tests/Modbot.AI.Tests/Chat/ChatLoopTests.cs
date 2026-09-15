using System.Net;
using System.Text.Json;
using Modbot.AI.Chat;
using Modbot.Core.Data.Entities;

namespace Modbot.AI.Tests.Chat;

/// <summary>
/// The reply loop against a scripted provider: tool calls, then an answer, and every limit. Nothing
/// here reaches a real provider.
/// </summary>
public class ChatLoopTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(ChatOutcome Outcome, List<ChatEvent> Events)> RunAsync(ChatRequest request, ListLogger<ChatLoop>? logger = null)
    {
        var events = new List<ChatEvent>();
        var outcome = await new ChatLoop(logger ?? new ListLogger<ChatLoop>())
            .RunAsync(request, e => { events.Add(e); return Task.CompletedTask; }, Ct);
        return (outcome, events);
    }

    [Fact]
    public async Task AToolCall_ThenAnAnswer()
    {
        var provider = new StreamingProvider()
            .Then(Sse.Of(
                Sse.ToolCall(0, "call_a", "find_person", "{\"que"),
                Sse.MoreArguments(0, "ry\":\"Gunner\"}"),
                Sse.Finish("tool_calls")))
            .Then(Sse.Of(Sse.Text("Gunner24 "), Sse.Text("was banned once."), Sse.Finish("stop")));

        var tool = new FakeTool("find_person", ModbotPermissions.ViewProfile);

        var (outcome, events) = await RunAsync(ChatTestKit.Request(provider, [tool]));

        Assert.Equal(ChatOutcome.Answered, outcome);
        Assert.Equal("""{"query":"Gunner"}""", Assert.Single(tool.Calls));

        var turns = events.OfType<ChatTurnEvent>().Select(e => e.Turn).ToList();
        Assert.Equal([ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant], turns.Select(t => t.Role));

        var asked = Assert.Single(turns[0].ToolCalls);
        Assert.Equal(("call_a", "find_person"), (asked.Id, asked.Name));

        Assert.Equal("call_a", turns[1].ToolCallId);
        Assert.True(turns[1].Worked);
        Assert.Equal("usr_1", Assert.Single(turns[1].References!).Id);

        Assert.Equal("Gunner24 was banned once.", turns[2].Content);
        Assert.Equal("Gunner24 was banned once.", string.Concat(events.OfType<ChatTextEvent>().Select(e => e.Text)));
        Assert.Equal("find_person", Assert.Single(events.OfType<ChatToolStartedEvent>()).Tool);

        var done = Assert.IsType<ChatFinishedEvent>(events[^1]);
        Assert.Equal((ChatOutcome.Answered, 1), (done.Outcome, done.ToolCalls));

        // The first request offered the tool; the second carried its result back.
        Assert.Equal(2, provider.Requests.Count);
        Assert.True(provider.Requests[0].GetProperty("stream").GetBoolean());
        Assert.Equal(["find_person"], ToolNames(provider.Requests[0]));

        var result = provider.Requests[1].GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Equal("call_a", result.GetProperty("tool_call_id").GetString());
        Assert.Contains("Gunner24", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheToolsOffered_AreSentToTheProvider()
    {
        var provider = new StreamingProvider().Then(Sse.Of(Sse.Text("Nothing to look up."), Sse.Finish("stop")));

        await RunAsync(ChatTestKit.Request(provider,
            [new FakeTool("find_person", ModbotPermissions.ViewProfile), new FakeTool("get_world", ModbotPermissions.ViewAnalytics)]));

        Assert.Equal(["find_person", "get_world"], ToolNames(provider.Requests[0]));
    }

    [Fact]
    public async Task WithNoToolsOffered_TheRequestCarriesNone()
    {
        var provider = new StreamingProvider().Then(Sse.Of(Sse.Text("Hello."), Sse.Finish("stop")));

        var (outcome, _) = await RunAsync(ChatTestKit.Request(provider, []));

        Assert.Equal(ChatOutcome.Answered, outcome);
        Assert.False(provider.Requests[0].TryGetProperty("tools", out _));
    }

    /// <summary>
    /// A model can name any function it likes. One that was not offered to this person is never
    /// run, even when a tool of that name exists (design §3.1).
    /// </summary>
    [Fact]
    public async Task AToolTheModelNamesButWasNotOffered_NeverRuns()
    {
        var hidden = new FakeTool("search_audit_log", ModbotPermissions.ViewAuditLog);
        var offered = new FakeTool("find_person", ModbotPermissions.ViewProfile);

        var provider = new StreamingProvider()
            .Then(Sse.Of(Sse.ToolCall(0, "call_x", "search_audit_log", "{}"), Sse.Finish("tool_calls")))
            .Then(Sse.Of(Sse.Text("I can't see that."), Sse.Finish("stop")));

        var (outcome, events) = await RunAsync(ChatTestKit.Request(provider, [offered]));

        Assert.Equal(ChatOutcome.Answered, outcome);
        Assert.Empty(hidden.Calls);
        Assert.Empty(offered.Calls);
        Assert.Empty(events.OfType<ChatToolStartedEvent>());

        var refused = events.OfType<ChatTurnEvent>().Select(e => e.Turn).Single(t => t.Role == ChatRole.Tool);
        Assert.False(refused.Worked);
        Assert.Contains("No such tool", refused.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PastTheToolCallLimit_CallsAreRefused_AndTheNextRoundOffersNoTools()
    {
        var tool = new FakeTool("find_person", ModbotPermissions.ViewProfile);

        var provider = new StreamingProvider()
            .Then(Sse.Of(
                Sse.ToolCall(0, "call_1", "find_person", """{"query":"a"}"""),
                Sse.ToolCall(1, "call_2", "find_person", """{"query":"b"}"""),
                Sse.Finish("tool_calls")))
            .Then(Sse.Of(Sse.Text("Here is what I found."), Sse.Finish("stop")));

        var (outcome, events) = await RunAsync(ChatTestKit.Request(provider, [tool],
            limits: new ChatLimits(MaxToolCalls: 1, MaxReplyTokens: 1000, TimeLimit: TimeSpan.FromSeconds(30))));

        Assert.Equal(ChatOutcome.Answered, outcome);
        Assert.Equal("""{"query":"a"}""", Assert.Single(tool.Calls));

        var results = events.OfType<ChatTurnEvent>().Select(e => e.Turn).Where(t => t.Role == ChatRole.Tool).ToList();
        Assert.Equal([true, false], results.Select(r => r.Worked == true));
        Assert.Contains("limit reached", results[1].Content, StringComparison.Ordinal);

        Assert.True(provider.Requests[0].TryGetProperty("tools", out _));
        Assert.False(provider.Requests[1].TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task AModelThatKeepsCallingTools_IsStopped()
    {
        var tool = new FakeTool("find_person", ModbotPermissions.ViewProfile);
        var provider = new StreamingProvider();

        for (var i = 0; i < 10; i++)
            provider.Then(Sse.Of(Sse.ToolCall(0, $"call_{i}", "find_person", "{}"), Sse.Finish("tool_calls")));

        var (outcome, events) = await RunAsync(ChatTestKit.Request(provider, [tool],
            limits: new ChatLimits(2, 1000, TimeSpan.FromSeconds(30))));

        Assert.Equal(ChatOutcome.LimitReached, outcome);
        Assert.Equal(2, tool.Calls.Count);
        Assert.Equal(4, provider.Requests.Count);
        Assert.NotNull(Assert.IsType<ChatFinishedEvent>(events[^1]).Error);
    }

    [Fact]
    public async Task TheReplyLengthLimit_IsSentOnEveryRound()
    {
        var provider = new StreamingProvider()
            .Then(Sse.Of(Sse.ToolCall(0, "call_1", "find_person", "{}"), Sse.Finish("tool_calls")))
            .Then(Sse.Of(Sse.Text("Done."), Sse.Finish("stop")));

        await RunAsync(ChatTestKit.Request(provider, [new FakeTool("find_person", ModbotPermissions.None)],
            limits: new ChatLimits(8, 321, TimeSpan.FromSeconds(30))));

        Assert.All(provider.Requests, r => Assert.Equal(321, r.GetProperty("max_completion_tokens").GetInt32()));
    }

    [Fact]
    public async Task AReplyThatTakesTooLong_IsStopped()
    {
        var provider = new StreamingProvider().ThenNeverAnswer();

        var (outcome, events) = await RunAsync(ChatTestKit.Request(provider, [],
            limits: new ChatLimits(8, 1000, TimeSpan.FromMilliseconds(200))));

        Assert.Equal(ChatOutcome.TimedOut, outcome);
        Assert.Equal(ChatOutcome.TimedOut, Assert.IsType<ChatFinishedEvent>(Assert.Single(events)).Outcome);
    }

    [Fact]
    public async Task AProviderError_IsReportedInItsOwnWords()
    {
        var provider = new StreamingProvider()
            .ThenStatus(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key provided."}}""");

        var (outcome, events) = await RunAsync(ChatTestKit.Request(provider, []));

        Assert.Equal(ChatOutcome.Failed, outcome);
        Assert.Equal("llm.example.org answered 401: Incorrect API key provided.", Assert.IsType<ChatFinishedEvent>(events[^1]).Error);
    }

    [Fact]
    public async Task AToolThatThrows_GivesTheModelAFailure_NotTheException()
    {
        var tool = new FakeTool("find_person", ModbotPermissions.None)
        {
            Answer = _ => throw new InvalidOperationException("connection string Host=secret"),
        };

        var provider = new StreamingProvider()
            .Then(Sse.Of(Sse.ToolCall(0, "call_1", "find_person", "{}"), Sse.Finish("tool_calls")))
            .Then(Sse.Of(Sse.Text("Sorry."), Sse.Finish("stop")));

        var (outcome, events) = await RunAsync(ChatTestKit.Request(provider, [tool]));

        Assert.Equal(ChatOutcome.Answered, outcome);
        var result = events.OfType<ChatTurnEvent>().Select(e => e.Turn).Single(t => t.Role == ChatRole.Tool);
        Assert.False(result.Worked);
        Assert.DoesNotContain("secret", result.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reply cut off after the model asked for a tool leaves a call with no stored result;
    /// providers refuse a conversation like that, so the loop fills the gap.
    /// </summary>
    [Fact]
    public async Task AToolCallWithNoStoredResult_IsGivenOne()
    {
        var provider = new StreamingProvider().Then(Sse.Of(Sse.Text("OK."), Sse.Finish("stop")));

        IReadOnlyList<ChatTurn> history =
        [
            ChatTurn.User("first"),
            new ChatTurn(ChatRole.Assistant, "", [new ChatToolCallRecord("call_lost", "find_person", "{}")]),
            ChatTurn.User("second"),
        ];

        await RunAsync(ChatTestKit.Request(provider, [], history: history));

        var roles = provider.Requests[0].GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("role").GetString())
            .ToList();

        Assert.Equal(["system", "user", "assistant", "tool", "user"], roles);
    }

    [Fact]
    public async Task TheLog_NamesTheUserToolAndDuration_ButNeverTheText()
    {
        const string question = "Is PurpleElephant42 the one who crashed the rave?";
        const string arguments = """{"query":"PurpleElephant42"}""";

        var provider = new StreamingProvider()
            .Then(Sse.Of(Sse.ToolCall(0, "call_1", "find_person", arguments), Sse.Finish("tool_calls")))
            .Then(Sse.Of(Sse.Text("PurpleElephant42 has no bans."), Sse.Finish("stop")));

        var logger = new ListLogger<ChatLoop>();
        await RunAsync(ChatTestKit.Request(provider, [new FakeTool("find_person", ModbotPermissions.None)], question), logger);

        Assert.Contains(logger.Lines, l => l.Contains("find_person", StringComparison.Ordinal)
                                           && l.Contains("11111111-1111-1111-1111-111111111111", StringComparison.Ordinal)
                                           && l.Contains("DurationMs=", StringComparison.Ordinal));
        Assert.Contains(logger.Lines, l => l.Contains("Chat reply", StringComparison.Ordinal));
        Assert.All(logger.Lines, l => Assert.DoesNotContain("PurpleElephant42", l, StringComparison.Ordinal));
    }

    private static List<string?> ToolNames(JsonElement request) =>
        request.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("function").GetProperty("name").GetString())
            .ToList();

    private static string Text(JsonElement message)
    {
        var content = message.GetProperty("content");
        return content.ValueKind == JsonValueKind.String
            ? content.GetString()!
            : string.Concat(content.EnumerateArray().Select(p => p.GetProperty("text").GetString()));
    }
}
