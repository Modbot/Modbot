using System.Net;
using System.Text;
using Modbot.Companion.Feedback;
using Modbot.Companion.Ingest;

namespace Modbot.Companion.Tests.Feedback;

/// <summary>The three requests Feedback makes to the paired server, and what each answer means.</summary>
public class HttpFeedbackClientTests
{
    private static readonly ServerPairing Pairing = new("cats", new Uri("https://modbot.example"), "token", "grp_cats");

    [Fact]
    public async Task AvailabilityIsWhatTheServerSaysAndUnknownWhenItCannotBeAsked()
    {
        Assert.True(await Client(HttpStatusCode.OK, "{\"available\":true,\"ai\":false}").GetAvailableAsync(Pairing, TestContext.Current.CancellationToken));
        Assert.False(await Client(HttpStatusCode.OK, "{\"available\":false,\"ai\":true}").GetAvailableAsync(Pairing, TestContext.Current.CancellationToken));
        Assert.False(await Client(HttpStatusCode.Forbidden, "{\"code\":\"feedback_not_allowed\"}").GetAvailableAsync(Pairing, TestContext.Current.CancellationToken));
        Assert.False(await Client(HttpStatusCode.Unauthorized, string.Empty).GetAvailableAsync(Pairing, TestContext.Current.CancellationToken));
        Assert.Null(await Client(HttpStatusCode.BadGateway, string.Empty).GetAvailableAsync(Pairing, TestContext.Current.CancellationToken));
        Assert.Null(await new HttpFeedbackClient(new HttpClient(new Handler(new HttpRequestException("no route")))).GetAvailableAsync(Pairing, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AvailabilityIgnoresWhetherAIIsOn()
    {
        var withAi = await Client(HttpStatusCode.OK, "{\"available\":true,\"ai\":true}").GetAvailableAsync(Pairing, TestContext.Current.CancellationToken);
        var withoutAi = await Client(HttpStatusCode.OK, "{\"available\":true,\"ai\":false}").GetAvailableAsync(Pairing, TestContext.Current.CancellationToken);

        Assert.True(withAi);
        Assert.True(withoutAi);
    }

    [Fact]
    public async Task TheTidiedDraftCarriesTheKindTitleAndDetailsTheServerGave()
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"kind\":\"Bug\",\"title\":\"Map will not open\",\"details\":\"The map does not open.\",\"tidied\":true}");

        var draft = await new HttpFeedbackClient(new HttpClient(handler)).TidyAsync(Pairing, "the map is broken", TestContext.Current.CancellationToken);

        Assert.Equal(new FeedbackDraft("bug", "Map will not open", "The map does not open.", true), draft);
        Assert.Equal("Bearer token", handler.Request!.Headers.Authorization!.ToString());
        Assert.Equal("/api/v1/companion/feedback/tidy", handler.Request.RequestUri!.AbsolutePath);
        Assert.Contains("the map is broken", handler.Body);
    }

    [Fact]
    public async Task AnAnswerWithNoKindLeavesTheKindForThePersonToPick()
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"kind\":null,\"title\":null,\"details\":\"the map is broken\",\"tidied\":false}");

        var draft = await new HttpFeedbackClient(new HttpClient(handler)).TidyAsync(Pairing, "the map is broken", TestContext.Current.CancellationToken);

        Assert.Equal(new FeedbackDraft(null, null, "the map is broken", false), draft);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ATidyThatIsRefusedOrFailsGivesNothingSoTheWordsAreKept(HttpStatusCode status)
    {
        var draft = await Client(status, string.Empty).TidyAsync(Pairing, "the map is broken", TestContext.Current.CancellationToken);

        Assert.Null(draft);
    }

    [Fact]
    public async Task SendingPostsTheKindTitleAndDetailsToTheFeedbackRoute()
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"id\":\"3f1c\"}");

        var result = await new HttpFeedbackClient(new HttpClient(handler)).SendAsync(Pairing, "idea", "A better map", "Make it bigger.", TestContext.Current.CancellationToken);

        Assert.Null(result.Problem);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/api/v1/companion/feedback", handler.Request.RequestUri!.AbsolutePath);
        Assert.Contains("\"kind\":\"idea\"", handler.Body);
        Assert.Contains("\"title\":\"A better map\"", handler.Body);
        Assert.Contains("\"details\":\"Make it bigger.\"", handler.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, HttpFeedbackClient.NotAllowed)]
    [InlineData(HttpStatusCode.BadRequest, HttpFeedbackClient.TooLong)]
    [InlineData(HttpStatusCode.InternalServerError, HttpFeedbackClient.CouldNotSend)]
    public async Task EachRefusalIsOneShortLine(HttpStatusCode status, string line)
    {
        var result = await Client(status, string.Empty).SendAsync(Pairing, "bug", "", "Broken.", TestContext.Current.CancellationToken);

        Assert.Equal(line, result.Problem);
    }

    [Fact]
    public async Task AServerThatCannotBeReachedIsOneShortLine()
    {
        var client = new HttpFeedbackClient(new HttpClient(new Handler(new HttpRequestException("no route"))));

        var result = await client.SendAsync(Pairing, "bug", "", "Broken.", TestContext.Current.CancellationToken);

        Assert.Equal(HttpFeedbackClient.CouldNotSend, result.Problem);
    }

    [Fact]
    public void TheEndpointsAreUnderTheCompanionRoute()
    {
        Assert.Equal("/api/v1/companion/feedback", Pairing.FeedbackEndpoint().AbsolutePath);
        Assert.Equal("/api/v1/companion/feedback/tidy", Pairing.FeedbackTidyEndpoint().AbsolutePath);
    }

    private static HttpFeedbackClient Client(HttpStatusCode status, string body)
        => new(new HttpClient(new Handler(status, body)));

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

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;

            if (request.Content is not null)
                Body = await request.Content.ReadAsStringAsync(cancellationToken);

            if (_throws is not null)
                throw _throws;

            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8) };
        }
    }
}
