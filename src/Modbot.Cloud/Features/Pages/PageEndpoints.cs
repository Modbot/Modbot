using Microsoft.AspNetCore.Mvc;

namespace Modbot.Cloud.Features.Pages;

/// <summary>
/// The web app's routes. Each serves the same <c>index.html</c>, so a deep link works on first load.
/// Every other path is a 404.
/// </summary>
public static class PageEndpoints
{
    public static IEndpointRouteBuilder MapPages(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", ([FromServices] AppPage page, HttpContext http) => page.Serve(http));
        app.MapGet("/admin", ([FromServices] AppPage page, HttpContext http) => page.Serve(http));
        app.MapGet("/admin/{**rest}", ([FromServices] AppPage page, HttpContext http) => page.Serve(http));

        // Lowest priority, so it only answers what no other route claimed.
        app.MapFallback("{**path}", NotFound);

        return app;
    }

    internal static IResult NotFound(HttpContext http) =>
        http.Request.Path.StartsWithSegments("/api")
            ? Results.Json(new { error = "Not found." }, statusCode: StatusCodes.Status404NotFound)
            : Results.NotFound();
}
