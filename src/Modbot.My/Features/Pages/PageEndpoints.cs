using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.My.Common;
using Modbot.My.Data;
using Modbot.My.Features.Visits;
using Npgsql;

namespace Modbot.My.Features.Pages;

/// <summary>
/// The web app's routes. Each serves the same <c>index.html</c>, so a deep link works on first load.
/// </summary>
/// <remarks>
/// <para>
/// Only the app's real routes serve it. Every other path is a 404, so the retired
/// <c>/pair</c> and <c>/instanceredirect</c> stay dead instead of coming back as pages that happen
/// to render; <c>/go?redir=</c> is the one redirect route (central services spec 2.2).
/// </para>
/// <para>
/// <c>/</c>, <c>/register</c> and <c>/go</c> also record an instance URL carried in <c>url</c> as the
/// page is served. The app records it again once it renders; see <see cref="InstanceVisits"/>.
/// </para>
/// </remarks>
public static class PageEndpoints
{
    public static IEndpointRouteBuilder MapPages(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", ServeAndRecordAsync);
        app.MapGet("/register", ServeAndRecordAsync);
        app.MapGet("/go", ServeAndRecordAsync);
        app.MapGet("/admin", ([FromServices] AppPage page, HttpContext http) => page.Serve(http));
        app.MapGet("/admin/{**rest}", ([FromServices] AppPage page, HttpContext http) => page.Serve(http));

        // Lowest priority, so it only answers what no other route claimed.
        app.MapFallback("{**path}", NotFound);

        return app;
    }

    internal static async Task<IResult> ServeAndRecordAsync(
        [FromQuery] string? url,
        [FromServices] MyContext db,
        [FromServices] TimeProvider time,
        [FromServices] AppPage page,
        [FromServices] ILoggerFactory logs,
        HttpContext http,
        CancellationToken ct)
    {
        if (InstanceUrl.TryNormalise(url, out var origin))
        {
            try
            {
                await InstanceVisits.RecordAsync(db, ClientAddress.From(http), origin, time.GetUtcNow(), ct);
            }
            catch (Exception e) when (e is DbUpdateException or NpgsqlException)
            {
                // The page still has to work: the browser saves the instance itself, and the app
                // records it again after rendering.
                logs.CreateLogger(typeof(PageEndpoints))
                    .LogWarning(e, "Could not record a page visit for {InstanceUrl}", origin);
            }
        }

        return page.Serve(http);
    }

    internal static IResult NotFound(HttpContext http) =>
        http.Request.Path.StartsWithSegments("/api")
            ? Results.Json(new { error = "Not found." }, statusCode: StatusCodes.Status404NotFound)
            : Results.NotFound();
}
