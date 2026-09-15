using Microsoft.AspNetCore.Mvc;
using Modbot.Cloud.Data;
using Modbot.Cloud.Engine;

namespace Modbot.Cloud.Features.Health;

public static class HealthEndpoints
{
    public const string Live = "/health/live";
    public const string Ready = "/health/ready";

    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        // The process is up.
        app.MapGet(Live, () => Results.Ok());

        // The process is up and can reach both databases. Either one down means batches or
        // sign-ins fail, so either one down is not ready. This is the one to point a deploy at.
        app.MapGet(Ready, async ([FromServices] CloudContext cloud, [FromServices] EngineContext engine, CancellationToken ct) =>
            await cloud.Database.CanConnectAsync(ct) && await engine.Database.CanConnectAsync(ct)
                ? Results.Ok()
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

        return app;
    }
}
