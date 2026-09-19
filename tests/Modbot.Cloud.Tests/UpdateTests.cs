using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modbot.Cloud.Features.Updates;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Modbot.Cloud.Tests;

/// <summary>
/// What Cloud tells every Modbot and every client about the newest release
/// (update checking design §3, §4).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class UpdateTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (LatestReleases Releases, FakeReleaseHost Host) Build(ManualTime time)
    {
        var host = new FakeReleaseHost();

        return (new LatestReleases(
            new GitHubReleases(new HttpClient(host, disposeHandler: false), token: null, FakeReleaseHost.Repository),
            new DockerHubTags(new HttpClient(host, disposeHandler: false), FakeReleaseHost.Image),
            time), host);
    }

    [Fact]
    public async Task TheNewestReleaseOfEachThingIsServed()
    {
        var (releases, _) = Build(new ManualTime(CloudTestHost.Start));
        Assert.True(await releases.RefreshAsync(Ct));

        await using var host = await CloudTestHost.StartAsync(db, updates: releases);

        using var response = await host.GetAsync("/api/v1/updates");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var byName = page.GetProperty("releases").EnumerateArray()
            .ToDictionary(r => r.GetProperty("name").GetString()!);

        // A draft and a pre-release are both in the fixture and neither is the answer.
        Assert.Equal(FakeReleaseHost.ServerVersion, byName["server"].GetProperty("version").GetString());

        // 2026.9.10 beats 2026.9.9, which string sorting would get backwards.
        Assert.Equal(FakeReleaseHost.ClientVersion, byName["companion"].GetProperty("version").GetString());

        // The server ships as an image, so the answer says what to pull. The client does not.
        Assert.Equal(FakeReleaseHost.Image, byName["server"].GetProperty("image").GetString());
        Assert.Equal(FakeReleaseHost.ServerVersion, byName["server"].GetProperty("tag").GetString());
        Assert.Equal(JsonValueKind.Null, byName["companion"].GetProperty("image").ValueKind);

        Assert.Contains(
            $"host-v{FakeReleaseHost.ServerVersion}",
            byName["server"].GetProperty("notesUrl").GetString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole reason the answer is held in memory: GitHub's rate limit is what a request per
    /// deployment would spend.
    /// </summary>
    [Fact]
    public async Task HoweverManyAskGitHubIsAskedOnce()
    {
        var (releases, github) = Build(new ManualTime(CloudTestHost.Start));
        Assert.True(await releases.RefreshAsync(Ct));

        await using var host = await CloudTestHost.StartAsync(db, updates: releases);

        for (var i = 0; i < 20; i++)
        {
            using var response = await host.GetAsync("/api/v1/updates/server");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(1, github.ReleasesAsked);
        Assert.Equal(1, github.TagsAsked);
    }

    /// <summary>
    /// GitHub being down must not turn into every Modbot in the world being told nothing. The last
    /// answer is hours old at worst and still true.
    /// </summary>
    [Fact]
    public async Task AFailedFetchServesTheLastAnswer()
    {
        var time = new ManualTime(CloudTestHost.Start);
        var (releases, github) = Build(time);

        Assert.True(await releases.RefreshAsync(Ct));
        var known = releases.Answer!;

        github.Refusing = true;
        time.Advance(LatestReleases.RefreshEvery);

        Assert.False(await releases.RefreshAsync(Ct));

        await using var host = await CloudTestHost.StartAsync(db, updates: releases);

        using var response = await host.GetAsync("/api/v1/updates/server");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var release = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(FakeReleaseHost.ServerVersion, release.GetProperty("version").GetString());

        // Still the answer from before the outage, not a blank one.
        Assert.Same(known, releases.Answer);
    }

    /// <summary>
    /// An empty answer would read as "you are up to date", which would be a lie. A cold cache that
    /// cannot be filled says so instead.
    /// </summary>
    [Fact]
    public async Task AColdCacheThatCannotBeFilledIsAnError()
    {
        var (releases, github) = Build(new ManualTime(CloudTestHost.Start));
        github.Refusing = true;

        Assert.False(await releases.RefreshAsync(Ct));
        Assert.Null(releases.Answer);

        await using var host = await CloudTestHost.StartAsync(db, updates: releases);

        using var all = await host.GetAsync("/api/v1/updates");
        using var server = await host.GetAsync("/api/v1/updates/server");
        using var feed = await host.GetAsync("/api/v1/updates/companion/releases.win.json");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, all.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, server.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, feed.StatusCode);
    }

    /// <summary>
    /// The feed is the file the release workflow published, with each package's name replaced by
    /// the address it downloads from — so Cloud serves a few kilobytes and GitHub serves the
    /// hundred megabytes.
    /// </summary>
    [Fact]
    public async Task TheClientFeedPointsStraightAtTheReleaseFiles()
    {
        var (releases, _) = Build(new ManualTime(CloudTestHost.Start));
        Assert.True(await releases.RefreshAsync(Ct));

        await using var host = await CloudTestHost.StartAsync(db, updates: releases);

        using var response = await host.GetAsync("/api/v1/updates/companion/releases.win.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var feed = await response.Content.ReadAsStringAsync(Ct);
        var asset = JsonDocument.Parse(feed).RootElement.GetProperty("Assets")[0];

        Assert.Equal(FakeReleaseHost.PackageDownloadUrl, asset.GetProperty("FileName").GetString());

        // Everything else is carried through untouched: the checksums are what the client verifies
        // the download against, and Cloud must not be in a position to change them.
        Assert.Equal("Modbot", asset.GetProperty("PackageId").GetString());
        Assert.Equal(FakeReleaseHost.ClientVersion, asset.GetProperty("Version").GetString());
        Assert.Equal(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            asset.GetProperty("SHA256").GetString());
    }

    /// <summary>
    /// The shape checked against the Velopack the client actually ships with, rather than against
    /// what a document says it reads.
    /// </summary>
    [Fact]
    public async Task VelopackReadsTheFeedCloudServes()
    {
        var (releases, _) = Build(new ManualTime(CloudTestHost.Start));
        Assert.True(await releases.RefreshAsync(Ct));

        await using var host = await CloudTestHost.StartAsync(db, updates: releases);

        var downloader = new CloudDownloader(host);
        var source = new SimpleWebSource("https://cloud.modbot.test/api/v1/updates/companion", downloader);

        var read = await source.GetReleaseFeed(NullVelopackLogger.Instance, "Modbot", "win");

        // The file name Velopack asked for is the one Cloud serves.
        Assert.Equal(
            "/api/v1/updates/companion/releases.win.json",
            new Uri(downloader.Asked!).AbsolutePath);

        var asset = Assert.Single(read.Assets);

        Assert.Equal("Modbot", asset.PackageId);
        Assert.Equal(FakeReleaseHost.ClientVersion, asset.Version.ToString());
        Assert.Equal(VelopackAssetType.Full, asset.Type);
        Assert.Equal(FakeReleaseHost.PackageDownloadUrl, asset.FileName);
    }

    /// <summary>
    /// The property the client documentation has always promised about checking for updates, and
    /// which moving the check to Cloud must not quietly change.
    /// </summary>
    [Fact]
    public async Task NothingAboutTheCallerIsNeeded()
    {
        var (releases, _) = Build(new ManualTime(CloudTestHost.Start));
        Assert.True(await releases.RefreshAsync(Ct));

        await using var host = await CloudTestHost.StartAsync(db, updates: releases);

        // No key, no session, no install id -- and the query string Velopack attaches is ignored.
        using var feed = await host.GetAsync(
            "/api/v1/updates/companion/releases.win.json?arch=x64&os=win&rid=win-x64&id=Modbot&localVersion=2026.9.1");
        using var server = await host.GetAsync("/api/v1/updates/server");

        Assert.Equal(HttpStatusCode.OK, feed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, server.StatusCode);
    }

    [Fact]
    public async Task AChannelNobodyPublishedIsNotFound()
    {
        var (releases, _) = Build(new ManualTime(CloudTestHost.Start));
        Assert.True(await releases.RefreshAsync(Ct));

        await using var host = await CloudTestHost.StartAsync(db, updates: releases);

        using var channel = await host.GetAsync("/api/v1/updates/companion/releases.osx.json");
        using var nonsense = await host.GetAsync("/api/v1/updates/companion/index.html");
        using var thing = await host.GetAsync("/api/v1/updates/toaster");

        Assert.Equal(HttpStatusCode.NotFound, channel.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nonsense.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, thing.StatusCode);
    }

    [Fact]
    public async Task BothChannelsTheWorkflowPublishesAreServed()
    {
        var (releases, _) = Build(new ManualTime(CloudTestHost.Start));
        Assert.True(await releases.RefreshAsync(Ct));

        await using var host = await CloudTestHost.StartAsync(db, updates: releases);

        using var windows = await host.GetAsync("/api/v1/updates/companion/releases.win.json");
        using var linux = await host.GetAsync("/api/v1/updates/companion/releases.linux.json");

        Assert.Equal(HttpStatusCode.OK, windows.StatusCode);
        Assert.Equal(HttpStatusCode.OK, linux.StatusCode);
    }

    /// <summary>
    /// A copy installed from a preview asks for the preview channel for the rest of its life, so
    /// Cloud has to answer for that channel. Otherwise the handful of people most likely to find a
    /// bug are the only ones who cannot be sent the fix.
    /// </summary>
    [Fact]
    public async Task APreviewIsServedOnItsOwnChannel()
    {
        var (releases, _) = Build(new ManualTime(CloudTestHost.Start));
        Assert.True(await releases.RefreshAsync(Ct));

        await using var host = await CloudTestHost.StartAsync(db, updates: releases);

        using var windows = await host.GetAsync("/api/v1/updates/companion/releases.win-preview.json");
        using var linux = await host.GetAsync("/api/v1/updates/companion/releases.linux-preview.json");

        Assert.Equal(HttpStatusCode.OK, windows.StatusCode);
        Assert.Equal(HttpStatusCode.OK, linux.StatusCode);

        var feed = await windows.Content.ReadAsStringAsync(Ct);
        var asset = JsonDocument.Parse(feed).RootElement.GetProperty("Assets")[0];

        Assert.Equal(FakeReleaseHost.PreviewVersion, asset.GetProperty("Version").GetString());
        Assert.Equal(FakeReleaseHost.PreviewPackageDownloadUrl, asset.GetProperty("FileName").GetString());
    }

    /// <summary>
    /// The failure worth designing against: a preview is a build nobody has tried, and it must
    /// never reach somebody who did not ask for one. It is not the version deployments are told
    /// about, and it cannot answer for the channel released clients read — even though this
    /// fixture's preview is numbered higher than the release and attaches a releases.win.json of
    /// its own.
    /// </summary>
    [Fact]
    public async Task APreviewIsNotOfferedToAnybodyRunningARelease()
    {
        var (releases, _) = Build(new ManualTime(CloudTestHost.Start));
        Assert.True(await releases.RefreshAsync(Ct));

        await using var host = await CloudTestHost.StartAsync(db, updates: releases);

        using var newest = await host.GetAsync("/api/v1/updates/companion");
        var client = await newest.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(FakeReleaseHost.ClientVersion, client.GetProperty("version").GetString());

        using var windows = await host.GetAsync("/api/v1/updates/companion/releases.win.json");
        var feed = await windows.Content.ReadAsStringAsync(Ct);
        var asset = JsonDocument.Parse(feed).RootElement.GetProperty("Assets")[0];

        Assert.Equal(FakeReleaseHost.ClientVersion, asset.GetProperty("Version").GetString());
        Assert.Equal(FakeReleaseHost.PackageDownloadUrl, asset.GetProperty("FileName").GetString());
    }

    /// <summary>Reads through the test server, so Velopack talks to the real endpoint.</summary>
    private sealed class CloudDownloader(CloudTestHost host) : IFileDownloader
    {
        public string? Asked { get; private set; }

        public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            Asked = url;

            using var response = await host.GetAsync(new Uri(url).PathAndQuery);
            return await response.Content.ReadAsStringAsync(Ct);
        }

        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
            throw new NotSupportedException("The test never downloads a package.");

        public Task DownloadFile(
            string url,
            string targetFile,
            Action<int> progress,
            IDictionary<string, string>? headers = null,
            double timeout = 30,
            CancellationToken cancelToken = default) =>
            throw new NotSupportedException("The test never downloads a package.");
    }
}
