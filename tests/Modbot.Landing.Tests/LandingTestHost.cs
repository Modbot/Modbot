using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

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
        + "<body><main>Moderation for VRChat groups</main></body></html>";

    public const string NotFoundHtml =
        "<!doctype html><html><head><title>Not found</title></head><body><main>Nothing here</main></body></html>";

    public const string PrivacyHtml =
        "<!doctype html><html><head><title>Privacy policy</title></head><body><article>What we keep</article></body></html>";

    public const string AssetPath = "/assets/app-test.js";

    private readonly WebApplication _app;
    private readonly HttpClient _client;
    private readonly DirectoryInfo _webRoot;

    private LandingTestHost(WebApplication app, HttpClient client, DirectoryInfo webRoot)
    {
        _app = app;
        _client = client;
        _webRoot = webRoot;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <param name="built">False starts a server whose web root holds nothing, as before a build.</param>
    /// <param name="privacy">True adds the privacy page, as a build does once PRIVACY_POLICY.md exists.</param>
    public static async Task<LandingTestHost> StartAsync(bool built = true, bool privacy = false)
    {
        var webRoot = Directory.CreateTempSubdirectory("modbot-landing-tests-");

        if (privacy)
            await File.WriteAllTextAsync(Path.Combine(webRoot.FullName, "privacy.html"), PrivacyHtml, Ct);

        if (built)
        {
            await File.WriteAllTextAsync(Path.Combine(webRoot.FullName, "index.html"), LandingHtml, Ct);
            await File.WriteAllTextAsync(Path.Combine(webRoot.FullName, "404.html"), NotFoundHtml, Ct);
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

        LandingApp.AddServices(builder.Services);

        var app = builder.Build();
        LandingApp.MapEndpoints(app);
        await app.StartAsync(Ct);

        return new LandingTestHost(app, app.GetTestClient(), webRoot);
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
