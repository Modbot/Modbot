using System.Net;
using System.Text;
using Modbot.Companion.Chat;
using Modbot.Companion.Ingest;

namespace Modbot.Companion.Tests.Chat;

/// <summary>Reading the server's stream of events, and turning a reply or a refusal into what the panel shows.</summary>
public class ChatClientTests
{
    private static readonly ServerPairing Pairing = new("cats", new Uri("https://modbot.example"), "token", "grp_cats");

    [Fact]
    public async Task EventsAreReadOneBlockAtATime()
    {
        var stream = "event: conversation\ndata: {\"id\":1}\n\n: keep-alive\n\nevent: text\ndata: {\"text\":\"Hi\"}\n\ndata: plain\n\n";

        var read = new List<ServerEvent>();
        await foreach (var e in ServerEvents.ReadAsync(new StringReader(stream), TestContext.Current.CancellationToken))
            read.Add(e);

        Assert.Equal(
            [
                new ServerEvent("conversation", "{\"id\":1}"),
                new ServerEvent("text", "{\"text\":\"Hi\"}"),
                new ServerEvent("message", "plain"),
            ],
            read);
    }

    [Fact]
    public async Task ALastBlockWithNoBlankLineAfterItStillCounts()
    {
        var read = new List<ServerEvent>();
        await foreach (var e in ServerEvents.ReadAsync(new StringReader("event: done\ndata: {}"), TestContext.Current.CancellationToken))
            read.Add(e);

        Assert.Equal([new ServerEvent("done", "{}")], read);
    }

    [Fact]
    public async Task TheAnswerGrowsAsTextArrivesAndTheConversationIdIsKept()
    {
        var id = Guid.NewGuid();
        var handler = new Handler(HttpStatusCode.OK, Stream(
            ("conversation", $"{{\"id\":\"{id}\",\"title\":\"t\"}}"),
            ("message", "{\"role\":\"user\",\"content\":\"hello\",\"toolCalls\":[]}"),
            ("text", "{\"text\":\"Hello \"}"),
            ("text", "{\"text\":\"there\"}"),
            ("message", "{\"role\":\"assistant\",\"content\":\"Hello there\",\"toolCalls\":[]}"),
            ("done", "{\"outcome\":\"Answered\",\"error\":null}")));

        var seen = new List<string>();
        var result = await new HttpChatClient(new HttpClient(handler)).SendAsync(Pairing, "hello", null, seen.Add, TestContext.Current.CancellationToken);

        Assert.Null(result.Problem);
        Assert.Equal(id, result.ConversationId);
        Assert.Equal(["Hello ", "Hello there", "Hello there"], seen);
        Assert.Equal("Bearer token", handler.Request!.Headers.Authorization!.ToString());
        Assert.Equal("/api/v1/companion/chat/messages", handler.Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task WhatTheAiWroteBeforeLookingSomethingUpIsDroppedWhenItWritesTheReply()
    {
        var handler = new Handler(HttpStatusCode.OK, Stream(
            ("text", "{\"text\":\"Let me check.\"}"),
            ("message", "{\"role\":\"assistant\",\"content\":\"Let me check.\",\"toolCalls\":[{\"id\":\"1\"}]}"),
            ("message", "{\"role\":\"tool\",\"content\":\"x\",\"toolCalls\":[]}"),
            ("text", "{\"text\":\"Two people.\"}"),
            ("message", "{\"role\":\"assistant\",\"content\":\"Two people.\",\"toolCalls\":[]}"),
            ("done", "{\"outcome\":\"Answered\"}")));

        var seen = new List<string>();
        var result = await new HttpChatClient(new HttpClient(handler)).SendAsync(Pairing, "who", null, seen.Add, TestContext.Current.CancellationToken);

        Assert.Null(result.Problem);
        Assert.Equal("Two people.", seen[^1]);
        Assert.DoesNotContain(seen, s => s.Contains("check") && s.Contains("Two"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "{\"code\":\"chat_not_allowed\"}", ChatProblems.NotAllowed)]
    [InlineData(HttpStatusCode.Unauthorized, null, ChatProblems.NotAllowed)]
    [InlineData(HttpStatusCode.TooManyRequests, "{\"error\":\"limit\"}", ChatProblems.SpendLimit)]
    [InlineData(HttpStatusCode.Conflict, "{\"error\":\"Chat is off.\"}", ChatProblems.Off)]
    [InlineData(HttpStatusCode.Conflict, "{\"error\":\"AI is not set up.\"}", ChatProblems.Off)]
    [InlineData(HttpStatusCode.Conflict, "{\"error\":\"This conversation is full. Start a new one.\"}", ChatProblems.Full)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":\"A message can be at most 4000 characters.\"}", ChatProblems.TooLong)]
    [InlineData(HttpStatusCode.InternalServerError, null, ChatProblems.Unreachable)]
    public async Task EachRefusalIsOneShortLine(HttpStatusCode status, string? body, string line)
    {
        var handler = new Handler(status, body ?? string.Empty);

        var result = await new HttpChatClient(new HttpClient(handler)).SendAsync(Pairing, "hi", null, _ => { }, TestContext.Current.CancellationToken);

        Assert.Equal(line, result.Problem);
    }

    [Fact]
    public async Task AConversationTheServerNoLongerHasIsForgotten()
    {
        var handler = new Handler(HttpStatusCode.NotFound, string.Empty);

        var result = await new HttpChatClient(new HttpClient(handler)).SendAsync(Pairing, "hi", Guid.NewGuid(), _ => { }, TestContext.Current.CancellationToken);

        Assert.True(result.ConversationGone);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public async Task AStreamThatEndsWithoutDoneIsAProblem()
    {
        var handler = new Handler(HttpStatusCode.OK, Stream(("text", "{\"text\":\"Hel\"}")));

        var result = await new HttpChatClient(new HttpClient(handler)).SendAsync(Pairing, "hi", null, _ => { }, TestContext.Current.CancellationToken);

        Assert.Equal(ChatProblems.Unreachable, result.Problem);
    }

    [Fact]
    public async Task AFailedAnswerIsOneShortLine()
    {
        var handler = new Handler(HttpStatusCode.OK, Stream(("done", "{\"outcome\":\"Failed\",\"error\":\"provider said something long\"}")));

        var result = await new HttpChatClient(new HttpClient(handler)).SendAsync(Pairing, "hi", null, _ => { }, TestContext.Current.CancellationToken);

        Assert.Equal(ChatProblems.Failed, result.Problem);
    }

    [Fact]
    public async Task AServerThatCannotBeReachedIsOneShortLine()
    {
        var handler = new Handler(new HttpRequestException("no route"));

        var result = await new HttpChatClient(new HttpClient(handler)).SendAsync(Pairing, "hi", null, _ => { }, TestContext.Current.CancellationToken);

        Assert.Equal(ChatProblems.Unreachable, result.Problem);
    }

    [Fact]
    public async Task AvailabilityIsWhatTheServerSaysAndUnknownWhenItCannotBeAsked()
    {
        var client = new HttpChatClient(new HttpClient(new Handler(HttpStatusCode.OK, "{\"available\":true}")));
        Assert.True(await client.GetAvailableAsync(Pairing, TestContext.Current.CancellationToken));

        client = new HttpChatClient(new HttpClient(new Handler(HttpStatusCode.OK, "{\"available\":false}")));
        Assert.False(await client.GetAvailableAsync(Pairing, TestContext.Current.CancellationToken));

        client = new HttpChatClient(new HttpClient(new Handler(HttpStatusCode.Forbidden, string.Empty)));
        Assert.False(await client.GetAvailableAsync(Pairing, TestContext.Current.CancellationToken));

        client = new HttpChatClient(new HttpClient(new Handler(HttpStatusCode.BadGateway, string.Empty)));
        Assert.Null(await client.GetAvailableAsync(Pairing, TestContext.Current.CancellationToken));

        client = new HttpChatClient(new HttpClient(new Handler(new HttpRequestException("no route"))));
        Assert.Null(await client.GetAvailableAsync(Pairing, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheEndpointsAreUnderTheCompanionRoute()
    {
        Assert.Equal("/api/v1/companion/chat", Pairing.ChatEndpoint().AbsolutePath);
        Assert.Equal("/api/v1/companion/chat/messages", Pairing.ChatMessagesEndpoint().AbsolutePath);
    }

    private static string Stream(params (string Name, string Data)[] events)
        => string.Concat(events.Select(e => $"event: {e.Name}\ndata: {e.Data}\n\n"));

    private sealed class Handler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Exception? _throws;

        public Handler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public Handler(Exception throws)
        {
            _throws = throws;
            _body = string.Empty;
        }

        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;

            if (_throws is not null)
                throw _throws;

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8),
            });
        }
    }
}
