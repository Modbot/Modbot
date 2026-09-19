using Microsoft.AspNetCore.Mvc;
using Modbot.Landing.Configuration;

namespace Modbot.Landing.Features.Pages;

/// <summary>
/// <c>/</c> serves the landing page and each address in <see cref="Pages"/> serves the page built
/// for it, including <c>/instances</c> and the privacy policy. <c>/discord</c> and <c>/github</c>
/// send people to the addresses the server was given. Every other path no file claimed is a 404,
/// with the built not-found page for anything a person might have typed.
/// </summary>
public static class PageEndpoints
{
    /// <summary>The address people type, and the file the build wrote for it.</summary>
    private static readonly (string Path, string File)[] Pages =
    [
        ("/features", BuiltPages.FeaturesFile),
        ("/self-host", BuiltPages.SelfHostFile),
        ("/about", BuiltPages.AboutFile),
        ("/license", BuiltPages.LicenseFile),
        // The groups using Modbot and the instances they have open right now.
        ("/instances", BuiltPages.InstancesFile),
        // Built only once PRIVACY_POLICY.md exists at the repository root. Until then this is the
        // same 404 as any unknown page, and the footer does not link to it.
        ("/privacy", BuiltPages.PrivacyFile),
    ];

    public static IEndpointRouteBuilder MapPages(this IEndpointRouteBuilder app)
    {
        // HEAD as well, because link-preview crawlers check a URL before fetching it.
        app.MapMethods("/", [HttpMethods.Get, HttpMethods.Head],
            ([FromServices] BuiltPages pages, HttpContext http) => Serve(pages, http));

        foreach (var (path, file) in Pages)
            app.MapMethods(path, [HttpMethods.Get, HttpMethods.Head],
                ([FromServices] BuiltPages pages, HttpContext http) =>
                    pages.Find(file) is { } page ? Html(page, http, StatusCodes.Status200OK) : NotFound(pages, http));

        // The Discord mark is on every page and the pages are built once, so it cannot be hidden
        // when there is no address. Without one this answers with a page that says so, rather than
        // a not-found page the person who clicked cannot explain.
        app.MapMethods("/discord", [HttpMethods.Get, HttpMethods.Head],
            ([FromServices] LandingEnvironment environment, [FromServices] BuiltPages pages, HttpContext http) =>
                Send(environment.DiscordUrl, pages, http, BuiltPages.NoDiscordFile));

        app.MapMethods("/github", [HttpMethods.Get, HttpMethods.Head],
            ([FromServices] LandingEnvironment environment, [FromServices] BuiltPages pages, HttpContext http) =>
                Send(environment.GithubUrl, pages, http));

        // The page's address until 2026-09-17. Other sites link it, so it stays as a redirect.
        app.MapMethods("/rooms", [HttpMethods.Get, HttpMethods.Head],
            () => Results.Redirect("/instances", permanent: true));

        // Lowest priority, so it only answers what no other route claimed.
        app.MapFallback(([FromServices] BuiltPages pages, HttpContext http) => NotFound(pages, http));

        return app;
    }

    internal static IResult Serve(BuiltPages pages, HttpContext http)
    {
        if (pages.Find(BuiltPages.LandingFile) is not { } page)
            return Results.Text("The page has not been built.", statusCode: StatusCodes.Status503ServiceUnavailable);

        return Html(page, http, StatusCodes.Status200OK);
    }

    internal static IResult NotFound(BuiltPages pages, HttpContext http)
    {
        // A missing script, image or font gets a bare 404: an HTML body there is never what the
        // browser asked for. A path with no extension is a page somebody typed or followed.
        var wantsPage = (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
            && !Path.HasExtension(http.Request.Path.Value);

        if (wantsPage && pages.Find(BuiltPages.NotFoundFile) is { } page)
            return Html(page, http, StatusCodes.Status404NotFound);

        return Results.NotFound();
    }

    /// <summary>
    /// A short link out. Temporary, and never cached: a Discord invite is replaced from time to time
    /// and the next visitor must get the new one.
    /// </summary>
    /// <param name="instead">
    /// The page to serve when there is no address, for a link the site shows whether or not one was
    /// given. Without it, and without an address, this is the same 404 as any unknown page.
    /// </param>
    private static IResult Send(string? url, BuiltPages pages, HttpContext http, string? instead = null)
    {
        if (url is not null)
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Redirect(url);
        }

        if (instead is not null && pages.Find(instead) is { } page)
            return Html(page, http, StatusCodes.Status200OK);

        return NotFound(pages, http);
    }

    private static IResult Html(BuiltPage page, HttpContext http, int status)
    {
        // The page names the hashed script and style files of one build, so it must not outlive it.
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.ContentSecurityPolicy = page.ContentSecurityPolicy;
        return Results.Content(page.Html, "text/html; charset=utf-8", statusCode: status);
    }
}
