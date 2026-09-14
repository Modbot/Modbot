using Microsoft.AspNetCore.Mvc;
using Modbot.My.Data;

namespace Modbot.My.Features.Health;

public static class HealthEndpoints
{
    public const string Live = "/health/live";
    public const string Ready = "/health/ready";

    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        // The process is up.
        app.MapGet(Live, () => Results.Ok());

        // The process is up and can reach its database. This is the one to point a deploy at.
        app.MapGet(Ready, async ([FromServices] MyContext db, CancellationToken ct) =>
            await db.Database.CanConnectAsync(ct)
                ? Results.Ok()
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

        return app;
    }
}
