using System.Net;
using System.Text;
using Modbot.Companion.CloudBackup;
using Modbot.Companion.Credits;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Credits;

/// <summary>
/// The Credits page's reader: what it asks Modbot Cloud, what it keeps on the PC, and when it asks
/// nothing at all.
/// </summary>
public sealed class CloudCreditsTests : IDisposable
{
    private const string Sponsors = """
        {
          "items": [
            {
              "id": "0199a1b2-0000-7000-8000-000000000001",
              "name": "Kind Person",
              "link": "https://example.com/kind",
              "imageUrl": "https://cloud.modbot.test/api/v1/showcase-pictures/aaaa",
              "vrChatGroupId": "grp_1234",
              "groupImageUrl": "https://cloud.modbot.test/api/v1/showcase-pictures/bbbb",
              "groupBannerUrl": "https://cloud.modbot.test/api/v1/showcase-pictures/cccc"
            }
          ]
        }
        """;

    private const string EarlyAdopters = """
        {
          "items": [
            {
              "id": "0199a1b2-0000-7000-8000-000000000002",
              "name": "An early group",
              "link": "",
              "imageUrl": "",
              "vrChatGroupId": null,
              "groupImageUrl": null,
              "groupBannerUrl": null
            }
          ]
        }
        """;

    private const string Contributors = """
        { "items": [ { "login": "rin", "url": "https://github.com/rin", "avatarUrl": "https://avatars.test/rin.png", "contributions": 42 } ] }
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory("modbot-credits-").FullName;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string CacheFile => System.IO.Path.Combine(_directory, CloudCredits.FileName);

    private static CloudSettings Cloud(bool disabled = false) =>
        new(new Uri("https://cloud.modbot.test"), disabled);

    [Fact]
    public async Task ItReadsTheSponsorsTheEarlyAdoptersAndTheContributors()
    {
        var cloud = new FakeCloud();
        var credits = new CloudCredits(new HttpClient(cloud), Cloud(), CacheFile, _clock);

        await credits.RefreshAsync(Ct);

        var sponsor = Assert.Single(credits.Current.Sponsors);
        Assert.Equal("Kind Person", sponsor.Name);
        Assert.Equal("grp_1234", sponsor.VRChatGroupId);
        Assert.Equal("https://vrchat.com/home/group/grp_1234", sponsor.GroupPage);
        Assert.Equal("https://cloud.modbot.test/api/v1/showcase-pictures/cccc", sponsor.GroupBannerUrl);
        Assert.Equal("https://cloud.modbot.test/api/v1/showcase-pictures/bbbb", sponsor.GroupIconUrl);

        var early = Assert.Single(credits.Current.EarlyAdopters);
        Assert.Equal("An early group", early.Name);
        Assert.Null(early.VRChatGroupId);
        Assert.Null(early.GroupPage);

        var contributor = Assert.Single(credits.Current.Contributors);
        Assert.Equal("rin", contributor.Name);
        Assert.Equal("https://github.com/rin", contributor.ProfileUrl);

        // Three reads, and nothing else. No paired server is asked and no credential is sent.
        Assert.Equal(
            ["/api/v1/sponsors", "/api/v1/early-adopters", "/api/v1/contributors"],
            cloud.Asked);

        Assert.All(cloud.Credentials, header => Assert.Null(header));
    }

    [Fact]
    public async Task TheKeptCopyShowsTheNamesWhenCloudCannotBeReached()
    {
        var first = new CloudCredits(new HttpClient(new FakeCloud()), Cloud(), CacheFile, _clock);
        await first.RefreshAsync(Ct);
        Assert.Single(first.Current.Sponsors);
        Assert.True(File.Exists(CacheFile));

        // A different run of the client, a week later, with nothing answering.
        _clock.Advance(TimeSpan.FromDays(7));

        var refusing = new FakeCloud { Answer = HttpStatusCode.ServiceUnavailable };
        var later = new CloudCredits(new HttpClient(refusing), Cloud(), CacheFile, _clock);

        await later.RefreshAsync(Ct);

        var sponsor = Assert.Single(later.Current.Sponsors);
        Assert.Equal("Kind Person", sponsor.Name);
        Assert.Single(later.Current.Contributors);
    }

    [Fact]
    public async Task ACopyThatIsStillFreshIsNotAskedForAgain()
    {
        var cloud = new FakeCloud();
        var credits = new CloudCredits(new HttpClient(cloud), Cloud(), CacheFile, _clock);

        await credits.RefreshAsync(Ct);
        Assert.Equal(3, cloud.Asked.Count);

        _clock.Advance(CloudCredits.AskAgainAfter - TimeSpan.FromMinutes(1));
        await credits.RefreshAsync(Ct);
        Assert.Equal(3, cloud.Asked.Count);

        _clock.Advance(TimeSpan.FromMinutes(2));
        await credits.RefreshAsync(Ct);
        Assert.Equal(6, cloud.Asked.Count);
    }

    [Fact]
    public async Task WithModbotCloudTurnedOffOnThisPCNothingIsAskedAndNothingIsShown()
    {
        // The switch is this PC's: settings.json, or MODBOT_CLOUD_DISABLED. No paired server has a
        // say in it, and turning Cloud off has to mean no request rather than a quiet one.
        var cloud = new FakeCloud();
        var credits = new CloudCredits(new HttpClient(cloud), Cloud(disabled: true), CacheFile, _clock);

        await credits.RefreshAsync(Ct);

        Assert.False(credits.Allowed);
        Assert.True(credits.Current.IsEmpty);
        Assert.Empty(cloud.Asked);
        Assert.False(File.Exists(CacheFile));
    }

    [Fact]
    public async Task AKeptCopyIsIgnoredWhenCloudIsTurnedOff()
    {
        var first = new CloudCredits(new HttpClient(new FakeCloud()), Cloud(), CacheFile, _clock);
        await first.RefreshAsync(Ct);
        Assert.True(File.Exists(CacheFile));

        var off = new CloudCredits(new HttpClient(new FakeCloud()), Cloud(disabled: true), CacheFile, _clock);
        await off.RefreshAsync(Ct);

        Assert.True(off.Current.IsEmpty);
    }

    [Fact]
    public async Task AnAnswerThatIsNotJsonLeavesTheListsAlone()
    {
        var credits = new CloudCredits(
            new HttpClient(new FakeCloud { Body = "not json at all" }), Cloud(), CacheFile, _clock);

        await credits.RefreshAsync(Ct);

        Assert.True(credits.Current.IsEmpty);
        Assert.False(File.Exists(CacheFile));
    }

    /// <summary>A Modbot Cloud that answers the three showcase reads, and remembers what was asked.</summary>
    private sealed class FakeCloud : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        public List<string?> Credentials { get; } = [];

        public HttpStatusCode Answer { get; set; } = HttpStatusCode.OK;

        /// <summary>When set, every read answers this instead of the lists.</summary>
        public string? Body { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            Asked.Add(path);
            Credentials.Add(request.Headers.Authorization?.ToString());

            if (Answer != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(Answer));

            var body = Body ?? path switch
            {
                "/api/v1/sponsors" => Sponsors,
                "/api/v1/early-adopters" => EarlyAdopters,
                "/api/v1/contributors" => Contributors,
                _ => null,
            };

            if (body is null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
