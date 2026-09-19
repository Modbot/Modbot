using System.Collections.Concurrent;

namespace Modbot.My.Features.Pages;

/// <summary>
/// The web app's built pages, from <c>src/Modbot.My.Web</c> into <c>wwwroot</c>.
/// </summary>
/// <remarks>
/// One file per route. They load the same app and differ only in the head: each carries the title
/// and the link preview tags for its own page, which a chat app reads without running any script.
/// </remarks>
public sealed class AppPage(IWebHostEnvironment environment)
{
    /// <summary>The built file each route is served from.</summary>
    public const string Home = "index.html";
    public const string Register = "register.html";
    public const string Go = "go.html";

    private readonly ConcurrentDictionary<string, string> _pages = new(StringComparer.Ordinal);

    public IResult Serve(HttpContext http, string file = Home)
    {
        ArgumentNullException.ThrowIfNull(http);

        // Read once found. Not remembered while missing, so a build that lands after start-up is
        // picked up.
        if (!_pages.TryGetValue(file, out var html))
        {
            var root = environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");
            var path = Path.Combine(root, file);

            if (!File.Exists(path))
                return Results.Text("The web app has not been built.", statusCode: StatusCodes.Status503ServiceUnavailable);

            html = File.ReadAllText(path);
            _pages[file] = html;
        }

        // The page names the hashed script and style files of one build, so it must not outlive it.
        http.Response.Headers.CacheControl = "no-cache";
        return Results.Content(html, "text/html; charset=utf-8");
    }
}
