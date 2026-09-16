using System.Net;
using Modbot.Api.Features.Credits;
using Modbot.Core.Configuration;
using Modbot.Core.Time;

namespace Modbot.Api.Tests.Features.Credits;

/// <summary>
/// Reading the showcase from Modbot Cloud: never when Cloud is turned off, quietly absent when it
/// cannot be reached, and asked rarely.
/// </summary>
public class CloudShowcaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed class MovingClock(DateTimeOffset now) : IModbotClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(HttpStatusCode answer) : HttpMessageHandler
    {
        public HttpStatusCode Answer { get; set; } = answer;

        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);

            return Task.FromResult(new HttpResponseMessage(Answer)
            {
                Content = new StringContent(
                    """{"items":[{"name":"Kind Person","link":"https://example.com","imageUrl":"","vrChatGroupId":"grp_1","groupImageUrl":null,"groupBannerUrl":null,"login":"someone","url":"https://github.com/someone","avatarUrl":"","contributions":3}]}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    [Fact]
    public async Task CloudTurnedOffMeansNoRequestAtAll()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler(HttpStatusCode.OK);

        var showcase = new CloudShowcase(
            new StubFactory(handler),
            new ModbotCloudAddress(new Uri("https://cloud.example"), Disabled: true),
            new MovingClock(Now));

        var read = await showcase.ReadAsync(ct);

        Assert.False(read.Available);
        Assert.Empty(read.Sponsors);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task TheThreeListsAreReadAndThenCached()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler(HttpStatusCode.OK);
        var clock = new MovingClock(Now);

        var showcase = new CloudShowcase(
            new StubFactory(handler),
            new ModbotCloudAddress(new Uri("https://cloud.example"), Disabled: false),
            clock);

        var read = await showcase.ReadAsync(ct);

        Assert.True(read.Available);
        Assert.Equal(
            ["/api/v1/contributors", "/api/v1/sponsors", "/api/v1/early-adopters"],
            handler.Paths);

        Assert.Single(read.Sponsors);
        Assert.Equal("grp_1", read.Sponsors[0].VRChatGroupId);

        // Asked once, not once per reader.
        await showcase.ReadAsync(ct);
        Assert.Equal(3, handler.Paths.Count);

        clock.Advance(CloudShowcase.CacheFor + TimeSpan.FromMinutes(1));
        await showcase.ReadAsync(ct);
        Assert.Equal(6, handler.Paths.Count);
    }

    [Fact]
    public async Task ACloudThatCannotBeReachedIsQuietlyAbsentAndNotAskedAgainAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable);
        var clock = new MovingClock(Now);

        var showcase = new CloudShowcase(
            new StubFactory(handler),
            new ModbotCloudAddress(new Uri("https://cloud.example"), Disabled: false),
            clock);

        var read = await showcase.ReadAsync(ct);

        Assert.False(read.Available);
        Assert.Empty(read.Contributors);

        var asked = handler.Paths.Count;
        await showcase.ReadAsync(ct);
        Assert.Equal(asked, handler.Paths.Count);

        // Sooner than a success would be retried, but not on every page load.
        clock.Advance(CloudShowcase.CacheFailureFor + TimeSpan.FromMinutes(1));
        handler.Answer = HttpStatusCode.OK;

        Assert.True((await showcase.ReadAsync(ct)).Available);
    }
}
