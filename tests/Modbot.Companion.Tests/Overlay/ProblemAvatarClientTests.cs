using System.Net;
using System.Text;
using Modbot.Companion.Ingest;
using Modbot.Companion.Overlay;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>Mark avatar over HTTP: what is sent, and how each answer reads.</summary>
public class ProblemAvatarClientTests
{
    private static readonly ServerPairing Pairing = new("cats", new Uri("https://modbot.example"), "token", "grp_cats");

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task OnlyThePersonsIdIsSentWithTheDeviceToken()
    {
        var handler = new Handler(HttpStatusCode.OK, "{}");

        var result = await new HttpProblemAvatarClient(new HttpClient(handler)).MarkAsync(Pairing, "usr_kai", TestContext.Current.CancellationToken);

        Assert.Equal(MarkAvatarOutcome.Done, result.Outcome);
        Assert.Equal("Bearer token", handler.Request!.Headers.Authorization!.ToString());
        Assert.Equal(HttpMethod.Post, handler.Request.Method);
        Assert.Equal("/api/v1/companion/problem-avatars", handler.Request.RequestUri!.AbsolutePath);
        Assert.Equal("{\"vrchatUserId\":\"usr_kai\"}", handler.Body);
    }

    [Fact]
    public async Task AForbiddenAnswerIsARefusalInTheServersWords()
    {
        var handler = new Handler(HttpStatusCode.Forbidden, "{\"code\":\"forbidden\",\"message\":\"You cannot manage flag rules.\"}");

        var result = await new HttpProblemAvatarClient(new HttpClient(handler)).MarkAsync(Pairing, "usr_kai", TestContext.Current.CancellationToken);

        Assert.Equal(MarkAvatarOutcome.Refused, result.Outcome);
        Assert.Equal("You cannot manage flag rules.", result.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, MarkAvatarOutcome.Unauthorised)]
    [InlineData(HttpStatusCode.InternalServerError, MarkAvatarOutcome.Unreachable)]
    public async Task OtherAnswersAreTheTokenGoneOrTheServerNotThere(HttpStatusCode status, MarkAvatarOutcome expected)
    {
        var result = await new HttpProblemAvatarClient(new HttpClient(new Handler(status, "{}"))).MarkAsync(Pairing, "usr_kai", TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public async Task AMadeUpPersonIsNeverSent()
    {
        var handler = new Handler(HttpStatusCode.OK, "{}");

        var result = await new HttpProblemAvatarClient(new HttpClient(handler)).MarkAsync(Pairing, TestPeople.Prefix + "rin", TestContext.Current.CancellationToken);

        Assert.Equal(MarkAvatarOutcome.Refused, result.Outcome);
        Assert.Null(handler.Request);
    }
}
