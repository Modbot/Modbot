using Microsoft.AspNetCore.Mvc;
using Modbot.My.Cloud;
using Modbot.My.Common;

namespace Modbot.My.Features.Pages;

/// <summary>
/// The web app's routes. Each serves the same <c>index.html</c>, so a deep link works on first load.
/// </summary>
/// <remarks>
/// <para>
/// Only the app's real routes serve it. Every other path is a 404, so the retired <c>/pair</c> and
/// <c>/instanceredirect</c> stay dead instead of coming back as pages that happen to render;
/// <c>/go?redir=</c> is the one redirect route (central services spec 2.2).
/// </para>
/// <para>
/// <c>/admin</c> is not here any more. The registry moved to Modbot Cloud on 2026-09-16 and its
/// admin area went with it, to <c>cloud.modbot.co/admin</c> (spec 4.4).
/// </para>
/// <para>
/// <c>/</c>, <c>/register</c> and <c>/go</c> also note an instance address carried in <c>url</c> as
/// the page is served. The app notes it again once it renders, so a page the browser took from its
/// cache is still recorded; Cloud counts the two as one visit. The page does not wait for that
/// note: a Cloud that hangs would otherwise hold the page for as long as the call takes to give up,
/// and the app's own note is the one that is kept and sent again until Cloud takes it.
/// </para>
/// </remarks>
public static class PageEndpoints
{
    public static IEndpointRouteBuilder MapPages(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/", ServeAndRecord);
        app.MapGet("/register", ServeAndRecord);
        app.MapGet("/go", ServeAndRecord);

        // Lowest priority, so it only answers what no other route claimed.
        app.MapFallback("{**path}", NotFound);

        return app;
    }

    internal static IResult ServeAndRecord(
        [FromQuery] string? url,
        [FromServices] CloudClient cloud,
        [FromServices] SiteLimits limits,
        [FromServices] AppPage page,
        HttpContext http)
    {
        if (InstanceUrl.TryNormalise(url, out var origin))
        {
            var address = ClientAddress.From(http)?.ToString();

            // Over the limit, the page is still served. Somebody who reloads too often loses a count,
            // not the page they came for.
            if (limits.Saves.TryTake(address ?? "unknown") is null)
                cloud.RecordVisitInBackground(address, origin);
        }

        return page.Serve(http);
    }

    internal static IResult NotFound(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        return http.Request.Path.StartsWithSegments("/api")
            ? Results.Json(new { error = "Not found." }, statusCode: StatusCodes.Status404NotFound)
            : Results.NotFound();
    }
}
