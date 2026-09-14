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
        app.MapGet("/instanceredirect", ([FromServices] SelectorPage page) => page.Serve());

        // The desktop client opens my.modbot.co/pair. The page picks a saved instance and sends the
        // browser on to that instance's own /pair.
        app.MapGet("/pair", ([FromServices] SelectorPage page) => page.Serve());

        return app;
    }
}
