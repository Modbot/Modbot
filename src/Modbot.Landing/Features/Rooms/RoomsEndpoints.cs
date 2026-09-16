using Microsoft.AspNetCore.Mvc;

namespace Modbot.Landing.Features.Rooms;

/// <summary>
/// <c>/api/rooms</c>: the groups using Modbot and the rooms they have open, for the rooms page.
/// </summary>
/// <remarks>
/// Open, because everything it serves is open: group names, pictures and join links that were
/// reported for exactly this purpose. The Cloud key stays on this server (see
/// <see cref="OpenRooms"/>).
/// </remarks>
public static class RoomsEndpoints
{
    public const string Path = "/api/rooms";

    public static IEndpointRouteBuilder MapRooms(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(Path, async (
            [FromServices] OpenRooms rooms,
            [FromServices] TimeProvider time,
            HttpContext http,
            CancellationToken ct) =>
        {
            var json = await rooms.ReadAsync(time, ct);

            // The same short life the server keeps it for, so a visitor who moves between pages
            // does not ask again and a shared cache never holds it longer than the page is true.
            http.Response.Headers.CacheControl =
                $"public, max-age={(int)OpenRooms.Freshness.TotalSeconds}";

            return Results.Content(json, "application/json; charset=utf-8");
        });

        return app;
    }
}
