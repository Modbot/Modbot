using Microsoft.AspNetCore.Mvc;

namespace Modbot.Cloud.Features.Pages;

/// <summary>
/// The web app's routes. Each serves the same <c>index.html</c>, so a deep link works on first load.
/// Every other path is a 404.
/// </summary>
public static class PageEndpoints
{
    /// <summary>
    /// The pages a link in Cloud's mail can land on. <c>/verify</c> and <c>/reset-password</c> are
    /// where the account links land, so their spellings are fixed by <c>AccountMail</c>.
    /// </summary>
    public static readonly string[] AccountRoutes =
    [
        "/register",
        "/sign-in",
        "/account",
        "/verify",
        "/verify-email-change",
        "/forgot-password",
        "/reset-password",

        // Not an account page: the link at the foot of a Modbot mailing-list message, which needs no
        // account at all. Its spelling is fixed by the link stored against each subscriber.
        "/unsubscribe",
    ];

    public static IEndpointRouteBuilder MapPages(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", ([FromServices] AppPage page, HttpContext http) => page.Serve(http));
        app.MapGet("/admin", ([FromServices] AppPage page, HttpContext http) => page.Serve(http));
        app.MapGet("/admin/{**rest}", ([FromServices] AppPage page, HttpContext http) => page.Serve(http));

        // The account pages. Each is a real route rather than a fragment, because the links in
        // Cloud's mail land on them directly.
        foreach (var route in AccountRoutes)
            app.MapGet(route, ([FromServices] AppPage page, HttpContext http) => page.Serve(http));

        // Lowest priority, so it only answers what no other route claimed.
        app.MapFallback("{**path}", NotFound);

        return app;
    }

    internal static IResult NotFound(HttpContext http) =>
        http.Request.Path.StartsWithSegments("/api")
            ? Results.Json(new { error = "Not found." }, statusCode: StatusCodes.Status404NotFound)
            : Results.NotFound();
}
