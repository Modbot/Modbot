using Microsoft.AspNetCore.Routing;

namespace Modbot.Api.Features.Evidence;

/// <summary>
/// The whole evidence HTTP surface: the settings screen, the three-phase upload, and serving.
/// </summary>
/// <remarks>
/// <para>
/// One public entry point, deliberately. The three groups are separate files because they are
/// separate concerns, but nothing outside this class can map one of them on its own — a route
/// registered twice surfaces as an ambiguous match at <em>request</em> time rather than at startup,
/// so the symptom is every test failing with nothing pointing at the cause.
/// </para>
/// <para>
/// <strong>Every parameter these endpoints take is explicitly attributed.</strong> Minimal APIs
/// infer an unattributed concrete type as the request body, and on a GET that does not merely
/// misbehave: it throws while the route is being mapped and takes every other endpoint in the host
/// down with it.
/// </para>
/// </remarks>
public static class EvidenceEndpoints
{
    /// <summary>Maps everything. Call this once, from the API surface.</summary>
    public static IEndpointRouteBuilder MapEvidence(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapEvidenceSettings();
        app.MapEvidenceUploads();
        app.MapEvidenceObjects();

        return app;
    }
}
