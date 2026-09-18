using System.Net;
using System.Net.Http.Headers;
using Modbot.VRChat.Files;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Session;
using Modbot.VRChat.Tests.Fakes;
using NSubstitute;
using VRChat.API.Client;

namespace Modbot.VRChat.Tests.Gate;

/// <summary>
/// Fetching a picture from VRChat (VRChat files design): on the session, following VRChat's
/// redirect to its delivery host and nowhere else, and drawing from no budget at all.
/// </summary>
public class VRChatGateFileTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Uri Stored = new("https://api.vrchat.cloud/api/1/file/file_abc/1/file");

    [Fact]
    public async Task ThePictureComesBackWithTheTypeVRChatGaveIt()
    {
        var handler = new ScriptedHandler(Answer.Picture([1, 2, 3], "image/png"));
        var gate = NewGate(handler, out _);

        var result = await gate.FetchFileAsync(Stored, Ct);

        Assert.Equal(VRChatFileOutcome.Fetched, result.Outcome);
        Assert.Equal([1, 2, 3], result.File!.Bytes);
        Assert.Equal("image/png", result.File.ContentType);

        // Modbot's own User-Agent, because the request is Modbot's.
        var sent = Assert.Single(handler.Sent);
        Assert.Equal("Modbot/test test@example.com", sent.UserAgent);
    }

    /// <summary>
    /// The stored address answers with a redirect to whichever delivery host VRChat is using.
    /// Modbot follows it itself rather than letting the handler do it, so that every hop is
    /// checked before it is sent.
    /// </summary>
    [Fact]
    public async Task ARedirectToVRChatsDeliveryHostIsFollowed()
    {
        var handler = new ScriptedHandler(
            Answer.RedirectTo("https://d348imysud55la.vrchat.cloud/file_abc.png"),
            Answer.Picture([9], "image/jpeg"));

        var gate = NewGate(handler, out _);

        var result = await gate.FetchFileAsync(Stored, Ct);

        Assert.Equal(VRChatFileOutcome.Fetched, result.Outcome);
        Assert.Equal("image/jpeg", result.File!.ContentType);
        Assert.Equal(2, handler.Sent.Count);
        Assert.Equal("https://d348imysud55la.vrchat.cloud/file_abc.png", handler.Sent[1].Url);
    }

    /// <summary>
    /// <strong>The redirect target is checked the same way the first address is.</strong> An
    /// answer that points somewhere else is where a file proxy becomes a fetcher for the
    /// internet, so the second hop is never sent at all.
    /// </summary>
    [Fact]
    public async Task ARedirectAnywhereElseIsNotFollowed()
    {
        var handler = new ScriptedHandler(
            Answer.RedirectTo("https://169.254.169.254/latest/meta-data/"),
            Answer.Picture([9], "image/png"));

        var gate = NewGate(handler, out _);

        var result = await gate.FetchFileAsync(Stored, Ct);

        Assert.Equal(VRChatFileOutcome.NotVRChatAddress, result.Outcome);
        Assert.Null(result.File);

        // Only the first hop went out.
        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task AnAddressThatIsNotVRChatsIsNeverSent()
    {
        var handler = new ScriptedHandler(Answer.Picture([1], "image/png"));
        var gate = NewGate(handler, out _);

        var result = await gate.FetchFileAsync(new Uri("https://example.com/x.png"), Ct);

        Assert.Equal(VRChatFileOutcome.NotVRChatAddress, result.Outcome);
        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task SomethingThatIsNotAPictureIsRefused()
    {
        var handler = new ScriptedHandler(Answer.Picture("<html></html>"u8.ToArray(), "text/html"));
        var gate = NewGate(handler, out _);

        var result = await gate.FetchFileAsync(Stored, Ct);

        Assert.Equal(VRChatFileOutcome.NotShowable, result.Outcome);
    }

    /// <summary>
    /// The cap is enforced on the bytes as they arrive, not on the length the server claimed, so
    /// a body that keeps coming after its Content-Length still stops.
    /// </summary>
    [Fact]
    public async Task AFileLargerThanTheCapIsRefused()
    {
        var handler = new ScriptedHandler(
            Answer.Picture(new byte[VRChatFiles.MaxBytes + 1], "image/png", declareLength: false));

        var gate = NewGate(handler, out _);

        var result = await gate.FetchFileAsync(Stored, Ct);

        Assert.Equal(VRChatFileOutcome.TooBig, result.Outcome);
        Assert.Null(result.File);
    }

    [Fact]
    public async Task AFileVRChatDoesNotHaveIsNotFound()
    {
        var handler = new ScriptedHandler(new Answer(HttpStatusCode.NotFound, [], "application/json", null, true));
        var gate = NewGate(handler, out _);

        Assert.Equal(VRChatFileOutcome.NotFound, (await gate.FetchFileAsync(Stored, Ct)).Outcome);
    }

    /// <summary>
    /// <strong>No bucket, no lease, no cold stop.</strong> VRChat does not rate limit its file
    /// and image addresses (confirmed 2026-09-17), and a member list of forty faces must not
    /// queue behind itself. A picture fetch therefore spends nothing the limiter knows about.
    /// </summary>
    [Fact]
    public async Task APictureFetchDrawsFromNoBudget()
    {
        var handler = new ScriptedHandler(Answer.Picture([1], "image/png"));
        var gate = NewGate(handler, out var harness);

        for (var i = 0; i < 20; i++)
            Assert.Equal(VRChatFileOutcome.Fetched, (await gate.FetchFileAsync(Stored, Ct)).Outcome);

        Assert.Equal(20, handler.Sent.Count);

        // Nothing the limiter knows about is named for files, because nothing asked it for one.
        var buckets = await harness.Limiter.DescribeAsync(Ct);
        Assert.DoesNotContain(buckets, b => b.Name.Contains("file", StringComparison.Ordinal));
    }

    /// <summary>A deployment with no VRChat account fetches nothing, and says so plainly.</summary>
    [Fact]
    public async Task WithNoAccountThereIsNoSession()
    {
        var handler = new ScriptedHandler(Answer.Picture([1], "image/png"));
        var vrchat = new FakeVRChat();
        WireHttp(vrchat, handler);

        var harness = new LimiterHarness();
        var gate = new VRChatGate(
            new FakeClientFactory(vrchat.Client),
            new FakeConnectionStore(new VRChatConnection()),
            harness.Limiter, harness.Clock, new FakeMonotonicClock());

        var result = await gate.FetchFileAsync(Stored, Ct);

        Assert.Equal(VRChatFileOutcome.NoSession, result.Outcome);
        Assert.Empty(handler.Sent);
    }

    private static VRChatGate NewGate(ScriptedHandler handler, out LimiterHarness harness)
    {
        var vrchat = new FakeVRChat().AlwaysSignedInAs();
        WireHttp(vrchat, handler);

        harness = new LimiterHarness();

        return new VRChatGate(
            new FakeClientFactory(vrchat.Client),
            new FakeConnectionStore(new VRChatConnection("modbot@example.com", "hunter2", AuthCookie: "storedCookie")),
            harness.Limiter, harness.Clock, new FakeMonotonicClock());
    }

    private static void WireHttp(FakeVRChat vrchat, ScriptedHandler handler)
    {
        var configuration = new Configuration
        {
            BasePath = "https://api.vrchat.cloud/api/1",
            UserAgent = "Modbot/test test@example.com",
        };

        vrchat.Client.Configuration.Returns(configuration);
        vrchat.Client.HttpClient.Returns(new HttpClient(handler));
    }

    /// <param name="Repeat">True when this answer is given to every request from here on.</param>
    private sealed record Answer(
        HttpStatusCode Status,
        byte[] Body,
        string? ContentType,
        string? Location,
        bool Repeat = false)
    {
        public bool DeclareLength { get; init; } = true;

        public static Answer Picture(byte[] bytes, string contentType, bool declareLength = true) =>
            new(HttpStatusCode.OK, bytes, contentType, null, Repeat: true) { DeclareLength = declareLength };

        public static Answer RedirectTo(string location) =>
            new(HttpStatusCode.Found, [], null, location);
    }

    /// <summary>Answers each request with the next scripted answer, remembering what was asked.</summary>
    private sealed class ScriptedHandler(params Answer[] answers) : HttpMessageHandler
    {
        public List<(string Url, string? UserAgent)> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add((
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues("User-Agent", out var agents) ? string.Join(" ", agents) : null));

            var answer = Sent.Count <= answers.Length ? answers[Sent.Count - 1] : answers[^1];

            var response = new HttpResponseMessage(answer.Status)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(answer.Body),
            };

            if (answer.ContentType is not null)
                response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(answer.ContentType);

            // A body larger than the length it claims is exactly what the cap has to survive.
            if (!answer.DeclareLength)
                response.Content.Headers.ContentLength = 1;

            if (answer.Location is not null)
                response.Headers.Location = new Uri(answer.Location);

            return Task.FromResult(response);
        }
    }
}
