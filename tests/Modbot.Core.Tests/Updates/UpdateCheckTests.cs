using System.Net;
using System.Text;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Tests.Data;
using Modbot.Core.Updates;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Updates;

/// <summary>
/// A server asking what the newest release is, and what it does with the answer
/// (update checking design §5, §6).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class UpdateCheckTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A release later than whatever this build calls itself.</summary>
    private static string Later()
    {
        var (year, month, patch) = ReleaseVersion.Parse(ModbotVersion.Release)!.Value;
        return $"{year}.{month}.{patch + 1}";
    }

    private static string Answer(string version, string image = "modbot/modbot") =>
        $$"""
        {"name":"server","version":"{{version}}","publishedAt":"2026-09-18T08:00:00+00:00",
         "notesUrl":"https://github.com/binn/Modbot/releases/tag/host-v{{version}}",
         "image":"{{image}}","tag":"{{version}}"}
        """;

    [Fact]
    public async Task ANewerReleaseIsRecordedWithTheImageToPull()
    {
        var newest = Later();
        var cloud = new StubCloud(HttpStatusCode.OK, Answer(newest));

        await using var context = await FreshAsync();
        var checker = new UpdateChecker(context, Client(cloud), new FakeClock(Now));

        var status = await checker.CheckAsync(Ct);

        Assert.True(status.NewerAvailable);
        Assert.Equal(newest, status.Newest);
        Assert.Equal(ModbotVersion.Release, status.Running);
        Assert.Equal("modbot/modbot", status.Image);
        Assert.Equal(newest, status.Tag);
        Assert.Equal(Now, status.CheckedAt);
        Assert.Null(status.Problem);

        // Written down, so the settings screen can read it without asking anybody.
        var settings = await context.GetSettingsAsync(Ct);
        Assert.Equal(newest, settings.NewestRelease);
        Assert.Equal("modbot/modbot", settings.NewestReleaseImage);
    }

    [Fact]
    public async Task TheSameReleaseIsNotAnUpdate()
    {
        var cloud = new StubCloud(HttpStatusCode.OK, Answer(ModbotVersion.Release));

        await using var context = await FreshAsync();
        var checker = new UpdateChecker(context, Client(cloud), new FakeClock(Now));

        var status = await checker.CheckAsync(Ct);

        Assert.False(status.NewerAvailable);
        Assert.Equal(ModbotVersion.Release, status.Newest);
    }

    /// <summary>
    /// Running something later than the newest published release — a build from master — must not
    /// be told to "update" backwards.
    /// </summary>
    [Fact]
    public async Task SomethingNewerLocallyIsNotAnUpdate()
    {
        var (year, month, patch) = ReleaseVersion.Parse(ModbotVersion.Release)!.Value;
        var older = patch > 0 ? $"{year}.{month}.{patch - 1}" : $"{year - 1}.{month}.0";

        var cloud = new StubCloud(HttpStatusCode.OK, Answer(older));

        await using var context = await FreshAsync();
        var checker = new UpdateChecker(context, Client(cloud), new FakeClock(Now));

        var status = await checker.CheckAsync(Ct);

        Assert.False(status.NewerAvailable);
        Assert.Equal(older, status.Newest);
    }

    /// <summary>
    /// "We could not ask today" is not "there is no newer version", and the screen must not say the
    /// second when it means the first.
    /// </summary>
    [Fact]
    public async Task AFailedCheckKeepsWhatWasLastKnown()
    {
        var newest = Later();

        await using var context = await FreshAsync();

        var found = await new UpdateChecker(context, Client(new StubCloud(HttpStatusCode.OK, Answer(newest))), new FakeClock(Now))
            .CheckAsync(Ct);
        Assert.Equal(newest, found.Newest);

        var later = Now.AddHours(6);
        var failed = await new UpdateChecker(
                context,
                Client(new StubCloud(HttpStatusCode.ServiceUnavailable, "")),
                new FakeClock(later))
            .CheckAsync(Ct);

        Assert.Equal(newest, failed.Newest);
        Assert.True(failed.NewerAvailable);
        Assert.Equal(later, failed.CheckedAt);
        Assert.NotNull(failed.Problem);
    }

    /// <summary>
    /// The operator's own switch, which is the only thing that turns this off. Nothing leaves the
    /// deployment while it is off.
    /// </summary>
    [Fact]
    public async Task TheOperatorSwitchStopsTheCheck()
    {
        var cloud = new StubCloud(HttpStatusCode.OK, Answer(Later()));

        await using var context = await FreshAsync();
        var checker = new UpdateChecker(context, Client(cloud), new FakeClock(Now));

        await checker.CheckAsync(Ct);
        Assert.Equal(1, cloud.Asked);

        var off = await checker.SetAsync(false, Ct);

        Assert.False(off.On);
        Assert.Null(off.Newest);
        Assert.False(off.NewerAvailable);

        var again = await checker.CheckAsync(Ct);

        Assert.Equal(1, cloud.Asked);
        Assert.False(again.On);
        Assert.Null(again.Newest);
    }

    [Fact]
    public async Task TurningItBackOnAsksAgain()
    {
        var newest = Later();
        var cloud = new StubCloud(HttpStatusCode.OK, Answer(newest));

        await using var context = await FreshAsync();
        var checker = new UpdateChecker(context, Client(cloud), new FakeClock(Now));

        await checker.SetAsync(false, Ct);
        await checker.CheckAsync(Ct);
        Assert.Equal(0, cloud.Asked);

        await checker.SetAsync(true, Ct);
        var status = await checker.CheckAsync(Ct);

        Assert.Equal(1, cloud.Asked);
        Assert.Equal(newest, status.Newest);
    }

    /// <summary>
    /// MODBOT_CLOUD_DISABLED turns off the Cloud features. Update checking is not one of them, and
    /// this is the test that says so end to end.
    /// </summary>
    [Fact]
    public async Task TheCheckStillRunsWithTheCloudFeaturesTurnedOff()
    {
        var environment = ModbotEnvironment.Read(new Dictionary<string, string?>
        {
            ["MODBOT_CLOUD_DISABLED"] = "1",
        });

        Assert.True(ModbotCloudAddress.From(environment).Disabled);

        var newest = Later();
        var cloud = new StubCloud(HttpStatusCode.OK, Answer(newest));
        var client = new UpdateCheckClient(ModbotUpdateAddress.From(environment), new OneClient(cloud));

        await using var context = await FreshAsync();
        var status = await new UpdateChecker(context, client, new FakeClock(Now)).CheckAsync(Ct);

        Assert.Equal(1, cloud.Asked);
        Assert.Equal(newest, status.Newest);
        Assert.True(status.NewerAvailable);
    }

    [Fact]
    public async Task NothingAboutThisDeploymentIsSent()
    {
        var cloud = new StubCloud(HttpStatusCode.OK, Answer(Later()));

        await using var context = await FreshAsync();
        await new UpdateChecker(context, Client(cloud), new FakeClock(Now)).CheckAsync(Ct);

        var asked = Assert.Single(cloud.Requests);

        Assert.Equal(HttpMethod.Get, asked.Method);
        Assert.Null(asked.Content);
        Assert.Null(asked.Authorization);
        Assert.Equal("/api/v1/updates/server", asked.Path);
        Assert.Equal("", asked.Query);
    }

    /// <summary>
    /// A context over a settings row with nothing learned yet. The container is shared by the whole
    /// assembly and the settings row is a singleton, so each test puts it back rather than
    /// inheriting whatever the last one wrote.
    /// </summary>
    private async Task<ModbotContext> FreshAsync()
    {
        var context = db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);

        settings.CheckForUpdates = true;
        settings.NewestRelease = null;
        settings.NewestReleaseAt = null;
        settings.NewestReleaseNotesUrl = null;
        settings.NewestReleaseImage = null;
        settings.NewestReleaseTag = null;
        settings.UpdateCheckedAt = null;
        settings.UpdateCheckProblem = null;

        await context.SaveChangesAsync(Ct);
        return context;
    }

    private static UpdateCheckClient Client(StubCloud cloud) =>
        new(ModbotUpdateAddress.Default, new OneClient(cloud));

    /// <summary>Answers with a canned body and remembers exactly what was asked.</summary>
    private sealed class StubCloud(HttpStatusCode status, string body) : HttpMessageHandler
    {
        private readonly List<(HttpMethod Method, string Path, string Query, string? Authorization, string? Content)> _requests = [];

        public int Asked => _requests.Count;

        public IReadOnlyList<(HttpMethod Method, string Path, string Query, string? Authorization, string? Content)> Requests
            => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _requests.Add((
                request.Method,
                request.RequestUri?.AbsolutePath ?? "",
                request.RequestUri?.Query ?? "",
                request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>An <see cref="IHttpClientFactory"/> that hands out one stubbed client.</summary>
    private sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
