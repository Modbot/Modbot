using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Modbot.AI.Moderation;
using Modbot.Core.Moderation;
using OpenAI;

namespace Modbot.AI.Tests.Moderation;

/// <summary>AI topics against a scripted endpoint (AI moderation design §4.2). Nothing reaches a real provider.</summary>
public class TopicClassifierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TopicToCheck Scams = new("t1", Guid.NewGuid(), "Scams", "Offers of free things that ask for a login.", "medium");
    private static readonly TopicToCheck Politics = new("t2", Guid.NewGuid(), "Politics", "Campaigning.", "low");

    [Fact]
    public void AQuoteThatIsNotInTheTextIsThrownAway()
    {
        const string text = "Free nitro, just log in at this site";

        var check = TopicClassifier.Read(
            """
            {"matches":[
              {"topic":"t1","why":"Offers free Nitro for a login.","quote":"free NITRO, just log in"},
              {"topic":"t2","why":"Made up.","quote":"vote for me"},
              {"topic":"t9","why":"Not a topic.","quote":"Free"}
            ]}
            """,
            [Scams, Politics],
            text);

        Assert.Null(check.Error);
        var hit = Assert.Single(check.Hits);
        Assert.Equal("t1", hit.Topic.Key);
        Assert.Equal("Free nitro, just log in", hit.Quote);
    }

    [Fact]
    public void AnAnswerThatIsNotTheExpectedJsonIsAnError()
    {
        var check = TopicClassifier.Read("I think it is fine.", [Scams], "hello");

        Assert.Empty(check.Hits);
        Assert.NotNull(check.Error);
    }

    [Fact]
    public async Task OneRequestCarriesEveryTopic_AsksForStructuredOutput_AndFencesTheText()
    {
        var reply = JsonSerializer.Serialize(new
        {
            matches = new[] { new { topic = "t1", why = "Free Nitro scam.", quote = "free nitro" } },
        });

        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Completion(reply), Encoding.UTF8, "application/json"),
        });

        var chat = Chat(handler);

        var check = await TopicClassifier.CheckAsync(chat, [Scams, Politics], "get free nitro here", ModerationTargets.DiscordMessage, Ct);

        Assert.Null(check.Error);
        Assert.Equal("free nitro", Assert.Single(check.Hits).Quote);
        Assert.Equal("m", check.Model);
        Assert.Equal(40, check.Usage!.InputTokenCount);
        Assert.Equal(9, check.Usage.OutputTokenCount);

        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal("json_schema", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());

        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        var user = messages[1].GetProperty("content").GetString()!;
        Assert.Contains("key t1: Scams", user, StringComparison.Ordinal);
        Assert.Contains("key t2: Politics", user, StringComparison.Ordinal);
        Assert.Contains("====\nget free nitro here", user.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEndpointErrorIsReportedNotThrown()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":{"message":"bad key"}}""", Encoding.UTF8, "application/json"),
        });

        var check = await TopicClassifier.CheckAsync(Chat(handler), [Scams], "hello", ModerationTargets.Bio, Ct);

        Assert.Empty(check.Hits);
        Assert.Equal("The AI endpoint answered 401.", check.Error);
    }

    private static AiChat Chat(HttpMessageHandler handler)
    {
        var client = new OpenAIClient(new ApiKeyCredential("k"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://llm.example/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        });

        return new AiChat(client.GetChatClient("m"), client, "m", "custom");
    }

    private static string Completion(string text) => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-1",
        @object = "chat.completion",
        created = 1_700_000_000,
        model = "m",
        choices = new[]
        {
            new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = text } },
        },
        usage = new { prompt_tokens = 40, completion_tokens = 9, total_tokens = 49 },
    });
}
