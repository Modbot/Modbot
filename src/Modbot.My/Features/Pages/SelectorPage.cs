namespace Modbot.My.Features.Pages;

/// <summary>
/// The selector: one static page that handles <c>/</c>, <c>/register</c>, <c>/instanceredirect</c>
/// and <c>/pair</c> in the browser. Read from disk once.
/// </summary>
public sealed class SelectorPage(IWebHostEnvironment environment)
{
    private readonly Lazy<string> _html = new(() =>
        File.ReadAllText(Path.Combine(environment.WebRootPath, "index.html")));

    public IResult Serve() => Results.Content(_html.Value, "text/html; charset=utf-8");
}
