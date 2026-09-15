using Microsoft.AspNetCore.Mvc;
using Modbot.Landing.Features.Pages;

namespace Modbot.Landing.Features.Health;

public static class HealthEndpoints
{
    public const string Live = "/health/live";
    public const string Ready = "/health/ready";

    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        // The process is up.
        app.MapGet(Live, () => Results.Ok());

        // The process is up and has a page to serve. This is the one to point a deploy at: an image
        // built without the web stage starts fine and would otherwise pass for healthy.
        app.MapGet(Ready, ([FromServices] BuiltPages pages) =>
            pages.Find(BuiltPages.LandingFile) is not null
                ? Results.Ok()
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

        return app;
    }
}
