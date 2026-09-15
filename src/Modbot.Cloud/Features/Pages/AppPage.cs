namespace Modbot.Cloud.Features.Pages;

/// <summary>
/// The web app's <c>index.html</c>, built by <c>src/Modbot.Cloud/Web</c> into <c>wwwroot</c>.
/// </summary>
public sealed class AppPage(IWebHostEnvironment environment)
{
    private string? _html;

    public IResult Serve(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        // Read once found. Not remembered while missing, so a build that lands after start-up is
        // picked up.
        if (_html is null)
        {
            var root = environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");
            var path = Path.Combine(root, "index.html");

            if (!File.Exists(path))
                return Results.Text("The web app has not been built.", statusCode: StatusCodes.Status503ServiceUnavailable);

            _html = File.ReadAllText(path);
        }

        // The page names the hashed script and style files of one build, so it must not outlive it.
        http.Response.Headers.CacheControl = "no-cache";
        return Results.Content(_html, "text/html; charset=utf-8");
    }
}
