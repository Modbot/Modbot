using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Landing.Configuration;
using Modbot.Landing.Features.Rooms;

namespace Modbot.Landing.Tests;

/// <summary>
/// The app that ships, built through <see cref="LandingApp"/> over a test server and a web root
/// written by the test in place of the Vite build.
/// </summary>
public sealed class LandingTestHost : IAsyncDisposable
{
    public const string ThemeScript = "document.documentElement.classList.add('dark')";

    public const string LandingHtml =
        "<!doctype html><html><head><title>Modbot</title><script>" + ThemeScript + "</script>"
        + "<script type=\"module\" src=\"/assets/app-test.js\"></script></head>"
        + "<body><main>Moderation for VRChat groups</main>"
        + "<a href=\"https://my.modbot.co/go\">Open my server</a></body></html>";

    public const string NotFoundHtml =
        "<!doctype html><html><head><title>Not found</title></head><body><main>Nothing here</main></body></html>";

    public const string PrivacyHtml =
        "<!doctype html><html><head><title>Privacy policy</title></head><body><article>What we keep</article></body></html>";

    public const string RoomsHtml =
        "<!doctype html><html><head><title>Open rooms</title></head>"
        + "<body><div id=\"root\"><main>Open rooms</main></div></body></html>";

    public const string AssetPath = "/assets/app-test.js";

    public const string CloudUrl = "https://cloud.test.invalid";

    public const string CloudApiKey = "a-rooms-key-for-tests-only";

    public static readonly DateTimeOffset Start = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly WebApplication _app;
    private readonly HttpClient _client;
    private readonly DirectoryInfo _webRoot;

    private LandingTestHost(
        WebApplication app,
        HttpClient client,
        DirectoryInfo webRoot,
        ManualTime time,
        FakeCloud cloud)
    {
        _app = app;
        _client = client;
        _webRoot = webRoot;
        Time = time;
        Cloud = cloud;
    }

    public ManualTime Time { get; }

    /// <summary>The Cloud this site reads from: what it answers, and every request it was sent.</summary>
    public FakeCloud Cloud { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <param name="built">False starts a server whose web root holds nothing, as before a build.</param>
    /// <param name="privacy">True adds the privacy page, as a build does once PRIVACY_POLICY.md exists.</param>
    /// <param name="cloud">False leaves MODBOT_CLOUD_PROXY_URL and MODBOT_CLOUD_API_KEY unset.</param>
    /// <param name="myUrl">MODBOT_MY_URL. Null leaves the project's own selector in the page.</param>
    public static async Task<LandingTestHost> StartAsync(
        bool built = true, bool privacy = false, bool cloud = true, string? myUrl = null)
    {
        var webRoot = Directory.CreateTempSubdirectory("modbot-landing-tests-");

        if (privacy)
            await File.WriteAllTextAsync(Path.Combine(webRoot.FullName, "privacy.html"), PrivacyHtml, Ct);

        if (built)
        {
            await File.WriteAllTextAsync(Path.Combine(webRoot.FullName, "index.html"), LandingHtml, Ct);
            await File.WriteAllTextAsync(Path.Combine(webRoot.FullName, "404.html"), NotFoundHtml, Ct);
            await File.WriteAllTextAsync(Path.Combine(webRoot.FullName, "rooms.html"), RoomsHtml, Ct);
            await File.WriteAllTextAsync(
                Path.Combine(webRoot.FullName, "favicon.svg"),
                "<svg xmlns=\"http://www.w3.org/2000/svg\"/>",
                Ct);
            Directory.CreateDirectory(Path.Combine(webRoot.FullName, "assets"));
            // Long enough that compression is worth doing.
            await File.WriteAllTextAsync(
                Path.Combine(webRoot.FullName, "assets", "app-test.js"),
                string.Concat(Enumerable.Repeat("export const modbot = 'moderation';\n", 200)),
                Ct);
        }

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = webRoot.FullName,
            WebRootPath = webRoot.FullName,
            EnvironmentName = "Testing",
        });

        builder.WebHost.UseTestServer();

        var time = new ManualTime(Start);
        builder.Services.AddSingleton<TimeProvider>(time);

        var environment = new LandingEnvironment(
            LandingEnvironment.DefaultPort,
            cloud ? new Uri(CloudUrl) : null,
            cloud ? CloudApiKey : null,
            myUrl ?? LandingEnvironment.DefaultMyUrl);

        LandingApp.AddServices(builder.Services, environment);

        // Modbot Cloud, stood in for. Nothing in these tests reaches the network.
        var fake = new FakeCloud();
        builder.Services.AddHttpClient(OpenRooms.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => fake);

        var app = builder.Build();
        LandingApp.MapEndpoints(app);
        await app.StartAsync(Ct);

        return new LandingTestHost(app, app.GetTestClient(), webRoot, time, fake);
    }

    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var request = new HttpRequestMessage(method, path);

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            request.Headers.TryAddWithoutValidation(name, value);

        return _client.SendAsync(request, Ct);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path);

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();

        try
        {
            _webRoot.Delete(recursive: true);
        }
        catch (IOException)
        {
            // A temp folder left behind is not a test failure.
        }
    }
}

public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Modbot Cloud's feed, stood in for: a canned answer and a record of what was asked.</summary>
public sealed class FakeCloud : HttpMessageHandler
{
    private readonly List<(Uri? Url, string? Authorization)> _asked = [];

    /// <summary>What the next read gets back.</summary>
    public string Body { get; set; } = "{\"groups\":[]}";

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>True makes the read fail the way an unreachable Cloud does.</summary>
    public bool Unreachable { get; set; }

    public IReadOnlyList<(Uri? Url, string? Authorization)> Asked
    {
        get
        {
            lock (_asked)
                return [.. _asked];
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_asked)
        {
            _asked.Add((
                request.RequestUri,
                request.Headers.TryGetValues("Authorization", out var values) ? string.Join(' ', values) : null));
        }

        if (Unreachable)
            throw new HttpRequestException("no route to Modbot Cloud");

        return Task.FromResult(new HttpResponseMessage(Status)
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json"),
        });
    }
}
