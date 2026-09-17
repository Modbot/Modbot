using Microsoft.AspNetCore.Mvc;

namespace Modbot.Landing.Features.Instances;

/// <summary>
/// <c>/api/instances</c>: the groups using Modbot and the instances they have open, for the instances page.
/// </summary>
/// <remarks>
/// Open, because everything it serves is open: group names, pictures and join links that were
/// reported for exactly this purpose. The Cloud key stays on this server (see
/// <see cref="OpenInstances"/>).
/// </remarks>
public static class InstancesEndpoints
{
    public const string Path = "/api/instances";

    public static IEndpointRouteBuilder MapInstances(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(Path, async (
            [FromServices] OpenInstances instances,
            [FromServices] TimeProvider time,
            HttpContext http,
            CancellationToken ct) =>
        {
            var json = await instances.ReadAsync(time, ct);

            // The same short life the server keeps it for, so a visitor who moves between pages
            // does not ask again and a shared cache never holds it longer than the page is true.
            http.Response.Headers.CacheControl =
                $"public, max-age={(int)OpenInstances.Freshness.TotalSeconds}";

            return Results.Content(json, "application/json; charset=utf-8");
        });

        return app;
    }
}
