using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Modbot.AI.Calls;
using Modbot.AI.Usage;
using Modbot.Core.Data.Entities;
using OpenAI;
using OpenAI.Chat;

namespace Modbot.AI.Tests.Calls;

/// <summary>
/// The call boundary: the feature's timeout, the one retry on the fallback model, and the row in
/// the call log whatever happens. A scripted handler stands in for the provider; nothing here
/// reaches one.
/// </summary>
public class AiCallRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAnsweredCallIsRecordedWithItsTokensAndTheModelThatAnswered()
    {
        var log = new FakeCallLog();
        var usage = new FakeUsage();
        var chat = Chat(new ScriptedHandler(_ => Json(HttpStatusCode.OK, Completion("m-served", "hello"))));

        var result = await new AiCallRunner(log, usage).RunAsync(
            new AiCallPlan(AiFeatures.Moderation, chat, chat.Model, Prompt: "the instructions"),
            Ask,
            Ct);

        Assert.True(result.Answered);
        Assert.Equal("m-served", result.Model);
        Assert.False(result.Fallback);

        var row = Assert.Single(log.Entries);
        Assert.Equal(AiCallOutcomes.Answered, row.Outcome);
        Assert.Equal("m", row.ModelAsked);
        Assert.Equal("m-served", row.ModelAnswered);
        Assert.Equal(120, row.Usage!.InputTokenCount);
        Assert.Equal(100, row.Usage.InputTokenDetails.CachedTokenCount);
        Assert.Equal(30, row.Usage.OutputTokenCount);

        // The ledger is charged under the model the provider actually billed.
        Assert.Equal(("moderation", "m-served"), Assert.Single(usage.Recorded));
    }

    /// <summary>Each feature waits its own length, and Chat's is the operator's.</summary>
    [Fact]
    public void EachFeatureHasItsOwnTimeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), AiTimeouts.For(AiFeatures.Moderation));
        Assert.Equal(TimeSpan.FromMinutes(3), AiTimeouts.For(AiFeatures.Insights));
        Assert.Equal(TimeSpan.FromSeconds(30), AiTimeouts.For(AiFeatures.Test));
        Assert.Equal(TimeSpan.FromSeconds(45), AiTimeouts.For(AiFeatures.Chat, 45));
        Assert.Equal(TimeSpan.FromSeconds(120), AiTimeouts.For(AiFeatures.Chat));
    }

    /// <summary>
    /// A provider that never answers must not hold a feature up for ever. The timeout is an error
    /// on that call, never an empty answer that reads like "the model found nothing" -- and it is
    /// exactly the kind of failure a second model can survive, so it falls back.
    /// </summary>
    [Fact]
    public async Task ACallThatRunsPastItsTimeoutIsRecordedAsAnError_AndFallsBack()
    {
        var log = new FakeCallLog();
        var chat = Chat(new ScriptedHandler(_ => Json(HttpStatusCode.OK, Completion("spare", "hello"))), fallback: "spare");
        var attempts = 0;

        var result = await new AiCallRunner(log, new FakeUsage()).RunAsync(
            // Chat's timeout is the operator's, so one second here is a real deadline rather than
            // a trick: the runner is doing what it would do at a hundred and twenty.
            new AiCallPlan(AiFeatures.Chat, chat, chat.Model, ChatTimeLimitSeconds: 1),
            async (client, token) =>
            {
                if (attempts++ == 0)
                    await Task.Delay(Timeout.Infinite, token);

                return await Ask(client, token);
            },
            Ct);

        Assert.Equal(2, attempts);
        Assert.True(result.Answered);
        Assert.True(result.Fallback);

        Assert.Equal(AiCallOutcomes.TimedOut, log.Entries[0].Outcome);
        Assert.NotNull(log.Entries[0].Error);
        Assert.Null(log.Entries[0].ModelAnswered);
    }

    [Fact]
    public async Task AFailedCallIsTriedOnceOnTheFallback_AndBothAttemptsAreRecorded()
    {
        var log = new FakeCallLog();
        var asked = new List<string>();

        var chat = Chat(new ScriptedHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync(Ct).GetAwaiter().GetResult();
            var model = JsonDocument.Parse(body).RootElement.GetProperty("model").GetString()!;
            asked.Add(model);

            return model == "m"
                ? Json(HttpStatusCode.InternalServerError, """{"error":{"message":"upstream is down"}}""")
                : Json(HttpStatusCode.OK, Completion("spare", "hello"));
        }), fallback: "spare");

        var result = await new AiCallRunner(log, new FakeUsage()).RunAsync(
            new AiCallPlan(AiFeatures.Insights, chat, chat.Model), Ask, Ct);

        Assert.True(result.Answered);
        Assert.True(result.Fallback);
        Assert.Equal("spare", result.Model);
        Assert.Equal(["m", "spare"], asked);

        // Two rows: the attempt that failed and the one that answered, so the log says which
        // model actually wrote the answer.
        Assert.Equal(2, log.Entries.Count);
        Assert.Equal(AiCallOutcomes.Refused, log.Entries[0].Outcome);
        Assert.False(log.Entries[0].Fallback);
        Assert.Equal(AiCallOutcomes.Answered, log.Entries[1].Outcome);
        Assert.True(log.Entries[1].Fallback);
        Assert.Equal("spare", log.Entries[1].ModelAnswered);
        Assert.Equal(result.CallId, log.Entries[1].Id);
    }

    /// <summary>
    /// A key the provider will not take fails the same way on any model, so a second call would
    /// only spend a round trip to be told the same thing.
    /// </summary>
    [Theory]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(403)]
    public async Task AKeyTheProviderRefusesNeverFallsBack(int status)
    {
        var log = new FakeCallLog();
        var calls = 0;

        var chat = Chat(new ScriptedHandler(_ =>
        {
            calls++;
            return Json((HttpStatusCode)status, """{"error":{"message":"Incorrect API key provided."}}""");
        }), fallback: "spare");

        var result = await new AiCallRunner(log, new FakeUsage()).RunAsync(
            new AiCallPlan(AiFeatures.Moderation, chat, chat.Model), Ask, Ct);

        Assert.False(result.Answered);
        Assert.Equal(1, calls);
        Assert.Single(log.Entries);
    }

    /// <summary>
    /// A spend limit stops the call before it is made, so there is nothing to fall back from: the
    /// second model would be spending past the limit the operator set.
    /// </summary>
    [Fact]
    public async Task ASpendLimitIsRecordedAsALimitedCallWithNothingSent()
    {
        var log = new FakeCallLog();
        var calls = 0;
        var chat = Chat(new ScriptedHandler(_ =>
        {
            calls++;
            return Json(HttpStatusCode.OK, Completion("m", "hello"));
        }), fallback: "spare");

        await new AiCallRunner(log, new FakeUsage())
            .RecordLimitedAsync(AiFeatures.Moderation, chat.Model, chat.Provider, "The monthly limit is reached.", null, Ct);

        Assert.Equal(0, calls);
        var row = Assert.Single(log.Entries);
        Assert.Equal(AiCallOutcomes.Limited, row.Outcome);
        Assert.Equal("The monthly limit is reached.", row.Error);
        Assert.Null(row.Usage);
    }

    /// <summary>The person closed the page. Nothing is recorded and nothing is tried again.</summary>
    [Fact]
    public async Task TheCallersOwnCancellationIsNotAFallback()
    {
        var log = new FakeCallLog();
        var calls = 0;
        using var cancelled = new CancellationTokenSource();

        var chat = Chat(new ScriptedHandler(_ =>
        {
            calls++;
            cancelled.Cancel();
            throw new TaskCanceledException();
        }), fallback: "spare");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AiCallRunner(log, new FakeUsage()).RunAsync(
                new AiCallPlan(AiFeatures.Chat, chat, chat.Model), Ask, cancelled.Token));

        Assert.Equal(1, calls);
        Assert.Empty(log.Entries);
    }

    /// <summary>
    /// A call somebody started from a button keeps what was sent and what came back; a call Modbot
    /// made on its own keeps counts only until something is flagged.
    /// </summary>
    [Fact]
    public async Task OnlyACallThatAsksToKeepItsTextKeepsIt()
    {
        var log = new FakeCallLog();
        var chat = Chat(new ScriptedHandler(_ => Json(HttpStatusCode.OK, Completion("m", "hello"))));
        var runner = new AiCallRunner(log, new FakeUsage());

        await runner.RunAsync(new AiCallPlan(AiFeatures.Moderation, chat, chat.Model, Prompt: "asked"), Ask, Ct);
        await runner.RunAsync(new AiCallPlan(AiFeatures.Test, chat, chat.Model, Prompt: "asked", KeepText: true), Ask, Ct);

        Assert.False(log.Entries[0].KeepText);
        Assert.True(log.Entries[1].KeepText);
    }

    private static async Task<AiCallAnswer<string>> Ask(ChatClient client, CancellationToken ct)
    {
        ChatCompletion completion = await client.CompleteChatAsync([new UserChatMessage("hi")], new ChatCompletionOptions(), ct);

        var text = string.Concat(completion.Content.Where(p => p.Kind == ChatMessageContentPartKind.Text).Select(p => p.Text));
        return new AiCallAnswer<string>(text, completion.Model, completion.Usage, text);
    }

    private static AiChat Chat(HttpMessageHandler handler, string? fallback = null)
    {
        var client = new OpenAIClient(new ApiKeyCredential("k"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://llm.example/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        });

        return new AiChat(client.GetChatClient("m"), client, "m", "custom", fallback, new Uri("https://llm.example/v1"));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

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
        usage = new
        {
            prompt_tokens = 120,
            completion_tokens = 30,
            total_tokens = 150,
            prompt_tokens_details = new { cached_tokens = 100 },
        },
    });

    private sealed class FakeCallLog : IAiCallLog
    {
        public List<Recorded> Entries { get; } = [];

        public Task<Guid> RecordAsync(AiCallEntry entry, CancellationToken ct)
        {
            var id = Guid.CreateVersion7();
            Entries.Add(new Recorded(
                id, entry.Feature, entry.ModelAsked, entry.ModelAnswered, entry.Outcome, entry.Error,
                entry.Fallback, entry.Usage, entry.KeepText));
            return Task.FromResult(id);
        }

        public Task KeepTextAsync(Guid callId, string? prompt, string? answer, CancellationToken ct) => Task.CompletedTask;

        public sealed record Recorded(
            Guid Id,
            string Feature,
            string ModelAsked,
            string? ModelAnswered,
            string Outcome,
            string? Error,
            bool Fallback,
            ChatTokenUsage? Usage,
            bool KeepText);
    }

    private sealed class FakeUsage : IAiUsage
    {
        public List<(string Feature, string Model)> Recorded { get; } = [];

        public Task RecordAsync(string feature, Guid? userId, string model, string? provider, ChatTokenUsage? usage, CancellationToken ct)
        {
            if (usage is not null)
                Recorded.Add((feature, model));

            return Task.CompletedTask;
        }

        public Task<AiLimitReached?> LimitReachedAsync(string feature, CancellationToken ct)
            => Task.FromResult<AiLimitReached?>(null);
    }
}
