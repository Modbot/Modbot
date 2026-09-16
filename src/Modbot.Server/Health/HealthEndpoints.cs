using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Modbot.Server.Health;

/// <summary>
/// The two probes a hosting platform needs: is the process alive, and can it serve.
/// </summary>
public static class HealthEndpoints
{
    public const string Live = "/health/live";
    public const string Ready = "/health/ready";

    public static IEndpointRouteBuilder MapModbotHealthChecks(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Liveness runs no checks at all. If it ran the database check it would tell a platform to
        // kill and restart Modbot every time Postgres restarts -- which is exactly when an
        // operator wants the UI up to read the error.
        app.MapHealthChecks(Live, new HealthCheckOptions { Predicate = _ => false });

        app.MapHealthChecks(Ready, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(DatabaseHealthCheck.ReadyTag),
            ResponseWriter = WriteResponseAsync,
        });

        return app;
    }

    /// <summary>
    /// Names the failing dependency. The default writer emits the word "Unhealthy" and nothing
    /// else, which does not distinguish "still starting" from "misconfigured".
    /// </summary>
    private static Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new
                {
                    status = entry.Value.Status.ToString(),
                    description = entry.Value.Description ?? entry.Value.Exception?.Message,
                }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}
