using Microsoft.AspNetCore.Mvc;

namespace Modbot.Landing.Features.Pages;

/// <summary>
/// <c>/</c> serves the landing page and <c>/privacy</c> the privacy policy. Every other path no file
/// claimed is a 404, with the built not-found page for anything a person might have typed.
/// </summary>
public static class PageEndpoints
{
    public static IEndpointRouteBuilder MapPages(this IEndpointRouteBuilder app)
    {
        // HEAD as well, because link-preview crawlers check a URL before fetching it.
        app.MapMethods("/", [HttpMethods.Get, HttpMethods.Head],
            ([FromServices] BuiltPages pages, HttpContext http) => Serve(pages, http));

        // Built only once PRIVACY_POLICY.md exists at the repository root. Until then this is the
        // same 404 as any unknown page, and the footer does not link to it.
        app.MapMethods("/privacy", [HttpMethods.Get, HttpMethods.Head],
            ([FromServices] BuiltPages pages, HttpContext http) =>
                pages.Find(BuiltPages.PrivacyFile) is { } policy
                    ? Html(policy, http, StatusCodes.Status200OK)
                    : NotFound(pages, http));

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

    private static IResult Html(BuiltPage page, HttpContext http, int status)
    {
        // The page names the hashed script and style files of one build, so it must not outlive it.
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.ContentSecurityPolicy = page.ContentSecurityPolicy;
        return Results.Content(page.Html, "text/html; charset=utf-8", statusCode: status);
    }
}
