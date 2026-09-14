using Microsoft.AspNetCore.Mvc;

namespace Modbot.My.Features.Pages;

/// <summary>
/// Routes the selector page handles in the browser. Each serves the same file, so a deep link works
/// on first load and not only after navigating from <c>/</c>.
/// </summary>
/// <remarks>
/// <c>/</c> is served by the static files middleware, and <c>/register</c> by the register page
/// feature, because it also notes the URL.
/// </remarks>
public static class PageEndpoints
{
    public static IEndpointRouteBuilder MapPages(this IEndpointRouteBuilder app)
    {
        // The one route that sends a browser to a page on a saved instance: /go?redir=/pair picks an
        // instance and opens its /pair. The desktop client and documentation links both use it.
        app.MapGet("/go", ([FromServices] SelectorPage page) => page.Serve());

        return app;
    }
}
