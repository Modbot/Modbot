namespace Modbot.My.Features.Health;

/// <summary>
/// Liveness and readiness.
/// </summary>
/// <remarks>
/// Both answer as long as the process is up. my.modbot.co has no database to check, and a Modbot
/// Cloud that cannot be reached is a page with fewer instances on it — not a service that should be
/// taken out of rotation, which is what a failing readiness check would do.
/// </remarks>
public static class HealthEndpoints
{
    public const string Live = "/health/live";
    public const string Ready = "/health/ready";

    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(Live, () => Results.Ok());
        app.MapGet(Ready, () => Results.Ok());

        return app;
    }
}
